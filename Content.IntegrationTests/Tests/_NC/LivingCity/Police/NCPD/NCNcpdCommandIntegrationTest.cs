// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Employment;
using Content.Server._NC.LivingCity.Police.NCPD;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Roles;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Police.NCPD;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(NCNcpdCommandSystem))]
public sealed class NCNcpdCommandIntegrationTest : GameTest
{
    private static readonly NCZoneId StationZone =
        new(Guid.Parse("a9c069f8-8558-493c-844b-b7bfe8775521"));
    private static readonly ProtoId<NCCityActorArchetypePrototype> ActorArchetype =
        new("NCLivingCityUtilityResident");

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task WorkforceDutyUnitCommandAndMaterializationRemainAuthoritative()
    {
        var server = Pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var actors = server.System<NCCityActorRegistrySystem>();
        var workforce = server.System<NCOrganizationWorkforceSystem>();
        var officers = server.System<NCNcpdWorkforceProjectionSystem>();
        var duty = server.System<NCNcpdDutySystem>();
        var units = server.System<NCNcpdUnitSystem>();
        var commands = server.System<NCNcpdCommandSystem>();
        var runtime = server.System<NCCityActorRuntimeIndexSystem>();

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world clock did not start.");

        // The same jobs exposed to players are reused for NPC personnel, but the department head remains player-only.
        await server.WaitAssertion(() =>
        {
            Assert.That(workforce.TryResolveNpcJob("NCPDOfficer", out var organization, out _, out var error),
                Is.True, error);
            Assert.That(organization, Is.EqualTo(new ProtoId<DepartmentPrototype>("NCPD")));
            Assert.That(workforce.TryResolveNpcJob("NCPDChief", out _, out _, out var chiefError), Is.False);
            Assert.That(chiefError, Does.Contain("reserved for players"));
        });

        NCActiveWorldTime now = default;
        await server.WaitPost(() => Assert.That(clock.TryGetCurrentTime(out _, out now), Is.True));
        var populationScope = Guid.NewGuid();
        var actorIds = new List<NCActorId>
        {
            await CreateWorkingOfficer(server, actors, populationScope, "sergeant", "NCPDSergeant", now),
            await CreateWorkingOfficer(server, actors, populationScope, "officer-a", "NCPDOfficer", now),
            await CreateWorkingOfficer(server, actors, populationScope, "officer-b", "NCPDOfficer", now),
            await CreateWorkingOfficer(server, actors, populationScope, "cadet", "NCPDCadet", now),
        };

        // Employment persistence publishes this same event after committing. Raising the public boundary directly
        // keeps this test focused on the NCPD projection while still using real persistent actor records.
        await server.WaitPost(() =>
        {
            server.EntMan.EventBus.RaiseEvent(EventSource.Local,
                new NCWorkforceAssignmentChangedEvent(actorIds[0], "NCPD", "NCPDSergeant"));
            server.EntMan.EventBus.RaiseEvent(EventSource.Local,
                new NCWorkforceAssignmentChangedEvent(actorIds[1], "NCPD", "NCPDOfficer"));
            server.EntMan.EventBus.RaiseEvent(EventSource.Local,
                new NCWorkforceAssignmentChangedEvent(actorIds[2], "NCPD", "NCPDOfficer"));
            server.EntMan.EventBus.RaiseEvent(EventSource.Local,
                new NCWorkforceAssignmentChangedEvent(actorIds[3], "NCPD", "NCPDCadet"));
        });

        await WaitUntil(server,
            () => officers.OfficerCount == 4 && duty.AvailableCount == 4 && units.UnitCount == 1,
            "Four working officers did not form one patrol with two officers held in reserve.");

        NCNcpdUnitSnapshot unit = default!;
        await server.WaitAssertion(() =>
        {
            var snapshots = new List<NCNcpdUnitSnapshot>();
            units.GetSnapshots(snapshots);
            Assert.That(snapshots, Has.Count.EqualTo(1));
            unit = snapshots[0];
            Assert.That(unit.Members, Has.Length.EqualTo(2));
            Assert.That(unit.Members, Does.Contain(actorIds[3]), "The cadet must receive a supervising patrol partner.");
        });

        var issuer = new NCNcpdIssuerContext(new NCCharacterId(1), "NCPDChief", "Player Chief", 1000);
        NCNcpdCommandRecord accepted = default!;
        await server.WaitAssertion(() =>
        {
            var chiefAuthority = prototypes.Index<NCNcpdCommandAuthorityPrototype>(
                "NCNcpdChiefCommandAuthority");
            var inspectorAuthority = prototypes.Index<NCNcpdCommandAuthorityPrototype>(
                "NCNcpdInspectorCommandAuthority");
            var policy = prototypes.Index<NCNcpdCommandPolicyPrototype>("NCDefaultNcpdCommandPolicy");

            Assert.That(commands.TryIssue(
                issuer,
                inspectorAuthority,
                policy,
                unit.UnitId,
                NCNcpdCommandKind.PatrolZone,
                StationZone,
                500,
                "Invalid inspector patrol order",
                out var unauthorizedError), Is.False);
            Assert.That(unauthorizedError, Does.Contain("cannot issue"));

            Assert.That(commands.TryIssue(
                issuer,
                chiefAuthority,
                policy,
                unit.UnitId,
                NCNcpdCommandKind.PatrolZone,
                StationZone,
                700,
                "Patrol the station perimeter",
                out var issueError), Is.True, issueError);
            Assert.That(commands.TryGetActive(unit.UnitId, out accepted), Is.True);
            Assert.That(accepted.Status, Is.EqualTo(NCNcpdCommandStatus.Accepted));
            Assert.That(accepted.TargetZone, Is.EqualTo(StationZone));

            Assert.That(commands.TryIssue(
                issuer,
                chiefAuthority,
                policy,
                unit.UnitId,
                NCNcpdCommandKind.HoldZone,
                StationZone,
                700,
                "Conflicting second order",
                out var duplicateError), Is.False);
            Assert.That(duplicateError, Does.Contain("already has an active order"));
        });
        await WaitUntil(server,
            () => unit.Members.All(actorId =>
                duty.TryGetState(actorId, out var state) && state.Status == NCNcpdDutyStatus.Assigned),
            "The accepted command was not projected into both duty records.");

        EntityUid physicalOfficer = default;
        await server.WaitAssertion(() =>
        {
            physicalOfficer = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            Assert.That(runtime.TryBind(unit.Members[0], physicalOfficer), Is.True);
            Assert.That(entities.HasComponent<NCNcpdOfficerComponent>(physicalOfficer), Is.True);
            Assert.That(entities.HasComponent<NCNcpdUnitMemberComponent>(physicalOfficer), Is.True);
            Assert.That(entities.TryGetComponent<NCNcpdAssignmentComponent>(physicalOfficer, out var assignment),
                Is.True);
            Assert.That(assignment!.CommandId, Is.EqualTo(accepted.CommandId));
            Assert.That(assignment.Kind, Is.EqualTo(NCNcpdCommandKind.PatrolZone));
            Assert.That(assignment.TargetZone, Is.EqualTo(StationZone));
        });

        await server.WaitAssertion(() =>
        {
            var sergeantAuthority = prototypes.Index<NCNcpdCommandAuthorityPrototype>(
                "NCNcpdSergeantCommandAuthority");
            var chiefAuthority = prototypes.Index<NCNcpdCommandAuthorityPrototype>(
                "NCNcpdChiefCommandAuthority");
            var policy = prototypes.Index<NCNcpdCommandPolicyPrototype>("NCDefaultNcpdCommandPolicy");
            var otherIssuer = new NCNcpdIssuerContext(new NCCharacterId(2), "NCPDSergeant", "Other Sergeant", 45);

            Assert.That(commands.TryCancel(
                otherIssuer,
                sergeantAuthority,
                policy,
                accepted.CommandId,
                "Attempted unauthorized stand-down",
                out var unauthorizedError), Is.False);
            Assert.That(unauthorizedError, Does.Contain("cannot cancel another"));

            Assert.That(commands.TryCancel(
                issuer,
                chiefAuthority,
                policy,
                accepted.CommandId,
                "Patrol completed",
                out var cancelError), Is.True, cancelError);
            Assert.That(commands.ActiveCommandCount, Is.Zero);
            Assert.That(entities.HasComponent<NCNcpdAssignmentComponent>(physicalOfficer), Is.False);
            Assert.That(entities.GetComponent<NCNcpdPoliceClaimComponent>(physicalOfficer).AssignmentId,
                Is.EqualTo(Guid.Empty));
            Assert.That(runtime.Unbind(physicalOfficer), Is.True);
            entities.DeleteEntity(physicalOfficer);
        });
        await WaitUntil(server,
            () => unit.Members.All(actorId =>
                duty.TryGetState(actorId, out var state) && state.Status == NCNcpdDutyStatus.Available),
            "Cancelling the command did not return the patrol to availability.");
    }

