// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.GameRules.Components;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Neural;
using Content.Server._NC.LivingCity.Time;
using Content.Server.GameTicking.Rules;
using Content.Server.Spawners.Components;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.GameTicking.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Clothing;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.GameRules;

/// <summary>
/// Builds an isolated, persistent cohort of physical GPT NPCs on Reach. The rule only assigns vanilla job identity,
/// access and equipment; ordinary intent selection remains exclusively in the production physical-actor brain.
/// </summary>
public sealed partial class NCReachNpcTestRuleSystem : GameRuleSystem<NCReachNpcTestRuleComponent>
{
    public const int ExpectedNpcCount = 30;
    private const int MaximumReplacementGenerations = 1024;
    private static readonly ProtoId<NCCityActorArchetypePrototype> TestArchetype = new("NCReachTestWorker");

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private StationSystem _stations = default!;
    [Dependency] private StationSpawningSystem _stationSpawning = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCRandomResidentFactorySystem _factory = default!;
    [Dependency] private NCPhysicalActorLifecycleSystem _physicalActors = default!;
    [Dependency] private NCPhysicalActorBrainSystem _brain = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedIdCardSystem _idCards = default!;
    [Dependency] private MetaDataSystem _metaData = default!;

    private readonly HashSet<EntityUid> _activeRules = [];
    private readonly Dictionary<NCActorId, TestAssignment> _plannedAssignments = [];
    private readonly Dictionary<NCActorId, TestAssignment> _assignments = [];
    private readonly Dictionary<NCActorId, Guid> _spawnRequests = [];
    private readonly HashSet<NCActorId> _readyActors = [];
    private readonly HashSet<int> _failedSlots = [];
    private NCSessionEpoch? _runningEpoch;
    private int _generation;
    private bool _spawnPhaseStarted;

    public bool IsActive => _activeRules.Count != 0;
    public int CohortCount => _assignments.Count;
    public int ReadyNpcCount => _readyActors.Count;
    public int FailedNpcCount => _failedSlots.Count;
    public string? ConfigurationError { get; private set; }

