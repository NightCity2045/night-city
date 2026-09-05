// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Server._NC.LivingCity.Groups;
using Content.Server._NC.LivingCity.Population;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Groups;
using Content.Shared._NC.LivingCity.Population;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Population;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(NCCityPopulationImportSystem))]
public sealed class NCCityPersistentScaleGateTest : GameTest
{
    private const int ActorCount = 10_000;
    private const int GroupCount = 300;

    [TestPrototypes]
    private const string ScalePrototypes = """
        - type: ncZoneSet
          id: NCCityPersistentScaleZones
          networkId: d9c0c0bc-ec68-4cec-8918-49c4becdef24
          zones:
          - id: 96ad479b-3a69-407a-95a2-dd9f286286a0
            kind: District
            name: Persistent Scale District
            geometry:
            - kind: Polygon
              global: true
              vertices:
              - 0,0
              - 8,0
              - 8,8
              - 0,8
          - id: a237e306-3703-4c50-aa60-3563f5c66406
            kind: Building
            parent: 96ad479b-3a69-407a-95a2-dd9f286286a0
            name: Persistent Scale Building
            geometry:
            - kind: Polygon
              global: true
              vertices:
              - 0,0
              - 8,0
              - 8,8
              - 0,8

        - type: ncCityRoutineProfile
          id: NCCityPersistentScaleRoutine
          cycleDuration: 24h
          entries:
          - at: 0s
            activity: resident
            goal: live
            target: Home
            availability: Available

        - type: ncCityActorArchetype
          id: NCCityPersistentScaleCivilian
          entityPrototype: MobHuman
          spawnCandidateProfile: NCPedestrianSpawnCandidates
          travelMode: Pedestrian
          cognitionTier: Utility
          allowedHomeContexts: [Building]
          allowedWorkContexts: [Building]
          routineProfile: NCCityPersistentScaleRoutine

        - type: ncCityIdentityPool
          id: NCCityPersistentScaleNames
          givenNames: [Given000, Given001, Given002, Given003, Given004, Given005, Given006, Given007, Given008, Given009, Given010, Given011, Given012, Given013, Given014, Given015, Given016, Given017, Given018, Given019, Given020, Given021, Given022, Given023, Given024, Given025, Given026, Given027, Given028, Given029, Given030, Given031, Given032, Given033, Given034, Given035, Given036, Given037, Given038, Given039, Given040, Given041, Given042, Given043, Given044, Given045, Given046, Given047, Given048, Given049, Given050, Given051, Given052, Given053, Given054, Given055, Given056, Given057, Given058, Given059, Given060, Given061, Given062, Given063, Given064, Given065, Given066, Given067, Given068, Given069, Given070, Given071, Given072, Given073, Given074, Given075, Given076, Given077, Given078, Given079, Given080, Given081, Given082, Given083, Given084, Given085, Given086, Given087, Given088, Given089, Given090, Given091, Given092, Given093, Given094, Given095, Given096, Given097, Given098, Given099]
          familyNames: [Family000, Family001, Family002, Family003, Family004, Family005, Family006, Family007, Family008, Family009, Family010, Family011, Family012, Family013, Family014, Family015, Family016, Family017, Family018, Family019, Family020, Family021, Family022, Family023, Family024, Family025, Family026, Family027, Family028, Family029, Family030, Family031, Family032, Family033, Family034, Family035, Family036, Family037, Family038, Family039, Family040, Family041, Family042, Family043, Family044, Family045, Family046, Family047, Family048, Family049, Family050, Family051, Family052, Family053, Family054, Family055, Family056, Family057, Family058, Family059, Family060, Family061, Family062, Family063, Family064, Family065, Family066, Family067, Family068, Family069, Family070, Family071, Family072, Family073, Family074, Family075, Family076, Family077, Family078, Family079, Family080, Family081, Family082, Family083, Family084, Family085, Family086, Family087, Family088, Family089, Family090, Family091, Family092, Family093, Family094, Family095, Family096, Family097, Family098, Family099]

        - type: ncCityPopulationProfile
          id: NCCityPersistentScalePopulation
          scopeId: 714a3731-ee82-40f5-b03b-3243ce12a5c8
          cohorts:
          - id: residents
            count: 10000
            identityPool: NCCityPersistentScaleNames
            archetype: NCCityPersistentScaleCivilian
            homeZone: a237e306-3703-4c50-aa60-3563f5c66406
            workZone: a237e306-3703-4c50-aa60-3563f5c66406

        - type: ncCityGroupProfile
          id: NCCityPersistentScaleSingleActorGroup
          maximumMembers: 1
          maximumSnapshotHistory: 1
          allowSplit: false
          allowMerge: false
          materializeTogether: true
          formationPolicy: Loose
          initialSupplyUnits: 1
          initialMorale: 0.5
        """;

