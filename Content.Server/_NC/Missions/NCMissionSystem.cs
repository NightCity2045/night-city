// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._NC.City.ZoneTemplates;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.ZoneTemplates.Components;
using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.Missions;
using Content.Shared._NC.Missions.Components;
using Content.Shared._NC.Missions.Prototypes;
using Content.Shared._NC.Identity;
using Content.Shared.Doors;
using Content.Shared.GameTicking;
using Content.Shared.Movement.Events;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Missions;

/// <summary>
/// Owns temporary public-mission state while NCZoneTemplateSystem owns physical deployment and rollback.
/// One stable slot may have at most one run, regardless of its number of Z-level fragments.
/// </summary>
public sealed partial class NCMissionSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private City.Zones.NCZoneSystem _zones = default!;
    [Dependency] private NCZoneTemplateSystem _templates = default!;
    [Dependency] private NCZoneOccupancySystem _occupancy = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IGameTiming _timing = default!;

    private static readonly TimeSpan LifecycleUpdateInterval = TimeSpan.FromSeconds(1);
    private readonly Dictionary<NCMissionRunId, MissionRun> _runs = [];
    private readonly Dictionary<NCMissionSlotId, NCMissionRunId> _activeBySlot = [];
    private readonly Dictionary<NCZoneDeploymentId, NCMissionRunId> _runsByDeployment = [];
    private readonly Dictionary<NCZoneDeploymentId, HashSet<EntityUid>> _dynamicPoints = [];
    private readonly Dictionary<NCZoneDeploymentId, EntityUid> _evacuationPoints = [];
    private readonly Dictionary<NCMissionSlotId, HashSet<EntityUid>> _entrances = [];
    private readonly HashSet<EntityUid> _deploymentEntityBuffer = [];
    private readonly HashSet<EntityUid> _evacuees = [];
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private TimeSpan _nextLifecycleUpdate;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PostGameMapLoad>(OnPostGameMapLoad);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<NCZoneDeploymentStateChangedEvent>(OnDeploymentStateChanged);
        SubscribeLocalEvent<NCMissionEntranceComponent, ComponentStartup>(OnEntranceStartup);
        SubscribeLocalEvent<NCMissionEntranceComponent, ComponentShutdown>(OnEntranceShutdown);
        SubscribeLocalEvent<NCMissionEntranceComponent, BeforeDoorOpenedEvent>(OnBeforeEntranceOpened);
        SubscribeLocalEvent<NCMissionRunEntityComponent, MoveEvent>(OnRunEntityMoved);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_timing.CurTime < _nextLifecycleUpdate)
            return;
        _nextLifecycleUpdate = _timing.CurTime + LifecycleUpdateInterval;

        var now = DateTime.UtcNow;
        foreach (var run in _runs.Values.ToArray())
        {
            if (run.State == NCMissionRunState.Active && now >= run.ExpiresAt)
                BeginTimedExpiration(run, now);

            if (run.State != NCMissionRunState.Closing || run.CleanupRequested)
                continue;
            if (_occupancy.IsOccupied(run.SlotId.ZoneId, out _))
                continue;
            ExtractEligibleLoot(run);
            if (!_templates.TryCloseDeployment(run.DeploymentId, out var error))
            {
                run.Error = error;
                continue;
            }

            run.CleanupRequested = true;
            run.State = NCMissionRunState.Cleaning;
            _ = PersistStateAsync(run);
            NotifyChanged(run);
        }
    }

    public async Task<NCMissionStartResult> TryStartRunAsync(
        ProtoId<NCMissionDefinitionPrototype> definitionId,
        IReadOnlyCollection<NCCharacterId> participants,
        NCMissionSlotId? requestedSlot = null,
        int? seed = null)
    {
        await _startGate.WaitAsync();
        try
        {
            return await TryStartRunLockedAsync(definitionId, participants, requestedSlot, seed);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<NCMissionStartResult> TryStartRunLockedAsync(
        ProtoId<NCMissionDefinitionPrototype> definitionId,
        IReadOnlyCollection<NCCharacterId> participants,
        NCMissionSlotId? requestedSlot,
        int? seed)
    {
        if (!_prototypes.TryIndex(definitionId, out var definition) ||
            !_prototypes.TryIndex(definition.Template, out NCZoneTemplatePrototype? template))
            return new(false, default, "Mission definition or template does not exist.");
        if (definition.RunDuration <= TimeSpan.Zero || definition.EvacuationTime < TimeSpan.Zero ||
            definition.EvacuationTime > definition.RunDuration)
            return new(false, default, "Mission lifetime or warning period is invalid.");
        if (participants.Count == 0 || participants.Count > definition.MaximumParticipants ||
            participants.Any(character => !character.IsValid))
            return new(false, default, "Mission participant list is invalid.");

        if (!TryAllocateSlot(template, requestedSlot, out var slot, out var error))
            return new(false, default, error);
        if (_occupancy.IsOccupied(slot.ZoneId, out var blocker))
            return new(false, default, $"Mission slot is occupied by {ToPrettyString(blocker)}.");

        var runId = NCMissionRunId.New();
        var deploymentId = NCZoneDeploymentId.New();
        var run = new MissionRun(
            runId,
            slot,
            definitionId,
            definition.Template,
            deploymentId,
            participants.ToHashSet(),
            seed ?? Random.Shared.Next(),
            DateTime.UtcNow,
            definition.RunDuration,
            definition.EvacuationTime);
        var persisted = await _database.TryCreateNCMissionRunAsync(ToData(run, template.Revision));
        if (persisted == null)
            return new(false, default, "Mission slot reservation conflicted in persistent storage.");
        run.Version = persisted.Version;
        _runs.Add(runId, run);
        _activeBySlot.Add(slot, runId);
        _runsByDeployment.Add(deploymentId, runId);
        SetEntrances(slot, false);

        var request = new NCZoneDeploymentRequest(
            deploymentId,
            slot.ZoneId,
            definition.Template,
            NCZoneDeploymentPurpose.MissionRun,
            null,
            runId.Value);
        if (_templates.TryRequestDeployment(request, out _, out error))
        {
            run.State = NCMissionRunState.Preparing;
            _ = PersistStateAsync(run);
            NotifyChanged(run);
            return new(true, runId, string.Empty);
        }

        _runs.Remove(runId);
        _activeBySlot.Remove(slot);
        _runsByDeployment.Remove(deploymentId);
        run.State = NCMissionRunState.Failed;
        run.Result = NCMissionRunResult.Failure;
        run.CompletedAt = DateTime.UtcNow;
        run.Error = error;
        await PersistStateAsync(run);
        NotifyChanged(run);
        return new(false, default, error);
    }

    public bool TryFinishRun(NCMissionRunId runId, NCMissionRunResult result, out string error)
    {
        error = string.Empty;
        if (!_runs.TryGetValue(runId, out var run) || run.State != NCMissionRunState.Active ||
            result == NCMissionRunResult.None)
        {
            error = "Mission run is not active or the result is invalid.";
            return false;
        }

        run.Result = result;
        run.CompletedAt = DateTime.UtcNow;
        run.State = NCMissionRunState.Closing;
        SetEntrances(run.SlotId, false);
        _ = PersistStateAsync(run);
        NotifyChanged(run);
        return true;
    }

    public bool TryGetRun(NCMissionRunId runId, out NCMissionRunStatus status)
    {
        if (!_runs.TryGetValue(runId, out var run))
        {
            status = default;
            return false;
        }

        status = new NCMissionRunStatus(
            run.Id,
            run.SlotId,
            run.TemplateId.Id,
            run.DeploymentId,
            run.State,
            run.Participants,
            run.Seed,
            run.StartedAt,
            run.ExpiresAt,
            run.WarningAt,
            run.CompletedAt,
            run.Result,
            run.Error) { DefinitionId = run.DefinitionId.Id };
        return true;
    }

    /// <summary>Returns the one active or recovering public run reserved for a stable city slot.</summary>
    public bool TryGetRunForSlot(NCMissionSlotId slotId, out NCMissionRunStatus status)
    {
        if (!_activeBySlot.TryGetValue(slotId, out var runId))
        {
            status = default;
            return false;
        }

        return TryGetRun(runId, out status);
    }

    private bool TryAllocateSlot(
        NCZoneTemplatePrototype template,
        NCMissionSlotId? requested,
        out NCMissionSlotId slot,
        out string error)
    {
        error = string.Empty;
        if (requested is { } exact)
        {
            slot = exact;
            if (exact.ZoneId != template.TargetZoneId ||
                !_zones.TryGetMissionSlot(exact, out var zone) ||
                !template.CompatibleZoneKinds.Contains(zone.Kind) || _activeBySlot.ContainsKey(exact))
            {
                error = "Requested mission slot is unavailable or incompatible.";
                return false;
            }
            return true;
        }

        var direct = new NCMissionSlotId(template.TargetZoneId);
        if (!_activeBySlot.ContainsKey(direct) &&
            _zones.TryGetMissionSlot(direct, out var directZone) &&
            template.CompatibleZoneKinds.Contains(directZone.Kind))
        {
            slot = direct;
            return true;
        }

        slot = default;
        error = "No compatible mission slot is free.";
        return false;
    }

    private void OnDeploymentStateChanged(ref NCZoneDeploymentStateChangedEvent ev)
    {
        if (!_runsByDeployment.TryGetValue(ev.DeploymentId, out var runId) || !_runs.TryGetValue(runId, out var run))
            return;

        if (ev.NewState == NCZoneDeploymentState.Active)
        {
            IndexMissionSemantics(run.DeploymentId);
            if (run.RecoveryShouldClose)
            {
                run.State = NCMissionRunState.Closing;
                SetEntrances(run.SlotId, false);
                _ = PersistStateAsync(run);
                NotifyChanged(run);
                return;
            }

            run.State = NCMissionRunState.Active;
            if (DateTime.UtcNow >= run.ExpiresAt)
            {
                BeginTimedExpiration(run, DateTime.UtcNow);
                return;
            }

            SpawnDynamicContent(run);
            SetEntrances(run.SlotId, true);
            _ = PersistStateAsync(run);
            NotifyChanged(run);
            return;
        }

        if (ev.NewState == NCZoneDeploymentState.FailedClean)
        {
            run.State = NCMissionRunState.Failed;
            run.Result = NCMissionRunResult.Failure;
            run.CompletedAt = DateTime.UtcNow;
            run.Error = ev.Error;
            _ = PersistStateAsync(run);
            ReleaseRun(run);
            NotifyChanged(run);
            return;
        }

        if (ev.NewState == NCZoneDeploymentState.Completed)
        {
            run.State = NCMissionRunState.Closed;
            _ = PersistStateAsync(run);
            ReleaseRun(run);
            NotifyChanged(run);
        }
    }

    private void SpawnDynamicContent(MissionRun run)
    {
        if (!_prototypes.TryIndex(run.DefinitionId, out var definition) ||
            !_dynamicPoints.TryGetValue(run.DeploymentId, out var points))
            return;

        var random = new Random(run.Seed);
        foreach (var point in points.OrderBy(uid => uid.Id))
        {
            if (!TryComp(point, out NCMissionDynamicPointComponent? marker) ||
                !definition.DynamicSpawnPools.TryGetValue(marker.PoolId, out var pool) || pool.Count == 0)
                continue;

            var spawned = Spawn(pool[random.Next(pool.Count)], Transform(point).Coordinates);
            AddComp(spawned, new NCZoneDeploymentComponent
            {
                DeploymentId = run.DeploymentId.Value,
                TargetZoneId = run.SlotId.ZoneId,
                TemplateId = run.TemplateId,
                TemplateRevision = _prototypes.Index(run.TemplateId).Revision,
                CleanupPolicy = NCZoneTemplateCleanupPolicy.DeleteDeploymentEntities,
            });
            AddComp(spawned, new NCMissionRunEntityComponent
            {
                RunId = run.Id.Value,
                DeploymentId = run.DeploymentId.Value,
                ExtractionPolicy = marker.ExtractionPolicy,
            });
        }
    }

    private void OnRunEntityMoved(Entity<NCMissionRunEntityComponent> entity, ref MoveEvent args)
    {
        if (entity.Comp.ExtractionPolicy != NCMissionExtractionPolicy.CanBeExtracted ||
            !_runs.TryGetValue(new NCMissionRunId(entity.Comp.RunId), out var run) ||
            !_coordinates.TryGetCoordinates(entity.Owner, out var coordinates) ||
            _zones.Contains(run.SlotId.ZoneId, coordinates))
            return;

        entity.Comp.ExtractionPolicy = NCMissionExtractionPolicy.PersistentWorldItem;
        Dirty(entity, entity.Comp);
        RemCompDeferred<NCZoneDeploymentComponent>(entity.Owner);
    }

    /// <summary>
    /// Rechecks the bounded deployment set immediately before cleanup. Contained loot does not necessarily receive
    /// a MoveEvent when its carrier crosses the zone boundary, so this pass protects valid player extraction.
    /// </summary>
    private void ExtractEligibleLoot(MissionRun run)
    {
        if (!_templates.CollectDeploymentEntities(run.DeploymentId, _deploymentEntityBuffer))
            return;

        foreach (var uid in _deploymentEntityBuffer)
        {
            if (!TryComp(uid, out NCMissionRunEntityComponent? missionEntity) ||
                missionEntity.ExtractionPolicy != NCMissionExtractionPolicy.CanBeExtracted ||
                !_coordinates.TryGetCoordinates(uid, out var coordinates) ||
                _zones.Contains(run.SlotId.ZoneId, coordinates))
                continue;

            missionEntity.ExtractionPolicy = NCMissionExtractionPolicy.PersistentWorldItem;
            Dirty(uid, missionEntity);
            RemComp<NCZoneDeploymentComponent>(uid);
        }
    }

    /// <summary>
    /// Indexes only the completed deployment's bounded entity set. Prepared entities receive MapInit before
    /// deployment provenance, so indexing at the Active transition is the first reliable semantic boundary.
    /// </summary>
    private void IndexMissionSemantics(NCZoneDeploymentId deployment)
    {
        _dynamicPoints.Remove(deployment);
        _evacuationPoints.Remove(deployment);
        if (!_templates.CollectDeploymentEntities(deployment, _deploymentEntityBuffer))
            return;

        foreach (var uid in _deploymentEntityBuffer)
        {
            if (HasComp<NCMissionDynamicPointComponent>(uid))
            {
                if (!_dynamicPoints.TryGetValue(deployment, out var points))
                    _dynamicPoints[deployment] = points = [];
                points.Add(uid);
            }

            if (HasComp<NCMissionEvacuationPointComponent>(uid) &&
                !_evacuationPoints.TryAdd(deployment, uid))
                Log.Error($"Mission deployment {deployment} contains more than one evacuation point.");
        }
    }

    /// <summary>
    /// A public mission has a hard lifetime. External actors and mobs are moved to the mapper-selected
    /// safe tile before cleanup, while deployment-owned enemies remain part of the deletion target.
    /// </summary>
    private void BeginTimedExpiration(MissionRun run, DateTime now)
    {
        run.Result = NCMissionRunResult.Aborted;
        run.CompletedAt = now;
        run.State = NCMissionRunState.Closing;
        SetEntrances(run.SlotId, false);
        if (!TryEvacuateExternalOccupants(run, out var error))
            run.Error = error;
        _ = PersistStateAsync(run);
        NotifyChanged(run);
    }

    private bool TryEvacuateExternalOccupants(MissionRun run, out string error)
    {
        error = string.Empty;
        if (!_occupancy.CollectExternalMobOccupants(run.SlotId.ZoneId, _evacuees))
        {
            error = "Mission occupants could not be resolved for timed evacuation.";
            return false;
        }

        if (_evacuees.Count == 0)
            return true;
        if (!_evacuationPoints.TryGetValue(run.DeploymentId, out var marker) ||
            !TryComp(marker, out NCMissionEvacuationPointComponent? evacuationPoint) ||
            !_coordinates.TryGetCoordinates(marker, out var markerCoordinates))
        {
            error = "Mission evacuation point is missing or cannot be resolved.";
            return false;
        }

        var persistentTarget = new Content.Shared._NC.Coordinates.NCMapCoordinates(
            markerCoordinates.NetworkId,
            markerCoordinates.Position + evacuationPoint.ExitOffset,
            markerCoordinates.Z);
        if (!_coordinates.TryResolve(persistentTarget, out var target))
        {
            error = "Mission evacuation destination cannot be resolved.";
            return false;
        }

        foreach (var uid in _evacuees)
        {
            if (Exists(uid))
                _transform.SetMapCoordinates(uid, target);
        }

        return true;
    }

    private void OnEntranceStartup(Entity<NCMissionEntranceComponent> entity, ref ComponentStartup args)
    {
        IndexEntrance(entity);
        entity.Comp.Open = false;
        Dirty(entity, entity.Comp);
    }

    private void OnEntranceShutdown(Entity<NCMissionEntranceComponent> entity, ref ComponentShutdown args)
    {
        RemoveEntrance(entity.Owner);
    }

    private void IndexEntrance(Entity<NCMissionEntranceComponent> entity)
    {
        // Invalid serialized IDs are ignored defensively; valid layouts bind directly to their mission zone.
        if (!entity.Comp.SlotId.IsValid)
            return;

        if (!_entrances.TryGetValue(entity.Comp.SlotId, out var entrances))
            _entrances[entity.Comp.SlotId] = entrances = [];
        entrances.Add(entity.Owner);
    }

    private void RemoveEntrance(EntityUid uid)
    {
        foreach (var (slotId, entrances) in _entrances.ToArray())
        {
            entrances.Remove(uid);
            if (entrances.Count == 0)
                _entrances.Remove(slotId);
        }
    }

    private void OnBeforeEntranceOpened(Entity<NCMissionEntranceComponent> entity, ref BeforeDoorOpenedEvent args)
    {
        if (!entity.Comp.Open)
            args.Cancel();
    }

    private void SetEntrances(NCMissionSlotId slot, bool open)
    {
        if (!_entrances.TryGetValue(slot, out var entrances))
            return;
        foreach (var uid in entrances.ToArray())
        {
            if (!TryComp(uid, out NCMissionEntranceComponent? entrance))
                continue;
            entrance.Open = open;
            Dirty(uid, entrance);
        }
    }

    private void ReleaseRun(MissionRun run)
    {
        SetEntrances(run.SlotId, false);
        _activeBySlot.Remove(run.SlotId);
        _runsByDeployment.Remove(run.DeploymentId);
        _dynamicPoints.Remove(run.DeploymentId);
        _evacuationPoints.Remove(run.DeploymentId);
    }

    private void NotifyChanged(MissionRun run)
    {
        var changed = new NCMissionRunChangedEvent(run.SlotId);
        RaiseLocalEvent(ref changed);
    }

    private async void OnPostGameMapLoad(PostGameMapLoad ev)
    {
        try
        {
            foreach (var data in await _database.GetRecoverableNCMissionRunsAsync())
            {
                if (_runs.ContainsKey(data.RunId) || _activeBySlot.ContainsKey(data.SlotId) ||
                    !_prototypes.TryIndex(data.DefinitionId, out NCMissionDefinitionPrototype? definition))
                    continue;

                var run = new MissionRun(
                    data.RunId,
                    data.SlotId,
                    data.DefinitionId,
                    data.TemplateId,
                    data.DeploymentId,
                    data.Participants.ToHashSet(),
                    data.Seed,
                    data.StartedAt,
                    definition.RunDuration,
                    definition.EvacuationTime)
                {
                    Version = data.Version,
                    Result = data.Result,
                    CompletedAt = data.CompletedAt,
                    RecoveryShouldClose = data.State is NCMissionRunState.Closing or NCMissionRunState.Cleaning,
                    State = NCMissionRunState.Preparing,
                };
                _runs.Add(run.Id, run);
                _activeBySlot.Add(run.SlotId, run.Id);
                _runsByDeployment.Add(run.DeploymentId, run.Id);
                SetEntrances(run.SlotId, false);
                var request = new NCZoneDeploymentRequest(
                    run.DeploymentId,
                    run.SlotId.ZoneId,
                    run.TemplateId,
                    NCZoneDeploymentPurpose.MissionRun,
                    null,
                    run.Id.Value);
                if (!_templates.TryRequestDeployment(request, out _, out var error))
                {
                    run.State = NCMissionRunState.Failed;
                    run.Result = NCMissionRunResult.Failure;
                    run.CompletedAt = DateTime.UtcNow;
                    run.Error = $"Crash recovery failed: {error}";
                    _ = PersistStateAsync(run);
                    ReleaseRun(run);
                    NotifyChanged(run);
                }
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to recover mission runs: {exception}");
        }
    }

    private async Task PersistStateAsync(MissionRun run)
    {
        await _persistenceGate.WaitAsync();
        try
        {
            var updated = await _database.UpdateNCMissionRunAsync(
                run.Id,
                run.State,
                run.Result,
                run.CompletedAt,
                run.Error,
                run.Version);
            if (updated != null)
                run.Version = updated.Version;
            else
                Log.Error($"Persistent mission run {run.Id} had a version conflict.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to persist mission run {run.Id}: {exception}");
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private static NCMissionRunData ToData(MissionRun run, int templateRevision)
    {
        return new NCMissionRunData(
            run.Id,
            run.SlotId,
            run.DefinitionId.Id,
            run.TemplateId.Id,
            templateRevision,
            run.DeploymentId,
            run.State,
            run.Participants,
            run.Seed,
            run.StartedAt,
            run.CompletedAt,
            run.Result,
            run.Error,
            run.Version);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _runs.Clear();
        _activeBySlot.Clear();
        _runsByDeployment.Clear();
        _dynamicPoints.Clear();
        _evacuationPoints.Clear();
        _entrances.Clear();
        _deploymentEntityBuffer.Clear();
        _evacuees.Clear();
        _nextLifecycleUpdate = TimeSpan.Zero;
    }

    private sealed class MissionRun(
        NCMissionRunId id,
        NCMissionSlotId slotId,
        ProtoId<NCMissionDefinitionPrototype> definitionId,
        ProtoId<NCZoneTemplatePrototype> templateId,
        NCZoneDeploymentId deploymentId,
        HashSet<NCCharacterId> participants,
        int seed,
        DateTime startedAt,
        TimeSpan runDuration,
        TimeSpan evacuationTime)
    {
        public readonly NCMissionRunId Id = id;
        public readonly NCMissionSlotId SlotId = slotId;
        public readonly ProtoId<NCMissionDefinitionPrototype> DefinitionId = definitionId;
        public readonly ProtoId<NCZoneTemplatePrototype> TemplateId = templateId;
        public readonly NCZoneDeploymentId DeploymentId = deploymentId;
        public readonly HashSet<NCCharacterId> Participants = participants;
        public readonly int Seed = seed;
        public readonly DateTime StartedAt = startedAt;
        public readonly DateTime ExpiresAt = startedAt + runDuration;
        public readonly DateTime WarningAt = startedAt + runDuration - evacuationTime;
        public NCMissionRunState State = NCMissionRunState.Requested;
        public DateTime? CompletedAt;
        public NCMissionRunResult Result;
        public string? Error;
        public bool CleanupRequested;
        public bool RecoveryShouldClose;
        public int Version;
    }
}

/// <summary>Raised when a public mission changes state so open terminals can refresh immediately.</summary>
[ByRefEvent]
public readonly record struct NCMissionRunChangedEvent(NCMissionSlotId SlotId);