    private static async Task<NCActorId> CreateWorkingOfficer(
        RobustIntegrationTest.ServerIntegrationInstance server,
        NCCityActorRegistrySystem actors,
        Guid populationScope,
        string stableId,
        ProtoId<JobPrototype> job,
        NCActiveWorldTime now)
    {
        var created = await actors.CreateAsync(new NCCityActorCreateRequest(
            Guid.NewGuid(),
            populationScope,
            $"ncpd.integration.{stableId}",
            $"NCPD Integration {stableId}",
            [],
            ActorArchetype,
            StationZone,
            StationZone,
            Guid.NewGuid(),
            AuditSource: "integration-test:ncpd"));
        Assert.That(created.Status, Is.EqualTo(NCCityActorMutationStatus.Applied), created.Error);

        var working = await actors.SetLocationAsync(new NCCityActorLocationMutationRequest(
            Guid.NewGuid(),
            created.ActorId,
            created.AggregateVersion,
            StationZone,
            default,
            NCCityActorTravelPhase.Stationary,
            NCCityTravelMode.Pedestrian,
            null,
            "work",
            $"perform:{job.Id}",
            NCCityActorAvailability.Available,
            now.Advance(TimeSpan.FromHours(1)),
            Guid.NewGuid(),
            AuditSource: "integration-test:ncpd-duty",
            PresenceMode: NCCityActorPresenceMode.Public));
        Assert.That(working.Status, Is.EqualTo(NCCityActorMutationStatus.Applied), working.Error);
        return created.ActorId;
    }

    private static async Task WaitUntil(
        RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate,
        string failure,
        int attempts = 180)
    {
        for (var i = 0; i < attempts; i++)
        {
            await server.WaitRunTicks(1);
            var complete = false;
            await server.WaitPost(() => complete = predicate());
            if (complete)
                return;
        }

        Assert.Fail(failure);
    }
}
