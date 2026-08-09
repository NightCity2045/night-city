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
using Content.Shared._NC.Dungeons;
using Content.Shared._NC.Dungeons.Components;
using Content.Shared._NC.Dungeons.Prototypes;
using Content.Shared._NC.Identity;
using Content.Shared.Doors;
using Content.Shared.GameTicking;
using Content.Shared.Movement.Events;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Dungeons;

/// <summary>
/// Owns temporary dungeon-run state while NCZoneTemplateSystem owns physical deployment and rollback.
/// One stable slot may have at most one run, regardless of its number of Z-level fragments.
/// </summary>
public sealed partial class NCDungeonSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private City.Zones.NCZoneSystem _zones = default!;
    [Dependency] private NCZoneTemplateSystem _templates = default!;
    [Dependency] private NCZoneOccupancySystem _occupancy = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;

    private readonly Dictionary<NCDungeonRunId, DungeonRun> _runs = [];
    private readonly Dictionary<NCDungeonSlotId, NCDungeonRunId> _activeBySlot = [];
    private readonly Dictionary<NCZoneDeploymentId, NCDungeonRunId> _runsByDeployment = [];
    private readonly Dictionary<NCZoneDeploymentId, HashSet<EntityUid>> _dynamicPoints = [];
    private readonly Dictionary<NCDungeonSlotId, HashSet<EntityUid>> _entrances = [];
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PostGameMapLoad>(OnPostGameMapLoad);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<NCZoneDeploymentStateChangedEvent>(OnDeploymentStateChanged);
        SubscribeLocalEvent<NCDungeonDynamicPointComponent, MapInitEvent>(OnDynamicPointMapInit);
        SubscribeLocalEvent<NCDungeonDynamicPointComponent, ComponentShutdown>(OnDynamicPointShutdown);
        SubscribeLocalEvent<NCDungeonEntranceComponent, ComponentStartup>(OnEntranceStartup);
        SubscribeLocalEvent<NCDungeonEntranceComponent, ComponentShutdown>(OnEntranceShutdown);
        SubscribeLocalEvent<NCDungeonEntranceComponent, BeforeDoorOpenedEvent>(OnBeforeEntranceOpened);
        SubscribeLocalEvent<NCDungeonRunEntityComponent, MoveEvent>(OnRunEntityMoved);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        foreach (var run in _runs.Values.ToArray())
        {
            if (run.State != NCDungeonRunState.Closing || run.CleanupRequested)
                continue;
            if (_occupancy.IsOccupied(run.SlotId.ZoneId, out _))
                continue;
            if (!_templates.TryCloseDeployment(run.DeploymentId, out var error))
            {
                run.Error = error;
                continue;
            }

            run.CleanupRequested = true;
            run.State = NCDungeonRunState.Cleaning;
            _ = PersistStateAsync(run);
        }
    }

    public async Task<NCDungeonStartResult> TryStartRunAsync(
        ProtoId<NCDungeonDefinitionPrototype> definitionId,
        IReadOnlyCollection<NCCharacterId> participants,
        NCDungeonSlotId? requestedSlot = null,
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

    private async Task<NCDungeonStartResult> TryStartRunLockedAsync(
        ProtoId<NCDungeonDefinitionPrototype> definitionId,
        IReadOnlyCollection<NCCharacterId> participants,
        NCDungeonSlotId? requestedSlot,
        int? seed)
    {
        if (!_prototypes.TryIndex(definitionId, out var definition) ||
            !_prototypes.TryIndex(definition.Template, out NCZoneTemplatePrototype? template))
            return new(false, default, "Dungeon definition or template does not exist.");
        if (participants.Count == 0 || participants.Count > definition.MaximumParticipants ||
            participants.Any(character => !character.IsValid))
            return new(false, default, "Dungeon participant list is invalid.");

        if (!TryAllocateSlot(template, requestedSlot, out var slot, out var error))
            return new(false, default, error);
        if (_occupancy.IsOccupied(slot.ZoneId, out var blocker))
            return new(false, default, $"Dungeon slot is occupied by {ToPrettyString(blocker)}.");

        var runId = NCDungeonRunId.New();
        var deploymentId = NCZoneDeploymentId.New();
        var run = new DungeonRun(
            runId,
            slot,
            definitionId,
            definition.Template,
            deploymentId,
            participants.ToHashSet(),
            seed ?? Random.Shared.Next(),
            DateTime.UtcNow);
        var persisted = await _database.TryCreateNCDungeonRunAsync(ToData(run, template.Revision));
        if (persisted == null)
            return new(false, default, "Dungeon slot reservation conflicted in persistent storage.");
        run.Version = persisted.Version;
        _runs.Add(runId, run);
        _activeBySlot.Add(slot, runId);
        _runsByDeployment.Add(deploymentId, runId);
        SetEntrances(slot, false);

        var request = new NCZoneDeploymentRequest(
            deploymentId,
            slot.ZoneId,
            definition.Template,
            NCZoneDeploymentPurpose.DungeonRun,
            null,
            runId.Value);
        if (_templates.TryRequestDeployment(request, out _, out error))
        {
            run.State = NCDungeonRunState.Preparing;
            _ = PersistStateAsync(run);
            return new(true, runId, string.Empty);
        }

        _runs.Remove(runId);
        _activeBySlot.Remove(slot);
        _runsByDeployment.Remove(deploymentId);
        run.State = NCDungeonRunState.Failed;
        run.Result = NCDungeonRunResult.Failure;
        run.CompletedAt = DateTime.UtcNow;
        run.Error = error;
        await PersistStateAsync(run);
        return new(false, default, error);
    }

    public bool TryFinishRun(NCDungeonRunId runId, NCDungeonRunResult result, out string error)
    {
        error = string.Empty;
        if (!_runs.TryGetValue(runId, out var run) || run.State != NCDungeonRunState.Active ||
            result == NCDungeonRunResult.None)
        {
            error = "Dungeon run is not active or the result is invalid.";
            return false;
        }

        run.Result = result;
        run.CompletedAt = DateTime.UtcNow;
        run.State = NCDungeonRunState.Closing;
        SetEntrances(run.SlotId, false);
        _ = PersistStateAsync(run);
        return true;
    }

    public bool TryGetRun(NCDungeonRunId runId, out NCDungeonRunStatus status)
    {
        if (!_runs.TryGetValue(runId, out var run))
        {
            status = default;
            return false;
        }

        status = new NCDungeonRunStatus(
            run.Id,
            run.SlotId,
            run.TemplateId.Id,
            run.DeploymentId,
            run.State,
            run.Participants,
            run.Seed,
            run.StartedAt,
            run.CompletedAt,
            run.Result,
            run.Error);
        return true;
    }

    private bool TryAllocateSlot(
        NCZoneTemplatePrototype template,
        NCDungeonSlotId? requested,
        out NCDungeonSlotId slot,
        out string error)
    {
        error = string.Empty;
        if (requested is { } exact)
        {
            slot = exact;
            if (exact.ZoneId != template.TargetZoneId ||
                !_zones.TryGetDungeonSlot(exact, out var zone) ||
                !template.CompatibleZoneKinds.Contains(zone.Kind) || _activeBySlot.ContainsKey(exact))
            {
                error = "Requested dungeon slot is unavailable or incompatible.";
                return false;
            }
            return true;
        }

        var direct = new NCDungeonSlotId(template.TargetZoneId);
        if (!_activeBySlot.ContainsKey(direct) &&
            _zones.TryGetDungeonSlot(direct, out var directZone) &&
            template.CompatibleZoneKinds.Contains(directZone.Kind))
        {
            slot = direct;
            return true;
        }

        slot = default;
        error = "No compatible dungeon slot is free.";
        return false;
    }

    private void OnDeploymentStateChanged(ref NCZoneDeploymentStateChangedEvent ev)
    {
        if (!_runsByDeployment.TryGetValue(ev.DeploymentId, out var runId) || !_runs.TryGetValue(runId, out var run))
            return;

        if (ev.NewState == NCZoneDeploymentState.Active)
        {
            SpawnDynamicContent(run);
            run.State = NCDungeonRunState.Active;
            SetEntrances(run.SlotId, true);
            if (run.RecoveryShouldClose)
            {
                run.State = NCDungeonRunState.Closing;
                SetEntrances(run.SlotId, false);
            }
            _ = PersistStateAsync(run);
            return;
        }

        if (ev.NewState == NCZoneDeploymentState.FailedClean)
        {
            run.State = NCDungeonRunState.Failed;
            run.Result = NCDungeonRunResult.Failure;
            run.CompletedAt = DateTime.UtcNow;
            run.Error = ev.Error;
            _ = PersistStateAsync(run);
            ReleaseRun(run);
            return;
        }

        if (ev.NewState == NCZoneDeploymentState.Completed)
        {
            run.State = NCDungeonRunState.Closed;
            _ = PersistStateAsync(run);
            ReleaseRun(run);
        }
    }

    private void SpawnDynamicContent(DungeonRun run)
    {
        if (!_prototypes.TryIndex(run.DefinitionId, out var definition) ||
            !_dynamicPoints.TryGetValue(run.DeploymentId, out var points))
            return;

        var random = new Random(run.Seed);
        foreach (var point in points.OrderBy(uid => uid.Id))
        {
            if (!TryComp(point, out NCDungeonDynamicPointComponent? marker) ||
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
            AddComp(spawned, new NCDungeonRunEntityComponent
            {
                RunId = run.Id.Value,
                DeploymentId = run.DeploymentId.Value,
                ExtractionPolicy = marker.ExtractionPolicy,
            });
        }
    }

    private void OnRunEntityMoved(Entity<NCDungeonRunEntityComponent> entity, ref MoveEvent args)
    {
        if (entity.Comp.ExtractionPolicy != NCDungeonExtractionPolicy.CanBeExtracted ||
            !_runs.TryGetValue(new NCDungeonRunId(entity.Comp.RunId), out var run) ||
            !_coordinates.TryGetCoordinates(entity.Owner, out var coordinates) ||
            _zones.Contains(run.SlotId.ZoneId, coordinates))
            return;

        entity.Comp.ExtractionPolicy = NCDungeonExtractionPolicy.PersistentWorldItem;
        Dirty(entity, entity.Comp);
        RemCompDeferred<NCZoneDeploymentComponent>(entity.Owner);
    }

    private void OnDynamicPointMapInit(Entity<NCDungeonDynamicPointComponent> entity, ref MapInitEvent args)
    {
        if (!TryComp(entity.Owner, out NCZoneDeploymentComponent? deploymentComponent))
            return;
        var deployment = new NCZoneDeploymentId(deploymentComponent.DeploymentId);
        if (!_dynamicPoints.TryGetValue(deployment, out var points))
            _dynamicPoints[deployment] = points = [];
        points.Add(entity.Owner);
    }

    private void OnDynamicPointShutdown(Entity<NCDungeonDynamicPointComponent> entity, ref ComponentShutdown args)
    {
        if (!TryComp(entity.Owner, out NCZoneDeploymentComponent? deploymentComponent))
            return;
        var deployment = new NCZoneDeploymentId(deploymentComponent.DeploymentId);
        if (!_dynamicPoints.TryGetValue(deployment, out var points))
            return;
        points.Remove(entity.Owner);
        if (points.Count == 0)
            _dynamicPoints.Remove(deployment);
    }

    private void OnEntranceStartup(Entity<NCDungeonEntranceComponent> entity, ref ComponentStartup args)
    {
        IndexEntrance(entity);
        entity.Comp.Open = false;
        Dirty(entity, entity.Comp);
    }

    private void OnEntranceShutdown(Entity<NCDungeonEntranceComponent> entity, ref ComponentShutdown args)
    {
        RemoveEntrance(entity.Owner);
    }

    private void IndexEntrance(Entity<NCDungeonEntranceComponent> entity)
    {
        // Invalid serialized IDs are ignored defensively; valid layouts bind directly to their dungeon zone.
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

    private void OnBeforeEntranceOpened(Entity<NCDungeonEntranceComponent> entity, ref BeforeDoorOpenedEvent args)
    {
        if (!entity.Comp.Open)
            args.Cancel();
    }

    private void SetEntrances(NCDungeonSlotId slot, bool open)
    {
        if (!_entrances.TryGetValue(slot, out var entrances))
            return;
        foreach (var uid in entrances.ToArray())
        {
            if (!TryComp(uid, out NCDungeonEntranceComponent? entrance))
                continue;
            entrance.Open = open;
            Dirty(uid, entrance);
        }
    }

    private void ReleaseRun(DungeonRun run)
    {
        SetEntrances(run.SlotId, false);
        _activeBySlot.Remove(run.SlotId);
        _runsByDeployment.Remove(run.DeploymentId);
        _dynamicPoints.Remove(run.DeploymentId);
    }

    private async void OnPostGameMapLoad(PostGameMapLoad ev)
    {
        try
        {
            foreach (var data in await _database.GetRecoverableNCDungeonRunsAsync())
            {
                if (_runs.ContainsKey(data.RunId) || _activeBySlot.ContainsKey(data.SlotId) ||
                    !_prototypes.TryIndex(data.DefinitionId, out NCDungeonDefinitionPrototype? definition))
                    continue;

                var run = new DungeonRun(
                    data.RunId,
                    data.SlotId,
                    data.DefinitionId,
                    data.TemplateId,
                    data.DeploymentId,
                    data.Participants.ToHashSet(),
                    data.Seed,
                    data.StartedAt)
                {
                    Version = data.Version,
                    Result = data.Result,
                    CompletedAt = data.CompletedAt,
                    RecoveryShouldClose = data.State is NCDungeonRunState.Closing or NCDungeonRunState.Cleaning,
                    State = NCDungeonRunState.Preparing,
                };
                _runs.Add(run.Id, run);
                _activeBySlot.Add(run.SlotId, run.Id);
                _runsByDeployment.Add(run.DeploymentId, run.Id);
                SetEntrances(run.SlotId, false);
                var request = new NCZoneDeploymentRequest(
                    run.DeploymentId,
                    run.SlotId.ZoneId,
                    run.TemplateId,
                    NCZoneDeploymentPurpose.DungeonRun,
                    null,
                    run.Id.Value);
                if (!_templates.TryRequestDeployment(request, out _, out var error))
                {
                    run.State = NCDungeonRunState.Failed;
                    run.Result = NCDungeonRunResult.Failure;
                    run.CompletedAt = DateTime.UtcNow;
                    run.Error = $"Crash recovery failed: {error}";
                    _ = PersistStateAsync(run);
                    ReleaseRun(run);
                }
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to recover dungeon runs: {exception}");
        }
    }

    private async Task PersistStateAsync(DungeonRun run)
    {
        await _persistenceGate.WaitAsync();
        try
        {
            var updated = await _database.UpdateNCDungeonRunAsync(
                run.Id,
                run.State,
                run.Result,
                run.CompletedAt,
                run.Error,
                run.Version);
            if (updated != null)
                run.Version = updated.Version;
            else
                Log.Error($"Persistent dungeon run {run.Id} had a version conflict.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to persist dungeon run {run.Id}: {exception}");
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private static NCDungeonRunData ToData(DungeonRun run, int templateRevision)
    {
        return new NCDungeonRunData(
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
        _entrances.Clear();
    }

    private sealed class DungeonRun(
        NCDungeonRunId id,
        NCDungeonSlotId slotId,
        ProtoId<NCDungeonDefinitionPrototype> definitionId,
        ProtoId<NCZoneTemplatePrototype> templateId,
        NCZoneDeploymentId deploymentId,
        HashSet<NCCharacterId> participants,
        int seed,
        DateTime startedAt)
    {
        public readonly NCDungeonRunId Id = id;
        public readonly NCDungeonSlotId SlotId = slotId;
        public readonly ProtoId<NCDungeonDefinitionPrototype> DefinitionId = definitionId;
        public readonly ProtoId<NCZoneTemplatePrototype> TemplateId = templateId;
        public readonly NCZoneDeploymentId DeploymentId = deploymentId;
        public readonly HashSet<NCCharacterId> Participants = participants;
        public readonly int Seed = seed;
        public readonly DateTime StartedAt = startedAt;
        public NCDungeonRunState State = NCDungeonRunState.Requested;
        public DateTime? CompletedAt;
        public NCDungeonRunResult Result;
        public string? Error;
        public bool CleanupRequested;
        public bool RecoveryShouldClose;
        public int Version;
    }
}