    public IReadOnlyList<NCPhysicalActorSpawnState> GetSpawnStates()
    {
        var states = new List<NCPhysicalActorSpawnState>(_spawnRequests.Count);
        foreach (var requestId in _spawnRequests.Values)
        {
            if (_physicalActors.TryGetRequest(requestId, out var state))
                states.Add(state);
        }
        return states;
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => ResetSession());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => ResetSession());
        SubscribeLocalEvent<NCPhysicalActorCatalogBootstrapAttemptEvent>(OnCatalogBootstrapAttempt);
        SubscribeLocalEvent<NCPhysicalActorSpawnedEvent>(OnPhysicalActorSpawned);
    }

    public override void Shutdown()
    {
        _activeRules.Clear();
        ResetSession();
        base.Shutdown();
    }

    protected override void Started(
        EntityUid uid,
        NCReachNpcTestRuleComponent component,
        GameRuleComponent gameRule,
        GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);
        _activeRules.Add(uid);

        // Normally the durable session starts after the rule. This also supports starting the test rule mid-round.
        if (_clock.TryGetCurrentTime(out var epoch, out _))
            BeginCohort(epoch, component);
    }

    protected override void Ended(
        EntityUid uid,
        NCReachNpcTestRuleComponent component,
        GameRuleComponent gameRule,
        GameRuleEndedEvent args)
    {
        base.Ended(uid, component, gameRule, args);
        _activeRules.Remove(uid);
        if (!IsActive)
            ResetSession();
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        if (!TryGetActiveRule(out _, out var component))
            return;

        BeginCohort(args.Epoch, component);
    }

    private void BeginCohort(NCSessionEpoch epoch, NCReachNpcTestRuleComponent component)
    {
        if (_runningEpoch == epoch)
            return;

        ResetSession();
        _runningEpoch = epoch;
        if (!TryBuildSpawnPlan(component, out var plans, out var error))
        {
            ConfigurationError = error;
            for (var slot = 0; slot < ExpectedNpcCount; slot++)
                _failedSlots.Add(slot);
            Log.Error($"Reach NPC test cohort was not started: {error}");
            return;
        }

        var generation = _generation;
        foreach (var plan in plans)
            BeginActorLookup(generation, component.GenerationProfile, plan, 0);
    }

    private bool TryBuildSpawnPlan(
        NCReachNpcTestRuleComponent component,
        out List<TestSlotPlan> plans,
        out string? error)
    {
        plans = [];
        error = null;
        var configuredCount = 0;
        foreach (var (jobId, count) in component.Roster)
        {
            if (count <= 0 || !_prototypes.TryIndex(jobId, out JobPrototype? job) || job == null ||
                job.JobEntity != null)
            {
                error = $"Roster job {jobId} is missing, non-humanoid, or has invalid count {count}.";
                return false;
            }

            configuredCount += count;
        }

        if (component.NpcCount != ExpectedNpcCount || configuredCount != ExpectedNpcCount ||
            component.SpawnRadius <= 0f || !float.IsFinite(component.SpawnRadius) ||
            !_prototypes.HasIndex(component.GenerationProfile))
        {
            error = $"The Reach test roster must define exactly {ExpectedNpcCount} NPCs and a valid generation profile.";
            return false;
        }

        var byStation = new Dictionary<EntityUid, Dictionary<ProtoId<JobPrototype>, List<MapCoordinates>>>();
        var query = EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var spawnPoint, out var transform))
        {
            if (spawnPoint.SpawnType != SpawnPointType.Job || spawnPoint.Job is not { } job ||
                !component.Roster.ContainsKey(job) ||
                _stations.GetOwningStation(uid, transform) is not { } station ||
                !HasComp<StationJobsComponent>(station))
            {
                continue;
            }

            var coordinates = _transform.GetMapCoordinates(uid, transform);
            if (coordinates.MapId == MapId.Nullspace)
                continue;

            if (!byStation.TryGetValue(station, out var stationPoints))
            {
                stationPoints = [];
                byStation.Add(station, stationPoints);
            }

            if (!stationPoints.TryGetValue(job, out var jobPoints))
            {
                jobPoints = [];
                stationPoints.Add(job, jobPoints);
            }

            jobPoints.Add(coordinates);
        }

        var selected = byStation
            .Where(entry => component.Roster.Keys.All(entry.Value.ContainsKey))
            .OrderByDescending(entry => entry.Value.Sum(points => points.Value.Count))
            .FirstOrDefault();
        if (selected.Value == null)
        {
            error = "No single station has authored job spawn points for the complete Reach test roster.";
            return false;
        }

        var slot = 0;
        foreach (var (job, count) in component.Roster)
        {
            var points = selected.Value[job];
            RobustRandom.Shuffle(points);
            for (var index = 0; index < count; index++)
            {
                plans.Add(new TestSlotPlan(
                    slot++,
                    job,
                    selected.Key,
                    points[index % points.Count],
                    component.SpawnRadius));
            }
        }

        return true;
    }

    private void BeginActorLookup(
        int generation,
        ProtoId<NCRandomResidentGenerationProfilePrototype> profile,
        TestSlotPlan plan,
        int replacementGeneration)
    {
        if (replacementGeneration >= MaximumReplacementGenerations)
        {
            RecordFailure(plan.Slot, "the stable actor replacement range was exhausted");
            return;
        }

        var actorId = ActorIdFor(plan.Slot, replacementGeneration);
        _plannedAssignments[actorId] = AssignmentFor(plan);
        Task<NCCityActorRecord?> task;
        try
        {
            task = _actors.GetAsync(actorId, true);
        }
        catch (Exception exception)
        {
            CompleteActorLookup(generation, profile, plan, replacementGeneration, actorId, null, exception);
            return;
        }

        _ = ObserveActorLookupAsync(generation, profile, plan, replacementGeneration, actorId, task);
    }

    private async Task ObserveActorLookupAsync(
        int generation,
        ProtoId<NCRandomResidentGenerationProfilePrototype> profile,
        TestSlotPlan plan,
        int replacementGeneration,
        NCActorId actorId,
        Task<NCCityActorRecord?> task)
    {
        NCCityActorRecord? record = null;
        Exception? error = null;
        try
        {
            record = await task;
        }
        catch (Exception exception)
        {
            error = exception;
        }

        _taskManager.RunOnMainThread(() =>
            CompleteActorLookup(generation, profile, plan, replacementGeneration, actorId, record, error));
    }

    private void CompleteActorLookup(
        int generation,
        ProtoId<NCRandomResidentGenerationProfilePrototype> profile,
        TestSlotPlan plan,
        int replacementGeneration,
        NCActorId actorId,
        NCCityActorRecord? record,
        Exception? error)
    {
        if (!Current(generation))
            return;
        if (error != null)
        {
            if (_assignments.ContainsKey(actorId))
                return;
            _plannedAssignments.Remove(actorId);
            RecordFailure(plan.Slot, $"persistent actor lookup failed: {error.Message}");
            return;
        }

        if (record != null)
        {
            if (record.Snapshot.Identity.ArchetypeId != TestArchetype.Id ||
                record.Snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived)
            {
                _plannedAssignments.Remove(actorId);
                BeginActorLookup(generation, profile, plan, replacementGeneration + 1);
                return;
            }

            QueuePhysicalActor(generation, AssignmentFor(plan), actorId);
            return;
        }

        Task<NCRandomResidentCreationResult> createTask;
        try
        {
            // Invoke generation on the ECS thread; only its durable write continues asynchronously.
            createTask = _factory.CreatePersistentAsync(profile, actorId, "reach-npc-test-rule");
        }
        catch (Exception exception)
        {
            CompleteActorCreation(generation, plan, actorId, null, exception);
            return;
        }

        _ = ObserveActorCreationAsync(generation, plan, actorId, createTask);
    }

    private async Task ObserveActorCreationAsync(
        int generation,
        TestSlotPlan plan,
        NCActorId actorId,
        Task<NCRandomResidentCreationResult> task)
    {
        NCRandomResidentCreationResult? result = null;
        Exception? error = null;
        try
        {
            result = await task;
        }
        catch (Exception exception)
        {
            error = exception;
        }

        _taskManager.RunOnMainThread(() => CompleteActorCreation(generation, plan, actorId, result, error));
    }

    private void CompleteActorCreation(
        int generation,
        TestSlotPlan plan,
        NCActorId actorId,
        NCRandomResidentCreationResult? result,
        Exception? error)
    {
        if (!Current(generation))
            return;
        if (error != null || result is not { Success: true })
        {
            if (_assignments.ContainsKey(actorId))
                return;
            _plannedAssignments.Remove(actorId);
            RecordFailure(plan.Slot, error?.Message ?? result?.Error ?? "persistent actor creation failed");
            return;
        }

        QueuePhysicalActor(generation, AssignmentFor(plan), actorId);
    }

    private void QueuePhysicalActor(int generation, TestAssignment assignment, NCActorId actorId)
    {
        if (!Current(generation) || !_assignments.TryAdd(actorId, assignment))
            return;

        // Finish every durable create/read before lifecycle starts its first domain load. This keeps SQLite and
        // production databases out of a cold-start read/write convoy while preserving the lifecycle's own queue.
        TryStartPhysicalCohort(generation);
    }

    private void TryStartPhysicalCohort(int generation)
    {
        if (!Current(generation) || _spawnPhaseStarted ||
            _assignments.Count + _failedSlots.Count != ExpectedNpcCount)
        {
            return;
        }

        _spawnPhaseStarted = true;
        foreach (var (actorId, assignment) in _assignments.ToArray())
        {
            var requestId = Guid.NewGuid();
            _spawnRequests.Add(actorId, requestId);
            var state = _physicalActors.Request(new NCPhysicalActorSpawnRequest(
                requestId,
                actorId,
                NCPhysicalActorSpawnReason.ServiceDispatch,
                "reach-npc-test-rule",
                assignment.Target,
                assignment.SpawnRadius));
            if (state.Status == NCPhysicalActorSpawnStatus.Rejected)
            {
                _assignments.Remove(actorId);
                RecordFailure(assignment.Slot, state.Error ?? "physical actor spawn was rejected");
                continue;
            }

            if (state.EntityUid is { } entity)
                TryAssignJob(actorId, entity);
        }
    }

    private void OnPhysicalActorSpawned(NCPhysicalActorSpawnedEvent args)
    {
        if (_assignments.ContainsKey(args.ActorId))
            TryAssignJob(args.ActorId, args.EntityUid);
    }

    private void TryAssignJob(NCActorId actorId, EntityUid entity)
    {
        if (!_assignments.TryGetValue(actorId, out var assignment) || _readyActors.Contains(actorId) || !Exists(entity))
            return;

        try
        {
            var marker = EnsureComp<NCNpcJobComponent>(entity);
            marker.Job = assignment.Job;
            marker.IgnoreUntrackedInventory = true;

            var inventory = EnsureComp<NCCityActorInventoryAdapterComponent>(entity);
            inventory.EquippedLoadoutId = assignment.Job.Id;

            EquipDefaultRoleLoadout(entity, assignment.Job);
            var spawned = _stationSpawning.SpawnPlayerMob(
                Transform(entity).Coordinates,
                assignment.Job,
                null,
                assignment.Station,
                entity);
            if (spawned != entity)
                throw new InvalidOperationException("Vanilla job setup replaced the persistent physical actor body.");
            if (!_idCards.TryFindIdCard(entity, out var idCard))
                throw new InvalidOperationException($"Vanilla job setup did not issue a {assignment.Job} ID card.");

            // Player ID cards resolve their job through a station record. Physical NPCs deliberately have no player
            // record, so store the documented fallback prototype on the otherwise vanilla-issued card.
            idCard.Comp.JobPrototype = assignment.Job;
            if (_prototypes.TryIndex(assignment.Job, out JobPrototype? job) && job != null)
                _idCards.TryChangeJobDepartment(idCard.Owner, job, idCard.Comp);
            Dirty(idCard.Owner, idCard.Comp);
            if (idCard.Comp.JobPrototype != assignment.Job)
                throw new InvalidOperationException($"The {assignment.Job} ID card has no NPC job identity.");
            var equipped = 0;
            var slots = _inventory.GetSlotEnumerator(entity);
            while (slots.NextItem(out _, out _))
                equipped++;
            if (equipped == 0)
                throw new InvalidOperationException($"Vanilla job setup did not equip the {assignment.Job} loadout.");

            _readyActors.Add(actorId);
            _brain.RequestDecision(actorId);
        }
        catch (Exception exception)
        {
            RecordFailure(assignment.Slot, $"job setup failed: {exception.Message}");
        }
    }

    private void EquipDefaultRoleLoadout(EntityUid entity, ProtoId<JobPrototype> job)
    {
        var roleId = LoadoutSystem.GetJobPrototype(job.Id);
        if (!_prototypes.TryIndex(roleId, out RoleLoadoutPrototype? role) || role == null)
            return;

        // SpawnPlayerMob cannot infer role-loadout defaults without a player profile. Select the same default groups
        // against a neutral profile, equip them through vanilla authority, then preserve the persistent actor's name.
        var originalName = Name(entity);
        var loadout = new RoleLoadout(roleId);
        loadout.SetDefault(new HumanoidCharacterProfile(), null, _prototypes);
        _stationSpawning.EquipRoleLoadout(entity, loadout, role);
        _metaData.SetEntityName(entity, originalName);
    }

    private void OnCatalogBootstrapAttempt(NCPhysicalActorCatalogBootstrapAttemptEvent args)
    {
        // Intercept only an actor already planned by this running rule, then immediately replace the generic
        // bootstrap with the authored job spawn. All other living actors retain the one-body invariant.
        if (!IsActive || args.Archetype != TestArchetype ||
            !_plannedAssignments.TryGetValue(args.ActorId, out var assignment))
        {
            return;
        }

        args.Cancel();
        QueuePhysicalActor(_generation, assignment, args.ActorId);
    }

    private bool TryGetActiveRule(out EntityUid uid, out NCReachNpcTestRuleComponent component)
    {
        foreach (var candidate in _activeRules)
        {
            if (TryComp<NCReachNpcTestRuleComponent>(candidate, out var found) && found != null)
            {
                uid = candidate;
                component = found;
                return true;
            }
        }

        uid = default;
        component = default!;
        return false;
    }

    private bool Current(int generation) => generation == _generation && IsActive && _runningEpoch != null;

    private void RecordFailure(int slot, string error)
    {
        if (!_failedSlots.Add(slot))
            return;

        Log.Error($"Reach NPC test slot {slot} failed: {error}");
        TryStartPhysicalCohort(_generation);
    }

    private void ResetSession()
    {
        _generation++;
        _runningEpoch = null;
        _plannedAssignments.Clear();
        _assignments.Clear();
        _spawnRequests.Clear();
        _readyActors.Clear();
        _failedSlots.Clear();
        ConfigurationError = null;
        _spawnPhaseStarted = false;
    }

    private static NCActorId ActorIdFor(int slot, int replacementGeneration) =>
        new(NCPersistentIdentity.Command(
            "reach-npc-test",
            "actor",
            $"slot-{slot}-generation-{replacementGeneration}"));

    private static TestAssignment AssignmentFor(TestSlotPlan plan) =>
        new(plan.Slot, plan.Job, plan.Station, plan.Target, plan.SpawnRadius);

    private sealed record TestSlotPlan(
        int Slot,
        ProtoId<JobPrototype> Job,
        EntityUid Station,
        MapCoordinates Target,
        float SpawnRadius);

    private sealed record TestAssignment(
        int Slot,
        ProtoId<JobPrototype> Job,
        EntityUid Station,
        MapCoordinates Target,
        float SpawnRadius);
}
