// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.Fixers;
using Content.Server.NPC.HTN;
using Content.Shared._NC.Fixers;
using Content.Shared._NC.Identity;
using Content.Server._NC.Identity;
using Content.Shared.Damage.Systems;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Fixers;

[TestFixture]
[TestOf(typeof(NCAmbientDefenseSystem))]
[TestOf(typeof(NCMissionPatrolSystem))]
[NonParallelizable]
public sealed class NCAmbientDefenseTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
        Dirty = true,
    };

    private EntityUid SpawnVisitor(EntityCoordinates coords, int profile)
    {
        var minds = Server.System<SharedMindSystem>();
        var visitor = SEntMan.SpawnEntity("MobHuman", coords);
        SEntMan.EnsureComponent<MindContainerComponent>(visitor);
        var mind = minds.CreateMind(null).Owner;
        minds.TransferTo(mind, visitor);
        SEntMan.EnsureComponent<NCCharacterIdentityComponent>(mind).CharacterId = new NCCharacterId(profile);
        return visitor;
    }

    private void MoveTo(EntityUid uid, EntityCoordinates coords)
    {
        Server.System<SharedTransformSystem>().SetCoordinates(uid, coords);
    }

    [Test]
    public async Task AmbientAndPatrolPrototypesAreConsistent()
    {
        await Server.WaitAssertion(() =>
        {
            foreach (var owner in SProtoMan.EnumeratePrototypes<NCMissionOwnerPrototype>())
            {
                if (owner.Ambient is { } ambient)
                    Assert.That(SProtoMan.TryIndex(ambient, out var encounter) && encounter.Entries.Count > 0, owner.ID);
            }

            foreach (var patrol in SProtoMan.EnumeratePrototypes<NCMissionPatrolPrototype>())
            {
                Assert.That(patrol.Owners, Is.Not.Empty, patrol.ID);
                Assert.That(patrol.Owners.All(SProtoMan.HasIndex), patrol.ID);
                Assert.That(SProtoMan.TryIndex(patrol.Encounter, out var encounter) && encounter.Entries.Count > 0, patrol.ID);
                Assert.That(encounter!.Entries.Sum(e => e.Max), Is.InRange(1, 2), $"{patrol.ID}: patrols are one or two people.");
            }
        });
    }

    /// <summary>
    /// Nobody stands at a gang's place until someone walks up to it; then the gang's people come out at the guard
    /// posts, as a group of their own, and leave once nobody has been around for a while.
    /// </summary>
    [Test]
    public async Task DefendersComeOutForVisitorsAndLeave()
    {
        var ambient = Server.System<NCAmbientDefenseSystem>();
        var map = await Pair.CreateTestMap();
        EntityUid objective = default;
        EntityUid visitor = default;

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            objective = SEntMan.SpawnEntity("NCMissionMarkerObjectiveValentinos", coords);
            SEntMan.SpawnEntity("NCMissionMarkerGuardValentinos", coords.Offset(new Vector2(3, 0)));
            visitor = SpawnVisitor(coords.Offset(new Vector2(60, 0)), 910001);
        });

        await Pair.RunSeconds(3);
        await Server.WaitAssertion(() =>
        {
            Assert.That(ambient.GetDefenders(objective), Is.Empty, "Nobody is out while nobody is around.");
            MoveTo(visitor, map.GridCoords.Offset(new Vector2(6, 0)));
        });

        await Pair.RunSeconds(3);
        List<EntityUid> defenders = new();
        await Server.WaitAssertion(() =>
        {
            defenders = ambient.GetDefenders(objective).ToList();
            Assert.That(defenders, Is.Not.Empty, "The gang comes out for a visitor.");
            foreach (var uid in defenders)
            {
                Assert.That(SEntMan.GetComponent<NCMissionGuardComponent>(uid).Mission, Is.Negative, "A group without a mission.");
                // The test map has no air: keep them alive for the wait, so leaving is not mistaken for losing.
                Server.System<SharedGodmodeSystem>().EnableGodmode(uid);
            }

            MoveTo(visitor, map.GridCoords.Offset(new Vector2(200, 0)));
        });

        await Pair.RunSeconds(190);
        await Server.WaitAssertion(() =>
        {
            Assert.That(defenders.All(uid => !SEntMan.EntityExists(uid)), "They leave once nobody is around.");
            Assert.That(ambient.IsOnCooldown(objective), Is.False, "Leaving is not losing.");
        });
    }

    /// <summary>
    /// A beaten group keeps its place empty for a while, even with the visitor still there. NC medicine owns mob
    /// states, so the test takes the defenders out of the world instead of killing them.
    /// </summary>
    [Test]
    public async Task BeatenPlaceStaysEmpty()
    {
        var ambient = Server.System<NCAmbientDefenseSystem>();
        var map = await Pair.CreateTestMap();
        EntityUid objective = default;

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            objective = SEntMan.SpawnEntity("NCMissionMarkerObjectiveBozos", coords);
            SEntMan.SpawnEntity("NCMissionMarkerGuardBozos", coords.Offset(new Vector2(3, 0)));
            SpawnVisitor(coords.Offset(new Vector2(5, 0)), 910002);
        });

        await Pair.RunSeconds(3);
        await Server.WaitAssertion(() =>
        {
            var defenders = ambient.GetDefenders(objective).ToList();
            Assert.That(defenders, Is.Not.Empty);
            foreach (var uid in defenders)
                SEntMan.DeleteEntity(uid);
        });

        await Pair.RunSeconds(5);
        await Server.WaitAssertion(() =>
        {
            Assert.That(ambient.IsOnCooldown(objective), "The place rests after its people are beaten.");
            Assert.That(ambient.GetDefenders(objective), Is.Empty, "Nobody new comes out.");
        });
    }

    /// <summary>A mission taken for a place where people already stand makes them its defenders.</summary>
    [Test]
    public async Task MissionTakesOverThePeopleAlreadyThere()
    {
        var ambient = Server.System<NCAmbientDefenseSystem>();
        var missions = Server.System<NCFixerMissionSystem>();
        var reputation = Server.System<NCStreetReputationSystem>();
        var map = await Pair.CreateTestMap();
        EntityUid objective = default;
        EntityUid worker = default;
        EntityUid fixerUid = default;
        List<EntityUid> defenders = new();

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            objective = SEntMan.SpawnEntity("NCMissionMarkerObjectiveValentinos", coords.Offset(new Vector2(30, 0)));
            SEntMan.SpawnEntity("NCMissionMarkerGuardValentinos", coords.Offset(new Vector2(33, 0)));
            fixerUid = SEntMan.SpawnEntity("NCFixerVlad", coords);
            worker = SpawnVisitor(coords.Offset(new Vector2(34, 2)), 910003);
            reputation.AddReputation(910003, 40);
        });

        await Pair.RunSeconds(3);
        await Server.WaitAssertion(() =>
        {
            defenders = ambient.GetDefenders(objective).ToList();
            Assert.That(defenders, Is.Not.Empty);
            MoveTo(worker, map.GridCoords.Offset(new Vector2(1, 0)));
            var fixer = new Entity<NCFixerComponent>(fixerUid, SEntMan.GetComponent<NCFixerComponent>(fixerUid));
            Assert.That(missions.TrySetOffer("NCFixerVlad", 0, "NCFixerVladDebtorValentinos"), Is.True);
            Assert.That(missions.TryAccept(fixer, 0, worker), Is.True);

            var spawned = missions.GetMissionEntities(910003);
            foreach (var uid in defenders)
            {
                Assert.That(spawned, Does.Contain(uid), "The people at the place become the mission's defenders.");
                Assert.That(SEntMan.GetComponent<NCMissionGuardComponent>(uid).Mission, Is.Positive);
            }

            var guards = spawned.Count(uid => SEntMan.HasComponent<NCMissionGuardComponent>(uid));
            Assert.That(guards, Is.EqualTo(defenders.Count + 1), "No second group: the defenders plus the debtor.");
            Assert.That(ambient.GetDefenders(objective), Is.Empty);
        });
    }

    /// <summary>The Arasaka remnant does not warn anyone: whoever it sees is an enemy.</summary>
    [Test]
    public async Task ArasakaAttacksOnSight()
    {
        var ambient = Server.System<NCAmbientDefenseSystem>();
        var map = await Pair.CreateTestMap();
        EntityUid objective = default;
        EntityUid visitor = default;

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            objective = SEntMan.SpawnEntity("NCMissionMarkerObjectiveArasakaRemnant", coords);
            SEntMan.SpawnEntity("NCMissionMarkerGuardArasakaRemnant", coords.Offset(new Vector2(2, 0)));
            visitor = SpawnVisitor(coords.Offset(new Vector2(4, 0)), 910004);
        });

        await Pair.RunSeconds(3);
        await Server.WaitAssertion(() =>
        {
            var guards = ambient.GetDefenders(objective)
                .Where(uid => SEntMan.HasComponent<NCMissionGuardComponent>(uid))
                .Select(uid => SEntMan.GetComponent<NCMissionGuardComponent>(uid))
                .ToList();
            Assert.That(guards, Is.Not.Empty);
            Assert.That(guards.Any(g => g.Hostile.Contains(visitor)), "An unarmed visitor is attacked without a warning.");
        });
    }

    /// <summary>
    /// A patrol comes out of sight while someone is on the gang's ground, walks between the gang's places and goes
    /// home once nobody is around.
    /// </summary>
    [Test]
    public async Task PatrolWalksWhileSomeoneIsAround()
    {
        var patrols = Server.System<NCMissionPatrolSystem>();
        var map = await Pair.CreateTestMap();
        EntityUid visitor = default;

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            SEntMan.SpawnEntity("NCMissionMarkerObjectiveMaelstrom", coords);
            SEntMan.SpawnEntity("NCMissionMarkerGuardMaelstrom", coords.Offset(new Vector2(30, 0)));
            SEntMan.SpawnEntity("NCMissionMarkerGuardMaelstrom", coords.Offset(new Vector2(36, 0)));
            visitor = SpawnVisitor(coords.Offset(new Vector2(2, 0)), 910005);
        });

        await Pair.RunSeconds(4);
        List<EntityUid> members = new();
        await Server.WaitAssertion(() =>
        {
            members = patrols.GetMembers("Maelstrom").ToList();
            Assert.That(members.Count, Is.InRange(1, 2), "One or two people walk the patrol.");
            var visitorPos = Server.System<SharedTransformSystem>().GetMapCoordinates(visitor).Position;
            foreach (var uid in members)
            {
                var pos = Server.System<SharedTransformSystem>().GetMapCoordinates(uid).Position;
                Assert.That((pos - visitorPos).Length(), Is.GreaterThan(14f), "The patrol comes out of sight.");
                var htn = SEntMan.GetComponent<HTNComponent>(uid);
                Assert.That(htn.RootTask.Task, Is.EqualTo("NCPatrolHumanoidCompound"));
                Assert.That(SEntMan.GetComponent<NCMissionGuardComponent>(uid).Mission, Is.Negative);
            }

            Assert.That(SEntMan.GetComponent<HTNComponent>(members[0]).Blackboard.ContainsKey(NCMissionPatrolSystem.TargetKey),
                "The patrol has somewhere to walk.");
            MoveTo(visitor, map.GridCoords.Offset(new Vector2(300, 0)));
        });

        await Pair.RunSeconds(130);
        await Server.WaitAssertion(() =>
        {
            Assert.That(members.All(uid => !SEntMan.EntityExists(uid)), "The patrol goes home once nobody is around.");
            Assert.That(patrols.GetMembers("Maelstrom"), Is.Empty);
        });
    }
}
