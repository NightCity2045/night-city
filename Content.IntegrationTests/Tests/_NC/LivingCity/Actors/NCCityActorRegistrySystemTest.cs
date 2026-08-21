// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.City.Connectivity;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Groups;
using Content.Server._NC.LivingCity.Population;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Persistence.Outbox;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Groups;
using Content.Shared._NC.LivingCity.Population;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Hands.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Actors;

[TestFixture]
[TestOf(typeof(NCCityActorRegistrySystem))]
public sealed class NCCityActorRegistrySystemTest : GameTest
{
    [TestPrototypes]
    private const string ActorPrototypes = """
        - type: ncZoneSet
          id: NCCityActorIntegrationZones
          networkId: ef40e664-33e3-4f4f-95b5-a6d8a1c5161e
          zones:
          - id: 9eac3062-8744-4518-9209-d993486d66c6
            kind: District
            name: Actor Test District
            geometry:
            - kind: Polygon
              global: true
              vertices:
              - 0,0
              - 10,0
              - 10,10
              - 0,10
          - id: e1f68e94-9b21-4a95-92c7-c9cd2800c04c
            kind: Sector
            parent: 9eac3062-8744-4518-9209-d993486d66c6
            name: Actor Test Sector
            geometry:
            - kind: Polygon
              global: true
              vertices:
              - 0,0
              - 10,0
              - 10,10
              - 0,10
          - id: c85558ac-cd21-44c2-a567-a1e0c5c02422
            kind: Street
            parent: e1f68e94-9b21-4a95-92c7-c9cd2800c04c
            name: Actor Test Street
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - 0,0
              - 10,0
              - 10,10
              - 0,10
          - id: 073995c2-ee30-41d0-a281-ab9708cf34e8
            kind: Building
            parent: c85558ac-cd21-44c2-a567-a1e0c5c02422
            name: Actor Test Workplace
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - 1,1
              - 9,1
              - 9,9
              - 1,9
          - id: 6d9c09e9-b7bb-45ac-a865-5e2df19fcf79
            kind: Apartment
            parent: 073995c2-ee30-41d0-a281-ab9708cf34e8
            name: Actor Test Home
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - 2,2
              - 4,2
              - 4,4
              - 2,4

        - type: ncCityRoutineProfile
          id: NCCityActorIntegrationRoutine
          cycleDuration: 10s
          entries:
          - at: 0s
            activity: at-home
            goal: recover
            target: Home
            availability: Available

        - type: ncCityActorArchetype
          id: NCCityActorIntegrationCivilian
          entityPrototype: MobHuman
          spawnCandidateProfile: NCPedestrianSpawnCandidates
          travelMode: Pedestrian
          cognitionTier: Utility
          allowedHomeContexts: [Apartment]
          allowedWorkContexts: [Building]
          routineProfile: NCCityActorIntegrationRoutine

        - type: ncCityIdentityPool
          id: NCCityActorIntegrationNames
          givenNames: [Mara, Tomas]
          familyNames: [Velez, Reed]

        - type: ncCityPopulationProfile
          id: NCCityActorIntegrationPopulation
          scopeId: 97f0c210-1c19-4168-8b18-c2c8a1bb2b8a
          cohorts:
          - id: apartment-residents
            count: 3
            identityPool: NCCityActorIntegrationNames
            archetype: NCCityActorIntegrationCivilian
            homeZone: 6d9c09e9-b7bb-45ac-a865-5e2df19fcf79
            workZone: 073995c2-ee30-41d0-a281-ab9708cf34e8
          actors:
          - stableLocalId: authored-resident
            fullName: Keiko Aldecaldo
            aliases: [Latch]
            archetype: NCCityActorIntegrationCivilian
            homeZone: 6d9c09e9-b7bb-45ac-a865-5e2df19fcf79
            workZone: 073995c2-ee30-41d0-a281-ab9708cf34e8

        - type: ncCityGroupProfile
          id: NCCityActorIntegrationGroup
          maximumMembers: 4
          maximumSnapshotHistory: 16
          allowSplit: true
          allowMerge: true
          materializeTogether: true
          formationPolicy: Loose
          initialSupplyUnits: 12
          initialMorale: 0.7
        """;

