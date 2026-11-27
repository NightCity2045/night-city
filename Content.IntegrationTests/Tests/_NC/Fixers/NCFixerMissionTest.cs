// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.Fixers;
using Content.Server._NC.Identity;
using Content.Shared._NC.Fixers;
using Content.Shared._NC.Identity;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Stacks;
using Content.Shared.Storage.Components;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Fixers;

[TestFixture]
[TestOf(typeof(NCFixerMissionSystem))]
[NonParallelizable]
public sealed class NCFixerMissionTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
        Dirty = true,
    };

    [Test]
    public async Task MissionPrototypesAreConsistent()
    {
        await Server.WaitAssertion(() =>
        {
            foreach (var mission in SProtoMan.EnumeratePrototypes<NCFixerMissionPrototype>())
            {
                Assert.That(mission.Reward, Is.GreaterThan(0), mission.ID);
                Assert.That(SProtoMan.HasIndex(mission.Fixer), mission.ID);
                if (mission.Kind == NCFixerMissionKind.Collection)
                    Assert.That(mission.Requirements, Is.Not.Empty, mission.ID);
                else if (mission.TargetDepartments.Count == 0)
                    Assert.That(mission.Item, Is.Not.Null, mission.ID);
            }
        });
    }

    private (EntityUid Worker, int Profile) SpawnWorker(EntityCoordinates coords, int profile)
    {
        var minds = Server.System<SharedMindSystem>();
        var worker = SEntMan.SpawnEntity("MobHuman", coords);
        SEntMan.EnsureComponent<MindContainerComponent>(worker);
        var mind = minds.CreateMind(null).Owner;
        minds.TransferTo(mind, worker);
        SEntMan.EnsureComponent<NCCharacterIdentityComponent>(mind).CharacterId = new NCCharacterId(profile);
        return (worker, profile);
    }

    /// <summary>
    /// A delivery: the package appears at the objective marker, goes into the fixer's dead drop, and the fixer
    /// pays cash into the drop and raises the taker's street reputation.
    /// </summary>
    [Test]
    public async Task DeliveryPaysIntoTheDeadDrop()
    {
        var missions = Server.System<NCFixerMissionSystem>();
        var reputation = Server.System<NCStreetReputationSystem>();
        var map = await Pair.CreateTestMap();
        EntityUid drop = default;
        EntityUid package = default;
        var profile = 0;

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var fixerUid = SEntMan.SpawnEntity("NCFixerAlpha", coords);
            drop = SEntMan.SpawnEntity("NCFixerDeadDropAlpha", coords.Offset(new System.Numerics.Vector2(1, 0)));
            SEntMan.SpawnEntity("NCMissionMarkerObjectiveNeutral", coords.Offset(new System.Numerics.Vector2(3, 0)));
            (var worker, profile) = SpawnWorker(coords, 900001);
            var fixer = new Entity<NCFixerComponent>(fixerUid, SEntMan.GetComponent<NCFixerComponent>(fixerUid));

            Assert.That(missions.TrySetOffer("NCFixerAlpha", 0, "NCFixerAlphaDelivery"), Is.True);
            Assert.That(missions.TryAccept(fixer, 0, worker), Is.True);
            Assert.That(missions.TryAccept(fixer, 1, worker), Is.False, "One job at a time.");

            package = missions.GetMissionEntities(profile).First();
            Assert.That(SEntMan.GetComponent<MetaDataComponent>(package).EntityPrototype?.ID, Is.EqualTo("NCMissionPackage"));
            Assert.That(Server.System<SharedEntityStorageSystem>().Insert(package, drop), Is.True);
        });

        await Pair.RunSeconds(2);

        await Server.WaitAssertion(() =>
        {
            Assert.That(missions.TryGetActiveMission(profile, out _, out _), Is.False, "The delivery is complete.");
            Assert.That(reputation.GetReputation(profile), Is.EqualTo(2));
            var contents = SEntMan.GetComponent<EntityStorageComponent>(drop).Contents.ContainedEntities;
            var cash = contents.Where(uid => SEntMan.TryGetComponent<StackComponent>(uid, out var stack) && stack.StackTypeId == "Credit")
                .Sum(uid => SEntMan.GetComponent<StackComponent>(uid).Count);
            Assert.That(cash, Is.EqualTo(800), "The reward waits in the dead drop.");
            Assert.That(SEntMan.Deleted(package) || SEntMan.IsQueuedForDeletion(package), "The package is taken.");
        });
    }

    /// <summary>
    /// A sabotage behind a reputation gate: the target and its guards appear, wrecking the target completes the
    /// job, and an in-person fixer pays when the taker comes back.
    /// </summary>
    [Test]
    public async Task SabotagePaysInPerson()
    {
        var missions = Server.System<NCFixerMissionSystem>();
        var reputation = Server.System<NCStreetReputationSystem>();
        var map = await Pair.CreateTestMap();
        Entity<NCFixerComponent> fixer = default;
        EntityUid worker = default;
        var profile = 0;

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var fixerUid = SEntMan.SpawnEntity("NCFixerBeta", coords);
            SEntMan.SpawnEntity("NCMissionMarkerObjectiveSixthStreet", coords.Offset(new System.Numerics.Vector2(6, 0)));
            (worker, profile) = SpawnWorker(coords, 900002);
            fixer = new Entity<NCFixerComponent>(fixerUid, SEntMan.GetComponent<NCFixerComponent>(fixerUid));

            Assert.That(missions.TrySetOffer("NCFixerBeta", 0, "NCFixerBetaSabotage"), Is.True);
            Assert.That(missions.TryAccept(fixer, 0, worker), Is.False, "Not trusted without street reputation.");
            reputation.AddReputation(profile, 10);
            Assert.That(missions.TryAccept(fixer, 0, worker), Is.True);

            var spawned = missions.GetMissionEntities(profile);
            Assert.That(spawned.Count, Is.GreaterThan(1), "The target comes with guards.");
            SEntMan.DeleteEntity(spawned[0]);
        });

        await Pair.RunSeconds(2);

        await Server.WaitAssertion(() =>
        {
            Assert.That(missions.TryGetActiveMission(profile, out _, out var status), Is.True);
            Assert.That(status, Is.EqualTo(NCFixerMissionStatus.AwaitingPayment));
            Assert.That(missions.TryCollectPayment(fixer, worker), Is.True);
            Assert.That(missions.TryGetActiveMission(profile, out _, out _), Is.False);
            Assert.That(reputation.GetReputation(profile), Is.EqualTo(14));
        });
    }

    /// <summary>Guards warn an unarmed visitor and turn hostile, as a group, on someone holding a weapon.</summary>
    [Test]
    public async Task GuardsWarnFirstAndFightArmedVisitors()
    {
        var missions = Server.System<NCFixerMissionSystem>();
        var reputation = Server.System<NCStreetReputationSystem>();
        var hands = Server.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>();
        var map = await Pair.CreateTestMap();
        EntityUid visitor = default;
        EntityUid armed = default;
        List<EntityUid> guards = new();

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var fixerUid = SEntMan.SpawnEntity("NCFixerBeta", coords);
            SEntMan.SpawnEntity("NCMissionMarkerObjectiveSixthStreet", coords.Offset(new System.Numerics.Vector2(10, 0)));
            var (worker, profile) = SpawnWorker(coords, 900003);
            reputation.AddReputation(profile, 10);
            var fixer = new Entity<NCFixerComponent>(fixerUid, SEntMan.GetComponent<NCFixerComponent>(fixerUid));
            Assert.That(missions.TrySetOffer("NCFixerBeta", 0, "NCFixerBetaSabotage"), Is.True);
            Assert.That(missions.TryAccept(fixer, 0, worker), Is.True);
            guards = missions.GetMissionEntities(profile).Where(uid => SEntMan.HasComponent<NCMissionGuardComponent>(uid)).ToList();
            Assert.That(guards, Is.Not.Empty);

            var post = SEntMan.GetComponent<TransformComponent>(guards[0]).Coordinates;
            (visitor, _) = SpawnWorker(post.Offset(new System.Numerics.Vector2(0, 3)), 900004);
            (armed, _) = SpawnWorker(post.Offset(new System.Numerics.Vector2(0, -3)), 900005);
            var gun = SEntMan.SpawnEntity("NCWeaponPistolStreetmaster", SEntMan.GetComponent<TransformComponent>(armed).Coordinates);
            Assert.That(hands.TryPickupAnyHand(armed, gun), Is.True);
        });

        await Pair.RunSeconds(1);

        await Server.WaitAssertion(() =>
        {
            var guard = SEntMan.GetComponent<NCMissionGuardComponent>(guards[0]);
            Assert.That(guard.Warned.ContainsKey(visitor), "The unarmed visitor is warned.");
            Assert.That(guard.Hostile.Contains(visitor), Is.False, "Not attacked yet.");
            foreach (var uid in guards)
                Assert.That(SEntMan.GetComponent<NCMissionGuardComponent>(uid).Hostile.Contains(armed), "The whole group turns on the armed visitor.");
        });
    }

    /// <summary>Every defender of every encounter spawns with its gear.</summary>
    [Test]
    public async Task EncounterMobsSpawn()
    {
        var map = await Pair.CreateTestMap();
        await Server.WaitAssertion(() =>
        {
            foreach (var encounter in SProtoMan.EnumeratePrototypes<NCFixerEncounterPrototype>())
            {
                foreach (var entry in encounter.Entries)
                {
                    var mob = SEntMan.SpawnEntity(entry.Mob, map.GridCoords);
                    Assert.That(SEntMan.HasComponent<Content.Shared.NPC.Components.NpcFactionMemberComponent>(mob), $"{encounter.ID}: {entry.Mob}");
                    SEntMan.DeleteEntity(mob);
                }
            }
        });
    }
}