    private static readonly Guid PopulationScope =
        Guid.Parse("714a3731-ee82-40f5-b03b-3243ce12a5c8");
    private static readonly Guid GroupScope =
        Guid.Parse("b439e7f1-cc36-4909-99b6-b897652d2038");
    private static readonly NCZoneId Building =
        new(Guid.Parse("a237e306-3703-4c50-aa60-3563f5c66406"));
    private static readonly ProtoId<NCCityPopulationProfilePrototype> PopulationProfile =
        new("NCCityPersistentScalePopulation");
    private static readonly ProtoId<NCCityGroupProfilePrototype> GroupProfile =
        new("NCCityPersistentScaleSingleActorGroup");

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [Explicit("Semi-release persistence scale gate: writes 10,000 actors and 300 groups through production paths.")]
    [CancelAfter(30 * 60 * 1000)]
    public async Task TenThousandActorsAndThreeHundredGroupsRemainPageableAndAbstract()
    {
        var server = Pair.Server;
        var clock = server.System<NCActiveWorldTimeSystem>();
        var importer = server.System<NCCityPopulationImportSystem>();
        var actors = server.System<NCCityActorRegistrySystem>();
        var domains = server.System<NCCityActorDomainRegistrySystem>();
        var groups = server.System<NCCityGroupRegistrySystem>();
        var runtime = server.System<NCCityActorRuntimeIndexSystem>();

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world epoch did not start.");

        var report = await importer.ImportAsync(PopulationProfile, "integration.scale-gate");
        Assert.Multiple(() =>
        {
            Assert.That(report.Status, Is.EqualTo(NCCityPopulationImportStatus.Completed));
            Assert.That(report.Requested, Is.EqualTo(ActorCount));
            Assert.That(report.Created, Is.EqualTo(ActorCount));
            Assert.That(report.Existing, Is.Zero);
            Assert.That(report.Conflicts, Is.Zero);
            Assert.That(report.Failed, Is.Zero);
            Assert.That(report.Failures, Is.Empty);
            Assert.That(runtime.MaterializedActorCount, Is.Zero);
        });

        var expectedActors = Enumerable.Range(0, ActorCount)
            .Select(index => NCCityActorRegistrySystem.CreateDeterministicId(
                PopulationScope,
                NCCityPopulationImportSystem.BuildCohortStableLocalId(
                    PopulationProfile.Id,
                    "residents",
                    index)))
            .ToArray();

        for (var start = 0; start < GroupCount; start += 8)
        {
            var count = Math.Min(8, GroupCount - start);
            var tasks = new Task<NCCityGroupMutationResult>[count];
            for (var offset = 0; offset < count; offset++)
            {
                var index = start + offset;
                tasks[offset] = groups.CreateAsync(new NCCityGroupCreateRequest(
                    Guid.NewGuid(),
                    GroupScope,
                    $"scale-group-{index:D4}",
                    $"Scale Group {index:D4}",
                    GroupProfile,
                    default,
                    expectedActors[index],
                    "leader",
                    Building,
                    "scale-validation",
                    Guid.NewGuid(),
                    AuditSource: "integration.scale-gate"));
            }

            var results = await Task.WhenAll(tasks);
            Assert.That(results, Has.All.Matches<NCCityGroupMutationResult>(result =>
                result.Status == NCCityGroupMutationStatus.Applied));
        }

        var remainingActors = expectedActors.ToHashSet();
        NCActorId? actorCursor = null;
        do
        {
            var page = await actors.GetPageResultAsync(actorCursor, 500);
            foreach (var actor in page.Records)
                remainingActors.Remove(actor.Snapshot.Identity.ActorId);
            actorCursor = page.HasMore ? page.ContinuationActorId : null;
            if (page.Records.Count == 0)
                break;
        } while (actorCursor != null);
        Assert.That(remainingActors, Is.Empty, "Not every scale actor was recoverable through keyset paging.");

        var expectedGroups = Enumerable.Range(0, GroupCount)
            .Select(index => NCCityGroupRegistrySystem.CreateDeterministicId(
                GroupScope,
                $"scale-group-{index:D4}"))
            .ToArray();
        var remainingGroups = expectedGroups.ToHashSet();
        NCGroupId? groupCursor = null;
        do
        {
            var page = await groups.GetPageAsync(groupCursor, 100);
            foreach (var group in page.Records)
                remainingGroups.Remove(group.Snapshot.GroupId);
            groupCursor = page.HasMore ? page.ContinuationGroupId : null;
            if (page.Records.Count == 0)
                break;
        } while (groupCursor != null);
        Assert.That(remainingGroups, Is.Empty, "Not every scale group was recoverable through keyset paging.");

        foreach (var groupId in expectedGroups)
            groups.Unload(groupId);
        foreach (var actorId in expectedActors)
        {
            actors.Unload(actorId);
            domains.Unload(actorId);
            groups.UnloadMembership(actorId);
        }

        Assert.Multiple(() =>
        {
            Assert.That(actors.CachedActorCount, Is.Zero);
            Assert.That(groups.CachedGroupCount, Is.Zero);
            Assert.That(groups.CachedMembershipCount, Is.Zero);
            Assert.That(runtime.MaterializedActorCount, Is.Zero);
        });

        var reloadedActor = await actors.GetAsync(expectedActors[^1], true);
        var reloadedGroup = await groups.GetAsync(expectedGroups[^1], true);
        Assert.Multiple(() =>
        {
            Assert.That(reloadedActor?.Snapshot.Identity.ActorId, Is.EqualTo(expectedActors[^1]));
            Assert.That(reloadedGroup?.Snapshot.GroupId, Is.EqualTo(expectedGroups[^1]));
            Assert.That(runtime.MaterializedActorCount, Is.Zero);
        });

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
    }

    private static async Task WaitUntil(
        RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate,
        string failure,
        int attempts = 120)
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