    private static readonly NCZoneId HomeZone =
        new(Guid.Parse("6d9c09e9-b7bb-45ac-a865-5e2df19fcf79"));
    private static readonly NCZoneId WorkZone =
        new(Guid.Parse("073995c2-ee30-41d0-a281-ab9708cf34e8"));
    private static readonly NCZoneId DistrictZone =
        new(Guid.Parse("9eac3062-8744-4518-9209-d993486d66c6"));
    private static readonly NCZoneId SectorZone =
        new(Guid.Parse("e1f68e94-9b21-4a95-92c7-c9cd2800c04c"));
    private static readonly ProtoId<NCCityActorArchetypePrototype> Archetype =
        new("NCCityActorIntegrationCivilian");
    private static readonly ProtoId<NCCityGroupProfilePrototype> GroupProfile =
        new("NCCityActorIntegrationGroup");
    private static readonly NCZNetworkId NetworkId =
        new(Guid.Parse("ef40e664-33e3-4f4f-95b5-a6d8a1c5161e"));

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task PersistentIdentityLifecycleAndRuntimeBindingRemainDeterministic()
    {
        var server = Pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var entities = server.ResolveDependency<IEntityManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var registry = server.System<NCCityActorRegistrySystem>();
        var cognitionRegistry = server.System<NCCognitionRegistrySystem>();
        var socialProfiles = server.System<NCSocialProfileRegistrySystem>();
        var runtime = server.System<NCCityActorRuntimeIndexSystem>();

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world epoch did not become ready.");

        var populationScope = Guid.Parse("cf764ece-a406-44c7-827e-90ddef894100");
        var create = new NCCityActorCreateRequest(
            Guid.NewGuid(),
            populationScope,
            "watson.home.042.resident.01",
            "Mara Velez",
            ["Switch"],
            Archetype,
            HomeZone,
            WorkZone,
            Guid.NewGuid());
        var expectedId = NCCityActorRegistrySystem.CreateDeterministicId(
            populationScope,
            create.StableLocalId);

        var applied = await registry.CreateAsync(create);
        var duplicate = await registry.CreateAsync(create);
        var cognition = await cognitionRegistry.GetCognitionAsync(expectedId, true);
        var beliefs = await cognitionRegistry.GetBeliefsAsync(expectedId, true);
        var socialProfile = await socialProfiles.GetAsync(expectedId, true);

        Assert.Multiple(() =>
        {
            Assert.That(applied.Status, Is.EqualTo(NCCityActorMutationStatus.Applied));
            Assert.That(applied.ActorId, Is.EqualTo(expectedId));
            Assert.That(applied.AggregateVersion, Is.EqualTo(1));
            Assert.That(applied.Record?.Snapshot.Identity.FullName, Is.EqualTo("Mara Velez"));
            Assert.That(applied.Record?.Snapshot.Location.HomeZone, Is.EqualTo(HomeZone));
            Assert.That(applied.Record?.Snapshot.Location.WorkZone, Is.EqualTo(WorkZone));
            Assert.That(duplicate.Status, Is.EqualTo(NCCityActorMutationStatus.Duplicate));
            Assert.That(duplicate.AggregateVersion, Is.EqualTo(1));
            Assert.That(duplicate.Record?.Snapshot.Identity.ActorId, Is.EqualTo(expectedId));
            Assert.That(duplicate.Record?.Snapshot.Identity.Aliases, Is.EqualTo(new[] { "Switch" }));
            Assert.That(duplicate.Record?.Snapshot.LastAppliedEventId,
                Is.EqualTo(applied.Record?.Snapshot.LastAppliedEventId));
            Assert.That(cognition?.Snapshot.ActorId, Is.EqualTo(expectedId));
            Assert.That(cognition?.Snapshot.DecisionPolicyId, Is.EqualTo("NCDefaultCognitionDecisionPolicy"));
            Assert.That(cognition?.AggregateVersion, Is.GreaterThanOrEqualTo(1));
            Assert.That(beliefs?.Snapshot.ActorId, Is.EqualTo(expectedId));
            Assert.That(beliefs?.AggregateVersion, Is.GreaterThanOrEqualTo(1));
            Assert.That(socialProfile?.Snapshot.ActorId, Is.EqualTo(expectedId));
            Assert.That(socialProfile?.Snapshot.Speech.Languages,
                Is.EquivalentTo(new[] { NCLanguage.Russian, NCLanguage.English }));
            Assert.That(socialProfile?.AggregateVersion, Is.EqualTo(1));
        });

        var deathCommand = new NCCityActorStatusMutationRequest(
            Guid.NewGuid(),
            expectedId,
            1,
            NCCityActorStatus.Dead,
            Guid.NewGuid());
        var died = await registry.SetStatusAsync(deathCommand);
        var retriedDeath = await registry.SetStatusAsync(deathCommand);
        var reviveRejected = await registry.SetStatusAsync(new NCCityActorStatusMutationRequest(
            Guid.NewGuid(),
            expectedId,
            2,
            NCCityActorStatus.Active,
            Guid.NewGuid()));

        Assert.Multiple(() =>
        {
            Assert.That(died.Status, Is.EqualTo(NCCityActorMutationStatus.Applied));
            Assert.That(died.AggregateVersion, Is.EqualTo(2));
            Assert.That(died.Record?.Snapshot.Lifecycle.DiedAt, Is.Not.Null);
            Assert.That(retriedDeath.Status, Is.EqualTo(NCCityActorMutationStatus.Duplicate));
            Assert.That(retriedDeath.AggregateVersion, Is.EqualTo(2));
            Assert.That(retriedDeath.Record?.Snapshot.Lifecycle.Status, Is.EqualTo(NCCityActorStatus.Dead));
            Assert.That(retriedDeath.Record?.Snapshot.LastAppliedEventId,
                Is.EqualTo(died.Record?.Snapshot.LastAppliedEventId));
            Assert.That(reviveRejected.Status, Is.EqualTo(NCCityActorMutationStatus.Rejected));
        });

        Assert.That(registry.Unload(expectedId), Is.True);
        var reloaded = await registry.GetAsync(expectedId);
        var page = await registry.GetPageAsync(null, 500);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded?.AggregateVersion, Is.EqualTo(2));
            Assert.That(reloaded?.Snapshot.Lifecycle.Status, Is.EqualTo(NCCityActorStatus.Dead));
            Assert.That(page.Select(value => value.Snapshot.Identity.ActorId), Does.Contain(expectedId));
        });

        EntityUid firstEntity = default;
        EntityUid secondEntity = default;
        var secondActor = NCCityActorRegistrySystem.CreateDeterministicId(populationScope, "resident.02");
        await server.WaitAssertion(() =>
        {
            firstEntity = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            secondEntity = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            Assert.That(runtime.TryBind(expectedId, firstEntity), Is.True);
            Assert.That(runtime.TryBind(expectedId, firstEntity), Is.True);
            Assert.That(runtime.TryBind(expectedId, secondEntity), Is.False);
            Assert.That(runtime.TryBind(secondActor, firstEntity), Is.False);
            Assert.That(runtime.TryGetEntity(expectedId, out var boundEntity), Is.True);
            Assert.That(boundEntity, Is.EqualTo(firstEntity));
            Assert.That(runtime.TryGetActor(firstEntity, out var boundActor), Is.True);
            Assert.That(boundActor, Is.EqualTo(expectedId));
            Assert.That(runtime.MaterializedActorCount, Is.EqualTo(1));
        });

        NCSessionEpoch epoch = default;
        await server.WaitPost(() => Assert.That(clock.TryGetCurrentTime(out epoch, out _), Is.True));
        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(runtime.MaterializedActorCount, Is.Zero);
            Assert.That(entities.HasComponent<NCCityActorBindingComponent>(firstEntity), Is.False);
            Assert.That(entities.HasComponent<NCCityActorBindingComponent>(secondEntity), Is.False);
        });

        await WaitUntilAsync(server, async () =>
        {
            var stored = await database.GetNCActiveWorldClockAsync();
            return stored is { RoundActive: false } && stored.SessionEpoch == epoch;
        }, "The active-world epoch did not close.");
    }

    [Test]
    public async Task ActorCreationAndMaterializedDomainCaptureAreAtomicAndRestorable()
    {
        var server = Pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var entities = server.ResolveDependency<IEntityManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var registry = server.System<NCCityActorRegistrySystem>();
        var domains = server.System<NCCityActorDomainRegistrySystem>();
        var runtime = server.System<NCCityActorRuntimeIndexSystem>();
        var hands = server.System<SharedHandsSystem>();

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world epoch did not become ready.");

        var created = await registry.CreateAsync(new NCCityActorCreateRequest(
            Guid.NewGuid(),
            Guid.Parse("a18e30f6-00e2-47c2-9827-aa27d7988897"),
            "domain-capture.resident.01",
            "Lucia Guerra",
            [],
            Archetype,
            HomeZone,
            WorkZone,
            Guid.NewGuid()));
        Assert.That(created.Status, Is.EqualTo(NCCityActorMutationStatus.Applied));

        var initialHealth = await domains.GetHealthAsync(created.ActorId, true);
        var initialInventory = await domains.GetInventoryAsync(created.ActorId, true);
        Assert.Multiple(() =>
        {
            Assert.That(initialHealth?.AggregateVersion, Is.EqualTo(1));
            Assert.That(initialHealth?.Snapshot.MedicalStatePayload, Is.Null);
            Assert.That(initialHealth?.Snapshot.Summary.OverallCondition,
                Is.EqualTo(NCCityOverallCondition.Stable));
            Assert.That(initialInventory?.AggregateVersion, Is.EqualTo(1));
            Assert.That(initialInventory?.Snapshot.Summary.UniqueItems, Is.Empty);
        });

        EntityUid body = default;
        EntityUid weapon = default;
        NCCirculationComponent circulation = default!;
        float capturedBloodVolume = 0f;
        var itemId = new NCItemId(Guid.Parse("70540291-4638-4fa8-a663-95cf73e87a16"));
        await server.WaitAssertion(() =>
        {
            body = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            weapon = entities.SpawnEntity("Crowbar", MapCoordinates.Nullspace);
            Assert.That(runtime.TryBind(created.ActorId, body), Is.True);

            var authority = entities.EnsureComponent<NCCityActorInventoryAdapterComponent>(body);
            authority.AuthorityVersion = 7;
            authority.EquippedLoadoutId = "street-loadout";
            var item = entities.EnsureComponent<NCCityPersistentItemAdapterComponent>(weapon);
            item.ItemId = itemId;
            item.Categories = NCCityInventoryItemCategory.Unique | NCCityInventoryItemCategory.Weapon;
            Assert.That(hands.TryPickupAnyHand(body, weapon, animate: false), Is.True);

            circulation = entities.GetComponent<NCCirculationComponent>(body);
            capturedBloodVolume = circulation.BloodVolume;
            Assert.That(capturedBloodVolume, Is.GreaterThan(0f));
        });

        var commandId = Guid.NewGuid();
        var request = new NCCityActorDomainCaptureRequest(
            commandId,
            created.ActorId,
            1,
            1,
            Guid.NewGuid(),
            AuditSource: "test:actor-domain-capture");
        Task<NCCityActorDomainCaptureResult> captureTask = default!;
        await server.WaitPost(() => captureTask = domains.CaptureMaterializedAsync(request));
        var captured = await captureTask;
        Assert.Multiple(() =>
        {
            Assert.That(captured.Status, Is.EqualTo(NCCityActorMutationStatus.Applied));
            Assert.That(captured.Health?.AggregateVersion, Is.EqualTo(2));
            Assert.That(captured.Health?.Snapshot.MedicalSnapshotVersion, Is.GreaterThan(0));
            Assert.That(captured.Health?.Snapshot.MedicalStatePayload, Is.Not.Null.And.Not.Empty);
            Assert.That(captured.Inventory?.AggregateVersion, Is.EqualTo(2));
            Assert.That(captured.Inventory?.Snapshot.SourceAuthorityVersion, Is.EqualTo(7));
            Assert.That(captured.Inventory?.Snapshot.Summary.EquippedLoadoutId, Is.EqualTo("street-loadout"));
            Assert.That(captured.Inventory?.Snapshot.Summary.UniqueItems, Is.EqualTo(new[] { itemId }));
            Assert.That(captured.Inventory?.Snapshot.Summary.Weapons, Is.EqualTo(new[] { itemId }));
        });

        Task<NCCityActorDomainCaptureResult> duplicateTask = default!;
        await server.WaitPost(() => duplicateTask = domains.CaptureMaterializedAsync(request));
        var duplicate = await duplicateTask;
        Assert.Multiple(() =>
        {
            Assert.That(duplicate.Status, Is.EqualTo(NCCityActorMutationStatus.Duplicate));
            Assert.That(duplicate.Health?.AggregateVersion, Is.EqualTo(2));
            Assert.That(duplicate.Inventory?.AggregateVersion, Is.EqualTo(2));
        });

        EntityUid untagged = default;
        await server.WaitAssertion(() =>
        {
            untagged = entities.SpawnEntity("Wrench", MapCoordinates.Nullspace);
            Assert.That(hands.TryPickupAnyHand(body, untagged, animate: false), Is.True);
        });
        Task<NCCityActorDomainCaptureResult> changedWorldRetryTask = default!;
        await server.WaitPost(() => changedWorldRetryTask = domains.CaptureMaterializedAsync(request));
        var changedWorldRetry = await changedWorldRetryTask;
        Assert.That(changedWorldRetry.Status, Is.EqualTo(NCCityActorMutationStatus.Duplicate),
            "An exact command retry must use its stored receipt before re-reading mutable inventory.");

        Task<NCCityActorDomainCaptureResult> rejectedTask = default!;
        await server.WaitPost(() => rejectedTask = domains.CaptureMaterializedAsync(
            new NCCityActorDomainCaptureRequest(
                Guid.NewGuid(),
                created.ActorId,
                2,
                2,
                Guid.NewGuid(),
                AuditSource: "test:untagged-item")));
        var rejected = await rejectedTask;
        var unchangedHealth = await domains.GetHealthAsync(created.ActorId, true);
        var unchangedInventory = await domains.GetInventoryAsync(created.ActorId, true);
        Assert.Multiple(() =>
        {
            Assert.That(rejected.Status, Is.EqualTo(NCCityActorMutationStatus.Rejected));
            Assert.That(unchangedHealth?.AggregateVersion, Is.EqualTo(2));
            Assert.That(unchangedInventory?.AggregateVersion, Is.EqualTo(2));
        });

        await server.WaitAssertion(() =>
        {
            circulation.BloodVolume = capturedBloodVolume * 0.5f;
            Assert.That(domains.TryRestoreHealth(created.ActorId, body, captured.Health!), Is.True);
            Assert.That(circulation.BloodVolume, Is.EqualTo(capturedBloodVolume).Within(0.001f));
        });

        Assert.That(domains.Unload(created.ActorId), Is.True);
        var reloadedHealth = await domains.GetHealthAsync(created.ActorId);
        var reloadedInventory = await domains.GetInventoryAsync(created.ActorId);
        Assert.Multiple(() =>
        {
            Assert.That(reloadedHealth?.AggregateVersion, Is.EqualTo(2));
            Assert.That(reloadedHealth?.Snapshot.MedicalStatePayload, Is.Not.Null.And.Not.Empty);
            Assert.That(reloadedInventory?.AggregateVersion, Is.EqualTo(2));
            Assert.That(reloadedInventory?.Snapshot.Summary.Weapons, Is.EqualTo(new[] { itemId }));
        });

        await server.WaitPost(() =>
        {
            runtime.Unbind(body);
            entities.DeleteEntity(body);
            if (entities.EntityExists(weapon))
                entities.DeleteEntity(weapon);
            if (entities.EntityExists(untagged))
                entities.DeleteEntity(untagged);
        });

        NCSessionEpoch epoch = default;
        await server.WaitPost(() => Assert.That(clock.TryGetCurrentTime(out epoch, out _), Is.True));
        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await WaitUntilAsync(server, async () =>
        {
            var stored = await database.GetNCActiveWorldClockAsync();
            return stored is { RoundActive: false } && stored.SessionEpoch == epoch;
        }, "The active-world epoch did not close.");
    }

    [Test]
    public async Task SchedulerAppliesCommitmentThenReturnsToCyclicRoutineWithoutMaterialization()
    {
        var server = Pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var registry = server.System<NCCityActorRegistrySystem>();
        var scheduler = server.System<NCCityActorScheduleSchedulerSystem>();
        var runtime = server.System<NCCityActorRuntimeIndexSystem>();

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world epoch did not become ready.");
        await WaitUntil(server, () => scheduler.InitialLoadComplete, "The actor schedule queue did not load.");

        var create = new NCCityActorCreateRequest(
            Guid.NewGuid(),
            Guid.Parse("3e031e7b-59fa-4894-af4b-4271873bc458"),
            "scheduler.resident.01",
            "Tomas Reed",
            [],
            Archetype,
            HomeZone,
            WorkZone,
            Guid.NewGuid());
        var created = await registry.CreateAsync(create);
        Assert.That(created.Status, Is.EqualTo(NCCityActorMutationStatus.Applied));

        var routineApplied = await WaitForActor(server, registry, created.ActorId, actor =>
            actor.AggregateVersion >= 2 && actor.Snapshot.Schedule.CurrentActivity == "at-home");
        Assert.Multiple(() =>
        {
            Assert.That(routineApplied.Snapshot.Location.CurrentZone, Is.EqualTo(HomeZone));
            Assert.That(routineApplied.Snapshot.Location.DestinationZone.IsValid, Is.False);
            Assert.That(routineApplied.Snapshot.Schedule.Availability,
                Is.EqualTo(NCCityActorAvailability.Available));
            Assert.That(runtime.MaterializedActorCount, Is.Zero);
        });

        NCActiveWorldTime commitmentStart = default;
        await server.WaitPost(() => Assert.That(clock.TryGetCurrentTime(out _, out commitmentStart), Is.True));
        var commitment = new NCCityActorCommitment(
            NCCityCommitmentId.New(),
            "scheduled-meeting",
            "meet-contact",
            WorkZone,
            commitmentStart,
            commitmentStart.Advance(TimeSpan.FromSeconds(1)),
            NCCityActorAvailability.Busy,
            100);
        var commitmentAdded = await registry.AddCommitmentAsync(new NCCityActorAddCommitmentRequest(
            Guid.NewGuid(),
            created.ActorId,
            routineApplied.AggregateVersion,
            commitment,
            Guid.NewGuid()));
        Assert.That(commitmentAdded.Status, Is.EqualTo(NCCityActorMutationStatus.Applied));

        var commitmentApplied = await WaitForActor(server, registry, created.ActorId, actor =>
            actor.AggregateVersion > commitmentAdded.AggregateVersion &&
            actor.Snapshot.Schedule.CurrentActivity == "scheduled-meeting");
        Assert.Multiple(() =>
        {
            Assert.That(commitmentApplied.Snapshot.Location.DestinationZone, Is.EqualTo(WorkZone));
            Assert.That(commitmentApplied.Snapshot.Location.TravelPhase,
                Is.EqualTo(NCCityActorTravelPhase.Planned));
            Assert.That(commitmentApplied.Snapshot.Schedule.Availability,
                Is.EqualTo(NCCityActorAvailability.Busy));
            Assert.That(commitmentApplied.Snapshot.Schedule.Commitments, Has.Length.EqualTo(1));
        });

        var routineResumed = await WaitForActor(server, registry, created.ActorId, actor =>
            actor.AggregateVersion > commitmentApplied.AggregateVersion &&
            actor.Snapshot.Schedule.CurrentActivity == "at-home" &&
            actor.Snapshot.Schedule.Commitments.Length == 0);
        Assert.Multiple(() =>
        {
            Assert.That(routineResumed.Snapshot.Location.CurrentZone, Is.EqualTo(HomeZone));
            Assert.That(routineResumed.Snapshot.Location.DestinationZone.IsValid, Is.False);
            Assert.That(routineResumed.Snapshot.Location.TravelPhase,
                Is.EqualTo(NCCityActorTravelPhase.Stationary));
            Assert.That(runtime.MaterializedActorCount, Is.Zero);
            Assert.That(scheduler.PendingMutationCount, Is.Zero);
        });

        NCSessionEpoch epoch = default;
        await server.WaitPost(() => Assert.That(clock.TryGetCurrentTime(out epoch, out _), Is.True));
        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await WaitUntilAsync(server, async () =>
        {
            var stored = await database.GetNCActiveWorldClockAsync();
            return stored is { RoundActive: false } && stored.SessionEpoch == epoch;
        }, "The active-world epoch did not close.");
    }

    [Test]
    public async Task PopulationImportIsDeterministicBoundedAndRestartSafe()
    {
        var server = Pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var registry = server.System<NCCityActorRegistrySystem>();
        var population = server.System<NCCityPopulationImportSystem>();
        var runtime = server.System<NCCityActorRuntimeIndexSystem>();
        var profileId = new ProtoId<NCCityPopulationProfilePrototype>("NCCityActorIntegrationPopulation");

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world epoch did not become ready.");

        var validationErrors = new List<string>();
        Assert.That(population.ValidateProfile(profileId, validationErrors), Is.True,
            string.Join(Environment.NewLine, validationErrors));

        var first = await population.ImportAsync(profileId, "test:population-import");
        var second = await population.ImportAsync(profileId, "test:population-import");
        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(NCCityPopulationImportStatus.Completed));
            Assert.That(first.Requested, Is.EqualTo(4));
            Assert.That(first.Created, Is.EqualTo(4));
            Assert.That(first.Existing, Is.Zero);
            Assert.That(first.Failed, Is.Zero);
            Assert.That(second.Status, Is.EqualTo(NCCityPopulationImportStatus.Completed));
            Assert.That(second.Created, Is.Zero);
            Assert.That(second.Existing, Is.EqualTo(4));
            Assert.That(second.Conflicts, Is.Zero);
            Assert.That(second.Failed, Is.Zero);
            Assert.That(runtime.MaterializedActorCount, Is.Zero);
        });

        var scopeId = Guid.Parse("97f0c210-1c19-4168-8b18-c2c8a1bb2b8a");
        var generated = new List<NCCityActorRecord>();
        for (var index = 0; index < 3; index++)
        {
            var localId = NCCityPopulationImportSystem.BuildCohortStableLocalId(
                profileId.Id,
                "apartment-residents",
                index);
            var actorId = NCCityActorRegistrySystem.CreateDeterministicId(scopeId, localId);
            generated.Add((await registry.GetAsync(actorId, true))!);
        }

        var authoredLocalId = NCCityPopulationImportSystem.BuildAuthoredStableLocalId(
            profileId.Id,
            "authored-resident");
        var authoredId = NCCityActorRegistrySystem.CreateDeterministicId(scopeId, authoredLocalId);
        var authored = await registry.GetAsync(authoredId, true);
        Assert.Multiple(() =>
        {
            Assert.That(generated, Has.All.Not.Null);
            Assert.That(generated.Select(actor => actor.Snapshot.Identity.FullName), Is.Unique);
            Assert.That(generated.Select(actor => actor.Snapshot.Location.HomeZone), Has.All.EqualTo(HomeZone));
            Assert.That(authored?.Snapshot.Identity.FullName, Is.EqualTo("Keiko Aldecaldo"));
            Assert.That(authored?.Snapshot.Identity.Aliases, Is.EqualTo(new[] { "Latch" }));
        });

        NCSessionEpoch epoch = default;
        await server.WaitPost(() => Assert.That(clock.TryGetCurrentTime(out epoch, out _), Is.True));
        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await WaitUntilAsync(server, async () =>
        {
            var stored = await database.GetNCActiveWorldClockAsync();
            return stored is { RoundActive: false } && stored.SessionEpoch == epoch;
        }, "The active-world epoch did not close.");
    }

    [Test]
    public async Task UnmaterializedActorCompletesAbstractCityGraphTravelInActiveWorldTime()
    {
        var server = Pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var entities = server.ResolveDependency<IEntityManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var registry = server.System<NCCityActorRegistrySystem>();
        var scheduler = server.System<NCCityActorScheduleSchedulerSystem>();
        var travel = server.System<NCCityAbstractTravelSystem>();
        var runtime = server.System<NCCityActorRuntimeIndexSystem>();
        var connectivity = server.System<NCCityConnectivitySystem>();
        NCCityPortalId portalId = default;

        await server.WaitAssertion(() =>
        {
            Assert.That(connectivity.TryRegisterPortal(new NCCityPortalRegistration(
                HomeZone,
                WorkZone,
                new NCMapCoordinates(NetworkId, new Vector2(4f, 3f), 0),
                new NCMapCoordinates(NetworkId, new Vector2(5f, 3f), 0),
                NCCityPortalKind.OpenBoundary,
                new ProtoId<NCCityTraversalProfilePrototype>("NCTraversalOpenBoundary"),
                default), out portalId), Is.True);
        });

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world epoch did not become ready.");
        await WaitUntil(server, () => scheduler.InitialLoadComplete && travel.InitialLoadComplete,
            "The actor schedulers did not complete their initial snapshot loads.");

        var created = await registry.CreateAsync(new NCCityActorCreateRequest(
            Guid.NewGuid(),
            Guid.Parse("6e56b86a-a8ea-467f-9d40-b733d4da9015"),
            "travel.resident.01",
            "Inez Cross",
            [],
            Archetype,
            HomeZone,
            WorkZone,
            Guid.NewGuid()));
        Assert.That(created.Status, Is.EqualTo(NCCityActorMutationStatus.Applied));
        var routineApplied = await WaitForActor(server, registry, created.ActorId, actor =>
            actor.Snapshot.Schedule.CurrentActivity == "at-home");

        EntityUid materializedEntity = default;
        await server.WaitAssertion(() =>
        {
            materializedEntity = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            Assert.That(runtime.TryBind(created.ActorId, materializedEntity), Is.True);
        });

        NCActiveWorldTime now = default;
        await server.WaitPost(() => Assert.That(clock.TryGetCurrentTime(out _, out now), Is.True));
        var planned = await registry.SetLocationAsync(new NCCityActorLocationMutationRequest(
            Guid.NewGuid(),
            created.ActorId,
            routineApplied.AggregateVersion,
            HomeZone,
            WorkZone,
            NCCityActorTravelPhase.Planned,
            NCCityTravelMode.Pedestrian,
            null,
            routineApplied.Snapshot.Schedule.CurrentActivity,
            routineApplied.Snapshot.Schedule.CurrentGoal,
            routineApplied.Snapshot.Schedule.Availability,
            routineApplied.Snapshot.Schedule.NextUpdateAt,
            Guid.NewGuid(),
            AuditSource: "test:abstract-travel"));
        Assert.That(planned.Status, Is.EqualTo(NCCityActorMutationStatus.Applied));

        // Abstract travel must yield ownership while a physical entity represents the actor.
        await server.WaitRunTicks(15);
        var stillPhysical = await registry.GetAsync(created.ActorId, true);
        Assert.Multiple(() =>
        {
            Assert.That(stillPhysical?.Snapshot.Location.TravelPhase, Is.EqualTo(NCCityActorTravelPhase.Planned));
            Assert.That(stillPhysical?.Snapshot.Location.ExpectedArrivalAt, Is.Null);
            Assert.That(runtime.MaterializedActorCount, Is.EqualTo(1));
        });
        await server.WaitAssertion(() => Assert.That(runtime.Unbind(materializedEntity), Is.True));

        var inTransit = await WaitForActor(server, registry, created.ActorId, actor =>
            actor.Snapshot.Location.TravelPhase == NCCityActorTravelPhase.InTransit);
        var arrived = await WaitForActor(server, registry, created.ActorId, actor =>
            actor.AggregateVersion > inTransit.AggregateVersion &&
            actor.Snapshot.Location.TravelPhase == NCCityActorTravelPhase.Stationary &&
            actor.Snapshot.Location.CurrentZone == WorkZone);
        Assert.Multiple(() =>
        {
            Assert.That(inTransit.Snapshot.Location.ExpectedArrivalAt, Is.Not.Null);
            Assert.That(inTransit.Snapshot.Location.ExpectedArrivalAt, Is.GreaterThan(now));
            Assert.That(arrived.Snapshot.Location.DestinationZone.IsValid, Is.False);
            Assert.That(arrived.Snapshot.Location.ExpectedArrivalAt, Is.Null);
            Assert.That(runtime.MaterializedActorCount, Is.Zero);
            Assert.That(travel.PendingMutationCount, Is.Zero);
        });

        await server.WaitAssertion(() => Assert.That(connectivity.RemovePortal(portalId), Is.True));
        NCSessionEpoch epoch = default;
        await server.WaitPost(() => Assert.That(clock.TryGetCurrentTime(out epoch, out _), Is.True));
        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await WaitUntilAsync(server, async () =>
        {
            var stored = await database.GetNCActiveWorldClockAsync();
            return stored is { RoundActive: false } && stored.SessionEpoch == epoch;
        }, "The active-world epoch did not close.");
    }

    [Test]
    public async Task GroupMembershipCreateTransferAndLeaderSuccessionAreAtomicAndRestartSafe()
    {
        var server = Pair.Server;
        var clock = server.System<NCActiveWorldTimeSystem>();
        var actors = server.System<NCCityActorRegistrySystem>();
        var groups = server.System<NCCityGroupRegistrySystem>();

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world epoch did not become ready.");

        var populationScope = Guid.NewGuid();
        var firstLeader = await actors.CreateAsync(new NCCityActorCreateRequest(
            Guid.NewGuid(), populationScope, "group-leader-one", "Mara Velez", [], Archetype,
            HomeZone, WorkZone, Guid.NewGuid()));
        var secondLeader = await actors.CreateAsync(new NCCityActorCreateRequest(
            Guid.NewGuid(), populationScope, "group-leader-two", "Tomas Reed", [], Archetype,
            HomeZone, WorkZone, Guid.NewGuid()));
        var member = await actors.CreateAsync(new NCCityActorCreateRequest(
            Guid.NewGuid(), populationScope, "group-member", "Keiko Velez", [], Archetype,
            HomeZone, WorkZone, Guid.NewGuid()));
        Assert.That(new[] { firstLeader.Status, secondLeader.Status, member.Status },
            Is.All.EqualTo(NCCityActorMutationStatus.Applied));

        var groupScope = Guid.NewGuid();
        var firstCreate = new NCCityGroupCreateRequest(
            Guid.NewGuid(), groupScope, "first-crew", "First Crew", GroupProfile, default,
            firstLeader.ActorId, "lead", HomeZone, "hold-territory", Guid.NewGuid());
        var secondCreate = new NCCityGroupCreateRequest(
            Guid.NewGuid(), groupScope, "second-crew", "Second Crew", GroupProfile, default,
            secondLeader.ActorId, "lead", HomeZone, "escort-convoy", Guid.NewGuid());
        var firstGroup = await groups.CreateAsync(firstCreate);
        var duplicateCreate = await groups.CreateAsync(firstCreate);
        var secondGroup = await groups.CreateAsync(secondCreate);
        Assert.Multiple(() =>
        {
            Assert.That(firstGroup.Status, Is.EqualTo(NCCityGroupMutationStatus.Applied));
            Assert.That(duplicateCreate.Status, Is.EqualTo(NCCityGroupMutationStatus.Duplicate));
            Assert.That(secondGroup.Status, Is.EqualTo(NCCityGroupMutationStatus.Applied));
            Assert.That(firstGroup.Groups.Single().Snapshot.Resources.SupplyUnits, Is.EqualTo(12));
        });

        var firstGroupId = firstGroup.GroupId;
        var secondGroupId = secondGroup.GroupId;
        var addRequest = new NCCityGroupAddMemberRequest(
            Guid.NewGuid(), firstGroupId, 1, member.ActorId, "scout", Guid.NewGuid());
        var added = await groups.AddMemberAsync(addRequest);
        var duplicateAdd = await groups.AddMemberAsync(addRequest);
        Assert.Multiple(() =>
        {
            Assert.That(added.Status, Is.EqualTo(NCCityGroupMutationStatus.Applied));
            Assert.That(duplicateAdd.Status, Is.EqualTo(NCCityGroupMutationStatus.Duplicate));
            Assert.That(added.Groups.Single().Snapshot.Members, Has.Length.EqualTo(2));
            Assert.That(added.Membership?.Snapshot.GroupId, Is.EqualTo(firstGroupId));
        });

        var transferRequest = new NCCityGroupTransferMemberRequest(
            Guid.NewGuid(), firstGroupId, 2, secondGroupId, 1, member.ActorId, "driver", default,
            Guid.NewGuid());
        var transferred = await groups.TransferMemberAsync(transferRequest);
        var duplicateTransfer = await groups.TransferMemberAsync(transferRequest);
        Assert.Multiple(() =>
        {
            Assert.That(transferred.Status, Is.EqualTo(NCCityGroupMutationStatus.Applied));
            Assert.That(duplicateTransfer.Status, Is.EqualTo(NCCityGroupMutationStatus.Duplicate));
            Assert.That(transferred.Groups, Has.Count.EqualTo(2));
            Assert.That(transferred.Groups.Single(value => value.Snapshot.GroupId == firstGroupId)
                .Snapshot.Members.Select(value => value.ActorId), Does.Not.Contain(member.ActorId));
            Assert.That(transferred.Groups.Single(value => value.Snapshot.GroupId == secondGroupId)
                .Snapshot.Members.Select(value => value.ActorId), Does.Contain(member.ActorId));
            Assert.That(transferred.Membership?.Snapshot.GroupId, Is.EqualTo(secondGroupId));
            Assert.That(transferred.Membership?.Snapshot.Role, Is.EqualTo("driver"));
        });

        var rejectedDoubleMembership = await groups.AddMemberAsync(new NCCityGroupAddMemberRequest(
            Guid.NewGuid(), firstGroupId, 3, member.ActorId, "scout", Guid.NewGuid()));
        Assert.That(rejectedDoubleMembership.Status, Is.EqualTo(NCCityGroupMutationStatus.Rejected));

        var removeLeaderRequest = new NCCityGroupRemoveMemberRequest(
            Guid.NewGuid(), secondGroupId, 2, secondLeader.ActorId, member.ActorId, Guid.NewGuid());
        var removedLeader = await groups.RemoveMemberAsync(removeLeaderRequest);
        var duplicateRemove = await groups.RemoveMemberAsync(removeLeaderRequest);
        Assert.Multiple(() =>
        {
            Assert.That(removedLeader.Status, Is.EqualTo(NCCityGroupMutationStatus.Applied));
            Assert.That(duplicateRemove.Status, Is.EqualTo(NCCityGroupMutationStatus.Duplicate));
            Assert.That(removedLeader.Groups.Single().Snapshot.LeaderActorId, Is.EqualTo(member.ActorId));
            Assert.That(removedLeader.Membership?.Snapshot.GroupId.IsValid, Is.False);
        });

        groups.Unload(firstGroupId);
        groups.Unload(secondGroupId);
        groups.UnloadMembership(member.ActorId);
        var reloadedFirst = await groups.GetAsync(firstGroupId, true);
        var reloadedSecond = await groups.GetAsync(secondGroupId, true);
        var reloadedMembership = await groups.GetMembershipAsync(member.ActorId, true);
        Assert.Multiple(() =>
        {
            Assert.That(reloadedFirst?.AggregateVersion, Is.EqualTo(3));
            Assert.That(reloadedFirst?.Snapshot.Members.Select(value => value.ActorId),
                Is.EqualTo(new[] { firstLeader.ActorId }));
            Assert.That(reloadedSecond?.AggregateVersion, Is.EqualTo(3));
            Assert.That(reloadedSecond?.Snapshot.LeaderActorId, Is.EqualTo(member.ActorId));
            Assert.That(reloadedMembership?.Snapshot.GroupId, Is.EqualTo(secondGroupId));
            Assert.That(reloadedMembership?.AggregateVersion, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task DistrictAndSectorZoneStateTracksPersistentActorsAndAuthoritativeSignals()
    {
        var server = Pair.Server;
        var clock = server.System<NCActiveWorldTimeSystem>();
        var actors = server.System<NCCityActorRegistrySystem>();
        var runtime = server.System<NCCityActorRuntimeIndexSystem>();
        var projections = server.System<NCCityZoneStateProjectionSystem>();
        var zoneStates = server.System<NCCityZoneStateRegistrySystem>();

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady && projections.InitialLoadComplete,
            "The city zone-state projector did not finish its bounded initial load.", 300);

        var populationScope = Guid.NewGuid();
        var first = await actors.CreateAsync(new NCCityActorCreateRequest(
            Guid.NewGuid(), populationScope, "zone-state.01", "Mara Velez", [], Archetype,
            HomeZone, WorkZone, Guid.NewGuid()));
        var second = await actors.CreateAsync(new NCCityActorCreateRequest(
            Guid.NewGuid(), populationScope, "zone-state.02", "Tomas Reed", [], Archetype,
            HomeZone, WorkZone, Guid.NewGuid()));
        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(NCCityActorMutationStatus.Applied));
            Assert.That(second.Status, Is.EqualTo(NCCityActorMutationStatus.Applied));
        });

        var hasMaterializedEntity = false;
        await server.WaitPost(() => hasMaterializedEntity =
            runtime.TryGetEntity(first.ActorId, out _) || runtime.TryGetEntity(second.ActorId, out _));
        Assert.That(hasMaterializedEntity, Is.False,
            "Persistent population projection must not materialize NPC entities.");

        var district = await WaitForZoneState(server, zoneStates, DistrictZone,
            record => record.Snapshot.Population.TotalActors == 2);
        var sector = await WaitForZoneState(server, zoneStates, SectorZone,
            record => record.Snapshot.Population.TotalActors == 2);
        Assert.Multiple(() =>
        {
            Assert.That(district.Snapshot.Population.LivingActors, Is.EqualTo(2));
            Assert.That(sector.Snapshot.Population.LivingActors, Is.EqualTo(2));
            Assert.That(sector.Snapshot.Population.Archetypes.Single().ArchetypeId,
                Is.EqualTo(Archetype.Id));
            Assert.That(sector.Snapshot.Signals.Single(signal => signal.Kind == NCCityStateSignalKind.Activity)
                .Availability, Is.EqualTo(NCCityStateSignalAvailability.Available));
            Assert.That(sector.Snapshot.Signals.Single(signal => signal.Kind == NCCityStateSignalKind.Scarcity)
                .Availability, Is.EqualTo(NCCityStateSignalAvailability.Unavailable));
        });

        var currentFirst = await actors.GetAsync(first.ActorId, true);
        Assert.That(currentFirst, Is.Not.Null);
        var dead = await actors.SetStatusAsync(new NCCityActorStatusMutationRequest(
            Guid.NewGuid(), first.ActorId, currentFirst!.AggregateVersion, NCCityActorStatus.Dead,
            Guid.NewGuid()));
        Assert.That(dead.Status, Is.EqualTo(NCCityActorMutationStatus.Applied));

        sector = await WaitForZoneState(server, zoneStates, SectorZone,
            record => record.Snapshot.Population.DeadActors == 1);
        Assert.Multiple(() =>
        {
            Assert.That(sector.Snapshot.Population.TotalActors, Is.EqualTo(2));
            Assert.That(sector.Snapshot.Population.LivingActors, Is.EqualTo(1));
            Assert.That(sector.Snapshot.Population.Statuses.Single(entry => entry.Status == NCCityActorStatus.Dead)
                .Count, Is.EqualTo(1));
        });

        var acceptedSignal = false;
        await server.WaitPost(() => acceptedSignal = projections.PublishSignal(new NCCityStateSignalUpdate(
            SectorZone,
            NCCityStateSignalKind.Scarcity,
            NCCityStateSignalAvailability.Available,
            0.75f,
            "living-city.economy.test",
            1)));
        Assert.That(acceptedSignal, Is.True);

        sector = await WaitForZoneState(server, zoneStates, SectorZone,
            record => record.Snapshot.Signals.Any(signal =>
                signal.Kind == NCCityStateSignalKind.Scarcity &&
                signal.Availability == NCCityStateSignalAvailability.Available));
        var scarcity = sector.Snapshot.Signals.Single(signal => signal.Kind == NCCityStateSignalKind.Scarcity);
        Assert.Multiple(() =>
        {
            Assert.That(scarcity.Value, Is.EqualTo(0.75f));
            Assert.That(scarcity.Source, Is.EqualTo("living-city.economy.test"));
            Assert.That(scarcity.SourceVersion, Is.EqualTo(1));
        });

        NCActiveWorldTime projectionTime = default;
        await server.WaitPost(() => Assert.That(clock.TryGetCurrentTime(out _, out projectionTime), Is.True));
        var replayRequest = new NCCityZoneStateMutationRequest(
            Guid.NewGuid(),
            sector.AggregateVersion,
            sector.Snapshot with
            {
                CapturedAt = projectionTime,
                LastAppliedEventId = Guid.NewGuid(),
            },
            Guid.NewGuid(),
            AuditSource: "integration-test");
        var replayApplied = await zoneStates.PersistAsync(replayRequest);
        var replayDuplicate = await zoneStates.PersistAsync(replayRequest);
        Assert.Multiple(() =>
        {
            Assert.That(replayApplied.Status, Is.EqualTo(NCCityZoneStateMutationStatus.Applied));
            Assert.That(replayDuplicate.Status, Is.EqualTo(NCCityZoneStateMutationStatus.Duplicate));
            Assert.That(replayDuplicate.AggregateVersion, Is.EqualTo(replayApplied.AggregateVersion));
            Assert.That(replayDuplicate.Record?.Snapshot.LastAppliedEventId,
                Is.EqualTo(replayApplied.Record?.Snapshot.LastAppliedEventId));
        });

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
    }

    [Test]
    public async Task PersistentOutboxRetriesRequeuesAndAcknowledgesOnlyInActiveRound()
    {
        const string messageType = "living-city.test.outbox-dispatch";
        var server = Pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var dispatcher = server.System<NCPersistentOutboxDispatcherSystem>();
        var handlerCalls = 0;
        NCPersistentOutboxHandler handler = (_, _) =>
        {
            var attempt = Interlocked.Increment(ref handlerCalls);
            return attempt == 1
                ? Task.FromException(new InvalidOperationException("simulated transient delivery failure"))
                : Task.CompletedTask;
        };

        await server.WaitAssertion(() => Assert.That(dispatcher.RegisterHandler(messageType, handler), Is.True));
        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady && dispatcher.IsActive,
            "The active-world outbox dispatcher did not start.");

        NCSessionEpoch epoch = default;
        NCActiveWorldTime now = default;
        await server.WaitPost(() => Assert.That(clock.TryGetCurrentTime(out epoch, out now), Is.True));
        var messageId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var aggregateId = $"outbox-dispatch-{Guid.NewGuid():N}";
        var result = await database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
            Guid.NewGuid(),
            "living-city.test.outbox-dispatch-command",
            "outbox-test",
            aggregateId,
            eventId,
            "living-city.test.outbox-dispatch-created",
            "outbox-test",
            aggregateId,
            0,
            1,
            "{\"state\":1}",
            "{\"change\":1}",
            "{\"accepted\":true}",
            Guid.NewGuid(),
            null,
            epoch,
            now,
            new[] { new NCOutboxWrite(messageId, messageType, "{\"kind\":\"test\"}") }));
        Assert.That(result.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));

        await server.WaitPost(dispatcher.Wake);
        await WaitUntilAsync(server, async () =>
        {
            var stored = await database.GetNCOutboxMessageAsync(messageId);
            return stored is { Attempts: 1, ProcessedAt: null };
        }, "The dispatcher did not persist its transient failure.", 300);

        Assert.That(await database.RequeueNCOutboxMessageAsync(messageId, DateTime.UtcNow, false), Is.True);
        await server.WaitPost(dispatcher.Wake);
        await WaitUntilAsync(server, async () =>
        {
            var stored = await database.GetNCOutboxMessageAsync(messageId);
            return stored?.ProcessedAt != null;
        }, "The requeued outbox message was not acknowledged.", 300);
        await WaitUntil(server, () => dispatcher.DeliveredThisSession == 1,
            "The dispatcher did not publish its successful delivery counter.", 300);

        var delivered = await database.GetNCOutboxMessageAsync(messageId);
        Assert.Multiple(() =>
        {
            Assert.That(handlerCalls, Is.EqualTo(2));
            Assert.That(delivered?.Attempts, Is.EqualTo(1));
            Assert.That(delivered?.ProcessedAt, Is.Not.Null);
            Assert.That(delivered?.LastError, Is.Null);
            Assert.That(dispatcher.FailedThisSession, Is.EqualTo(1));
            Assert.That(dispatcher.ExhaustedThisSession, Is.Zero);
        });

        await server.WaitAssertion(() => Assert.That(dispatcher.UnregisterHandler(messageType, handler), Is.True));
        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await WaitUntilAsync(server, async () =>
        {
            var stored = await database.GetNCActiveWorldClockAsync();
            return stored is { RoundActive: false } && stored.SessionEpoch == epoch;
        }, "The active-world epoch did not close.");
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

    private static async Task WaitUntilAsync(
        RobustIntegrationTest.ServerIntegrationInstance server,
        Func<Task<bool>> predicate,
        string failure,
        int attempts = 120)
    {
        for (var i = 0; i < attempts; i++)
        {
            await server.WaitRunTicks(1);
            if (await predicate())
                return;
        }

        Assert.Fail(failure);
    }

    private static async Task<NCCityActorRecord> WaitForActor(
        RobustIntegrationTest.ServerIntegrationInstance server,
        NCCityActorRegistrySystem registry,
        NCActorId actorId,
        Func<NCCityActorRecord, bool> predicate)
    {
        NCCityActorRecord last = null!;
        for (var i = 0; i < 180; i++)
        {
            await server.WaitRunTicks(1);
            var actor = await registry.GetAsync(actorId, true);
            last = actor;
            if (actor != null && predicate(actor))
                return actor;
        }

        Assert.Fail(
            $"Actor {actorId} did not reach the expected scheduled state. " +
            $"Last version={last?.AggregateVersion}, activity={last?.Snapshot.Schedule.CurrentActivity}, " +
            $"commitments={last?.Snapshot.Schedule.Commitments.Length}, " +
            $"phase={last?.Snapshot.Location.TravelPhase}, current={last?.Snapshot.Location.CurrentZone}, " +
            $"destination={last?.Snapshot.Location.DestinationZone}.");
        throw new InvalidOperationException();
    }

    private static async Task<NCCityZoneStateRecord> WaitForZoneState(
        RobustIntegrationTest.ServerIntegrationInstance server,
        NCCityZoneStateRegistrySystem registry,
        NCZoneId scopeZone,
        Func<NCCityZoneStateRecord, bool> predicate)
    {
        NCCityZoneStateRecord? last = null;
        for (var i = 0; i < 360; i++)
        {
            await server.WaitRunTicks(1);
            last = await registry.GetAsync(scopeZone, true);
            if (last != null && predicate(last))
                return last;
        }

        Assert.Fail(
            $"Zone state {scopeZone} did not reach the expected projection. " +
            $"Last version={last?.AggregateVersion}, total={last?.Snapshot.Population.TotalActors}, " +
            $"living={last?.Snapshot.Population.LivingActors}, dead={last?.Snapshot.Population.DeadActors}.");
        throw new InvalidOperationException();
    }
}
