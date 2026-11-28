// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.Dispatch;
using Content.Server._NC.Identity;
using Content.Server._NC.Police;
using Content.Server._NC.ZLevels.Core;
using Content.Shared._NC.Bank;
using Content.Shared._NC.City.Atlas;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.Dispatch;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Police;
using Content.Shared.CartridgeLoader;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Roles;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._NC.Dispatch;

[TestFixture]
[TestOf(typeof(NCDispatchSystem))]
[NonParallelizable]
public sealed class NCDispatchTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
        Dirty = true,
    };

    /// <summary>A test map placed in a Z-network, so people on it have city coordinates.</summary>
    private async Task<Robust.UnitTesting.Pool.TestMapData> CreateCityMap()
    {
        var map = await Pair.CreateTestMap();
        await Server.WaitPost(() =>
        {
            // Floor under the whole test area: in a Z-network people off the floor fall through.
            var maps = Server.System<SharedMapSystem>();
            var plating = Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId;
            for (var x = -4; x <= 26; x++)
            {
                for (var y = -4; y <= 4; y++)
                    maps.SetTile(map.Grid, new Vector2i(x, y), new Tile(plating));
            }

            var zLevels = Server.System<NCZLevelsSystem>();
            var network = zLevels.CreateMapNetwork();
            Assert.That(zLevels.TryAddMapsIntoNetwork(network, new Dictionary<EntityUid, int> { [map.MapUid] = 0 }), Is.True);
        });

        return map;
    }

    private EntityUid SpawnPerson(EntityCoordinates coords, int profile, string? job = null)
    {
        var minds = Server.System<SharedMindSystem>();
        var person = SEntMan.SpawnEntity("MobHuman", coords);
        SEntMan.EnsureComponent<MindContainerComponent>(person);
        var mind = minds.CreateMind(null).Owner;
        minds.TransferTo(mind, person);
        SEntMan.EnsureComponent<NCCharacterIdentityComponent>(mind).CharacterId = new NCCharacterId(profile);
        if (job != null)
            Server.System<SharedRoleSystem>().MindAddJobRole(mind, jobPrototype: job);
        return person;
    }

    [Test]
    public async Task ServicesPlansAndCoverageAreConsistent()
    {
        await Server.WaitAssertion(() =>
        {
            Assert.That(SProtoMan.HasIndex(NCDispatchSystem.PoliceService));
            Assert.That(SProtoMan.HasIndex(NCDispatchSystem.TraumaService));
            foreach (var service in SProtoMan.EnumeratePrototypes<NCEmergencyServicePrototype>())
            {
                Assert.That(SProtoMan.HasIndex(service.Department), service.ID);
                if (service.CallPrice > 0)
                    Assert.That(service.Account is { } account && SProtoMan.HasIndex(account), service.ID);
            }

            Assert.That(SProtoMan.Index(NCDispatchSystem.TraumaService).Account, Is.Not.Null,
                "Subscriptions are paid to the Trauma Team account.");

            foreach (var plan in SProtoMan.EnumeratePrototypes<NCTraumaPlanPrototype>())
            {
                Assert.That(plan.Price, Is.GreaterThan(0), plan.ID);
                Assert.That(plan.ResponseTime, Is.GreaterThan(TimeSpan.Zero), plan.ID);
            }

            var districts = SProtoMan.EnumeratePrototypes<NCCityAtlasPrototype>()
                .SelectMany(atlas => atlas.Districts)
                .Select(district => district.Id)
                .ToHashSet();
            foreach (var entry in SProtoMan.Index(NCPoliceCoveragePrototype.DefaultId).Districts)
                Assert.That(districts, Does.Contain(entry.District), "Coverage names a district of the city atlas.");
        });
    }

    /// <summary>Every PDA has the emergency program; NCPD and Trauma PDAs also have the dispatcher.</summary>
    [TestCase("PassengerPDA", false)]
    [TestCase("NCPDOfficerPDA", true)]
    [TestCase("TraumaTeamParamedicPDA", true)]
    [TestCase("TraumaTeamOperativePDA", true)]
    public async Task PdasCarryTheirPrograms(string pda, bool dispatcher)
    {
        var map = await CreateCityMap();
        await Server.WaitAssertion(() =>
        {
            var uid = SEntMan.SpawnEntity(pda, map.GridCoords);
            var container = Server.System<SharedContainerSystem>()
                .GetContainer(uid, CartridgeLoaderComponent.UnremovableContainerId);
            var programs = container.ContainedEntities
                .Select(program => SEntMan.GetComponent<MetaDataComponent>(program).EntityPrototype?.ID)
                .ToList();
            Assert.That(programs, Does.Contain("NCEmergencyCartridge"));
            Assert.That(programs.Contains("NCDispatchCartridge"), Is.EqualTo(dispatcher));

            var console = SEntMan.SpawnEntity("NCComputerPoliceMonitor", map.GridCoords);
            Assert.That(SEntMan.HasComponent<Content.Shared._NC.Police.Components.NCPoliceMonitorConsoleComponent>(console));
            SEntMan.DeleteEntity(console);
            SEntMan.DeleteEntity(uid);
        });
    }

    /// <summary>A resident calls NCPD, an officer takes the call, gets there and closes it.</summary>
    [Test]
    public async Task PoliceCallIsTakenReachedAndClosed()
    {
        var dispatch = Server.System<NCDispatchSystem>();
        var transform = Server.System<SharedTransformSystem>();
        var map = await CreateCityMap();
        EntityUid caller = default;
        EntityUid officer = default;
        var id = 0;

        await Server.WaitAssertion(() =>
        {
            caller = SpawnPerson(map.GridCoords, 900101);
            officer = SpawnPerson(map.GridCoords.Offset(new System.Numerics.Vector2(20, 0)), 900102, "NCPDOfficer");

            Assert.That(dispatch.TryCall(caller, NCDispatchSystem.PoliceService, "  they are shooting  "), Is.True);
            var incident = dispatch.GetCall(caller, NCDispatchSystem.PoliceService);
            Assert.That(incident, Is.Not.Null);
            id = incident!.Id;
            Assert.That(incident.Kind, Is.EqualTo(NCDispatchIncidentKind.Call));
            Assert.That(incident.Status, Is.EqualTo(NCDispatchStatus.Open));
            Assert.That(incident.Message, Is.EqualTo("they are shooting"));
            Assert.That(incident.Paid, Is.Zero, "Police calls are free.");

            Assert.That(dispatch.TryCall(caller, NCDispatchSystem.PoliceService, "again", quiet: true), Is.False,
                "One active call per service.");
            Assert.That(dispatch.TryAccept(caller, id, quiet: true), Is.False, "Only officers take police calls.");
            Assert.That(dispatch.TryAccept(officer, id), Is.True);
            Assert.That(incident.Status, Is.EqualTo(NCDispatchStatus.Accepted));
            Assert.That(incident.Responder, Is.EqualTo(officer));
        });

        await Pair.RunSeconds(2);
        await Server.WaitAssertion(() =>
            Assert.That(dispatch.GetIncident(id)!.Status, Is.EqualTo(NCDispatchStatus.Accepted), "Still on the way."));

        await Server.WaitPost(() => transform.SetCoordinates(officer, map.GridCoords.Offset(new System.Numerics.Vector2(1, 0))));
        await Pair.RunSeconds(2);

        await Server.WaitAssertion(() =>
        {
            Assert.That(dispatch.GetIncident(id)!.Status, Is.EqualTo(NCDispatchStatus.OnScene));
            Assert.That(dispatch.TryClose(officer, id), Is.True);
            Assert.That(dispatch.GetIncident(id), Is.Null);
            Assert.That(dispatch.GetCall(caller, NCDispatchSystem.PoliceService), Is.Null);
        });
    }

    /// <summary>Gunfire raises one shooting incident; more shots nearby add to it; officers' shots do not count.</summary>
    [Test]
    public async Task GunfireRaisesOneShootingIncident()
    {
        var dispatch = Server.System<NCDispatchSystem>();
        var map = await CreateCityMap();

        await Server.WaitAssertion(() =>
        {
            var shooter = SpawnPerson(map.GridCoords, 900111);
            var officer = SpawnPerson(map.GridCoords.Offset(new System.Numerics.Vector2(3, 0)), 900112, "NCPDOfficer");

            dispatch.ReportGunfire(officer);
            Assert.That(dispatch.GetIncidents().Where(i => i.Kind == NCDispatchIncidentKind.Shooting), Is.Empty);

            dispatch.ReportGunfire(shooter);
            dispatch.ReportGunfire(shooter);
            dispatch.ReportGunfire(officer);
            var shootings = dispatch.GetIncidents().Where(i => i.Kind == NCDispatchIncidentKind.Shooting).ToList();
            Assert.That(shootings, Has.Count.EqualTo(1));
            Assert.That(shootings[0].Service, Is.EqualTo(NCDispatchSystem.PoliceService));
            Assert.That(shootings[0].Shots, Is.EqualTo(2));
        });
    }

    /// <summary>The biomonitor of a subscriber calls Trauma Team by itself; nobody else's does.</summary>
    [Test]
    public async Task BiomonitorCallsTraumaForSubscribers()
    {
        var dispatch = Server.System<NCDispatchSystem>();
        var mobState = Server.System<MobStateSystem>();
        var map = await CreateCityMap();

        await Server.WaitAssertion(() =>
        {
            var patient = SpawnPerson(map.GridCoords, 900121);
            var bystander = SpawnPerson(map.GridCoords.Offset(new System.Numerics.Vector2(5, 0)), 900122);
            dispatch.SetPlan(900121, "Gold");
            Assert.That(dispatch.GetPlan(patient)?.ID, Is.EqualTo("Gold"));

            mobState.ChangeMobState(patient, MobState.Critical);
            mobState.ChangeMobState(bystander, MobState.Critical);

            var calls = dispatch.GetIncidents().Where(i => i.Service == NCDispatchSystem.TraumaService).ToList();
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls[0].Kind, Is.EqualTo(NCDispatchIncidentKind.Biomonitor));
            Assert.That(calls[0].Subject, Is.EqualTo(patient));
            Assert.That(calls[0].Plan?.Id, Is.EqualTo("Gold"));
            Assert.That(calls[0].Deadline, Is.EqualTo(calls[0].Created + SProtoMan.Index<NCTraumaPlanPrototype>("Gold").ResponseTime));

            mobState.ChangeMobState(patient, MobState.Dead);
            Assert.That(dispatch.GetIncidents().Count(i => i.Service == NCDispatchSystem.TraumaService), Is.EqualTo(1),
                "One open call per patient.");
            dispatch.SetPlan(900121, null);
        });
    }

    /// <summary>A one-off Trauma call is paid up front; without an account it cannot be made.</summary>
    [Test]
    public async Task TraumaCallNeedsMoneyWithoutSubscription()
    {
        var dispatch = Server.System<NCDispatchSystem>();
        var map = await CreateCityMap();

        await Server.WaitAssertion(() =>
        {
            var caller = SpawnPerson(map.GridCoords, 900131);
            var trauma = SProtoMan.Index(NCDispatchSystem.TraumaService);
            Assert.That(dispatch.GetCallPrice(caller, trauma), Is.EqualTo(trauma.CallPrice));
            Assert.That(dispatch.CanCall(caller, NCDispatchSystem.TraumaService, quiet: true), Is.False);

            dispatch.SetPlan(900131, "Silver");
            Assert.That(dispatch.GetCallPrice(caller, trauma), Is.Zero, "Subscribers call for free.");
            Assert.That(dispatch.TryCall(caller, NCDispatchSystem.TraumaService, string.Empty), Is.True);
            Assert.That(dispatch.GetCall(caller, NCDispatchSystem.TraumaService)!.Plan?.Id, Is.EqualTo("Silver"));
            dispatch.SetPlan(900131, null);
        });
    }

    /// <summary>The monitoring console only refreshes a person as often as their place's coverage allows.</summary>
    [Test]
    public async Task MonitorRefreshesByCoverage()
    {
        var monitor = Server.System<NCPoliceMonitorSystem>();
        var coordinates = Server.System<NCMapCoordinatesSystem>();
        var transform = Server.System<SharedTransformSystem>();
        var timing = Server.ResolveDependency<IGameTiming>();
        var map = await CreateCityMap();

        await Server.WaitAssertion(() =>
        {
            var person = SpawnPerson(map.GridCoords, 900141);
            Assert.That(coordinates.TryGetCoordinates(person, out var at), Is.True);
            var now = timing.CurTime;
            monitor.UpdateSightings(now);
            var first = monitor.GetContacts(at.NetworkId).Single(contact => contact.Name == SEntMan.GetComponent<MetaDataComponent>(person).EntityName);

            transform.SetCoordinates(person, map.GridCoords.Offset(new System.Numerics.Vector2(6, 0)));
            monitor.UpdateSightings(now + TimeSpan.FromSeconds(5));
            var stale = monitor.GetContacts(at.NetworkId).Single(contact => contact.Id == first.Id);
            Assert.That(stale.Position, Is.EqualTo(first.Position), "Streets outside districts refresh every 30 seconds.");

            monitor.UpdateSightings(now + TimeSpan.FromSeconds(31));
            var fresh = monitor.GetContacts(at.NetworkId).Single(contact => contact.Id == first.Id);
            Assert.That(fresh.Position, Is.Not.EqualTo(first.Position));
        });
    }
}
