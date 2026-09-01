// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Linq;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Affordances;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Employment.Components;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Affordances;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

public readonly record struct NCHumanRoutineActivityCounters(
    int Active,
    long Started,
    long Completed,
    long Failed,
    long RuntimeUnavailable,
    long AffordanceUnavailable,
    long TargetUnreachable,
    long LocomotionBusy,
    string? ConfigurationError);

/// <summary>
/// Executes one common Human routine for civilians, employees and organization members. It selects only indexed
/// semantic affordances near the actor and delegates locomotion to vanilla NPC steering; professions remain overlays.
/// </summary>
public sealed partial class NCHumanRoutineActivitySystem : EntitySystem
{
    private const string ActionId = "perform-routine-activity";
    private const string FallbackActionId = "perform-safe-fallback";
    private const string SteeringOwner = "living-city-human-routine";
    private static readonly ProtoId<NCHumanRoutineActivityPolicyPrototype> DefaultPolicy =
        "NCDefaultHumanRoutineActivityPolicy";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityAffordanceCatalogSystem _affordances = default!;
    [Dependency] private NCCityLocationSystem _locations = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCCityNpcSteeringLeaseSystem _steeringLease = default!;
    [Dependency] private NCCityPhysicalExecutionSystem _physical = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private HTNSystem _htn = default!;

    private readonly Dictionary<EntityUid, RoutineActivity> _active = [];
    private readonly Dictionary<EntityUid, uint> _cycles = [];
    private readonly HashSet<EntityUid> _steeringActors = [];
    private readonly HashSet<EntityUid> _suspendedHtn = [];
    private readonly List<NCCityAffordanceInfo> _candidateBuffer = [];
    private NCHumanRoutineActivityPolicyPrototype? _policy;
    private long _started;
    private long _completed;
    private long _failed;
    private long _runtimeUnavailable;
    private long _affordanceUnavailable;
    private long _targetUnreachable;
    private long _locomotionBusy;
    private string? _configurationError;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCHumanActionExecutionEvent>(OnExecute);
        SubscribeLocalEvent<NCHumanActionInterruptedEvent>(OnInterrupted);
        SubscribeLocalEvent<NCCityActorBindingChangedEvent>(OnBindingChanged);
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Reset());
    }

    public NCHumanRoutineActivityCounters GetCounters() =>
        new(_active.Count, _started, _completed, _failed, _runtimeUnavailable, _affordanceUnavailable,
            _targetUnreachable, _locomotionBusy, _configurationError);

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (!_prototypes.TryIndex(DefaultPolicy, out var policy) || policy == null || !Validate(policy))
        {
            _configurationError = "human-routine-activity-policy-invalid";
            return;
        }

        _policy = policy;
    }

    private void OnExecute(NCHumanActionExecutionEvent args)
    {
        var fallback = args.Action.ID == FallbackActionId;
        if (args.Action.ID != ActionId && !fallback)
            return;

        args.Handled = true;
        if (_policy == null || args.EntityUid is not { } human || !Exists(human) ||
            !_clock.TryGetCurrentTime(out _, out var now) ||
            !_actors.TryGetCached(args.ActorId, out var actor))
        {
            args.Failure = _configurationError ?? "human-routine-runtime-unavailable";
            _failed++;
            _runtimeUnavailable++;
            return;
        }

        var activity = actor.Snapshot.Schedule.CurrentActivity;
        NCHumanRoutineActivityDefinition? definition = null;
        if (!fallback && !TryGetDefinition(activity, out definition))
        {
            // Home-only or deliberately inactive schedule entries need no physical world mutation.
            args.Completed = true;
            return;
        }

        if (!_active.TryGetValue(human, out var runtime) || runtime.InstanceId != args.InstanceId ||
            runtime.Fallback != fallback || (!fallback &&
                !runtime.Activity.Equals(activity, StringComparison.OrdinalIgnoreCase)))
        {
            Stop(human);
            NCMapCoordinates target;
            if (fallback)
            {
                if (!TrySelectFallbackTarget(human, args.ActorId, 0, out definition, out target))
                {
                    args.Failure = "human-routine-safe-fallback-unavailable";
                    _failed++;
                    _affordanceUnavailable++;
                    return;
                }
                activity = definition.Activity;
            }
            else if (!TrySelectTarget(human, args.ActorId, definition!, 0, out target))
            {
                args.Failure = $"human-routine-affordance-unavailable:{activity}";
                _failed++;
                _affordanceUnavailable++;
                return;
            }

            runtime = new RoutineActivity(args.InstanceId, activity, target, default, false, fallback, 0);
            _active[human] = runtime;
            _started++;
        }

        if (IsReached(human, runtime.Target, _policy.ArrivalRange))
        {
            // Keep procedural HTN suspended during the dwell so vanilla Idle cannot pull the actor away.
            StopMovement(human, false);
            if (!runtime.Dwelling)
            {
                var dwell = SelectDwell(args.ActorId, _cycles.GetValueOrDefault(human), _policy);
                runtime = runtime with { Dwelling = true, DwellUntil = now.Advance(dwell) };
                _active[human] = runtime;
                return;
            }

            if (now < runtime.DwellUntil)
                return;

            _cycles[human] = _cycles.GetValueOrDefault(human) + 1;
            _completed++;
            args.Completed = true;
            Stop(human);
            return;
        }

        if (TryComp<NPCSteeringComponent>(human, out var steering) && steering.Status == SteeringStatus.NoPath)
        {
            if (runtime.Fallback && runtime.TargetAttempt + 1 < _policy.MaximumFallbackTargetAttempts)
            {
                var nextAttempt = runtime.TargetAttempt + 1;
                StopMovement(human, false);
                if (TrySelectFallbackTarget(human, args.ActorId, nextAttempt, out var nextDefinition,
                        out var nextTarget))
                {
                    runtime = runtime with
                    {
                        Activity = nextDefinition.Activity,
                        Target = nextTarget,
                        TargetAttempt = nextAttempt,
                    };
                    _active[human] = runtime;
                    if (BeginApproach(human, nextTarget))
                        return;
                }
            }
            args.Failure = "human-routine-target-unreachable";
            _failed++;
            _targetUnreachable++;
            Stop(human);
            return;
        }

        if (!BeginApproach(human, runtime.Target))
        {
            args.Failure = "human-routine-locomotion-busy";
            _failed++;
            _locomotionBusy++;
            Stop(human);
        }
    }

    private bool TryGetDefinition(string activity, out NCHumanRoutineActivityDefinition definition)
    {
        if (_policy != null)
        {
            foreach (var candidate in _policy.Activities)
            {
                if (candidate.Activity.Equals(activity, StringComparison.OrdinalIgnoreCase))
                {
                    definition = candidate;
                    return true;
                }
            }
        }

        definition = default!;
        return false;
    }

    private bool TrySelectTarget(
        EntityUid human,
        NCActorId actorId,
        NCHumanRoutineActivityDefinition definition,
        int selectionOffset,
        out NCMapCoordinates target)
    {
        target = default;
        if (_policy == null)
            return false;

        _locations.EnsureTracked(human);
        if (!_locations.TryGetContext(human, out var context))
            return false;

        if (definition.PreferLivelihoodAffordance &&
            TryComp<NCCityLivelihoodComponent>(human, out var livelihood) &&
            _prototypes.TryIndex(livelihood.Profile, out var livelihoodProfile) && livelihoodProfile != null &&
            TrySelectKind(livelihoodProfile.WorkAffordance, context, actorId, human, selectionOffset, out target))
        {
            return true;
        }

        foreach (var kind in definition.Affordances)
        {
            if (TrySelectKind(kind, context, actorId, human, selectionOffset, out target))
                return true;
        }

        return false;
    }

    private bool TrySelectFallbackTarget(
        EntityUid human,
        NCActorId actorId,
        int attempt,
        out NCHumanRoutineActivityDefinition definition,
        out NCMapCoordinates target)
    {
        definition = default!;
        target = default;
        if (_policy == null || _policy.FallbackActivities.Count == 0)
            return false;

        // Rotate the YAML-authored fallbacks per actor and retry so a blocked anchor cannot create permanent Idle.
        var start = (int) (StableSeed(actorId.Value, _cycles.GetValueOrDefault(human), 0x85ebca6bu) %
                           (uint) _policy.FallbackActivities.Count);
        for (var index = 0; index < _policy.FallbackActivities.Count; index++)
        {
            var candidate = _policy.FallbackActivities[(start + attempt + index) % _policy.FallbackActivities.Count];
            if (!TrySelectTarget(human, actorId, candidate, attempt + index, out target))
                continue;
            definition = candidate;
            return true;
        }
        return false;
    }

    private bool TrySelectKind(
        NCCityAffordanceKind kind,
        NCCityLocationContext context,
        NCActorId actorId,
        EntityUid human,
        int selectionOffset,
        out NCMapCoordinates target)
    {
        target = default;
        // Residents broaden their search only through the mapper-authored semantic hierarchy. No per-NPC anchors or
        // global scans are introduced when the current building does not offer the required activity.
        if (context.BuildingId.IsValid &&
            TrySelectWithin(kind, context.BuildingId.ZoneId, actorId, human, selectionOffset, out target))
            return true;
        if (context.StreetId.IsValid &&
            TrySelectWithin(kind, context.StreetId.ZoneId, actorId, human, selectionOffset, out target))
            return true;
        if (context.DistrictId.IsValid &&
            TrySelectWithin(kind, context.DistrictId.ZoneId, actorId, human, selectionOffset, out target))
            return true;
        return !context.BuildingId.IsValid && !context.StreetId.IsValid && !context.DistrictId.IsValid &&
               TrySelectWithin(kind, context.MostSpecific, actorId, human, selectionOffset, out target);
    }

    private bool TrySelectWithin(
        NCCityAffordanceKind kind,
        NCZoneId within,
        NCActorId actorId,
        EntityUid human,
        int selectionOffset,
        out NCMapCoordinates target)
    {
        target = default;
        _affordances.Get(kind, within, _candidateBuffer);
        var count = Math.Min(_policy!.MaximumCatalogCandidates, _candidateBuffer.Count);
        if (count == 0)
            return false;

        var seed = StableSeed(actorId.Value, _cycles.GetValueOrDefault(human) + (uint) selectionOffset, (uint) kind);
        target = _candidateBuffer[(int) (seed % (uint) count)].Target;
        return _coordinates.TryResolve(target, out _);
    }

    private bool BeginApproach(EntityUid human, NCMapCoordinates target)
    {
        if (!TryResolveEntityCoordinates(target, out var destination) ||
            _steeringLease.HasOtherOwner(human, SteeringOwner) || !_steeringLease.Acquire(human, SteeringOwner))
            return false;
        if (_suspendedHtn.Add(human) && TryComp<HTNComponent>(human, out var htn) && htn.Enabled)
            _htn.SetHTNEnabled((human, htn), false);
        _physical.SetInterrupt(human, NCCityPhysicalInterruptFlags.Work, true);
        _steeringActors.Add(human);
        _steering.Register(human, destination);
        return true;
    }

    private bool TryResolveEntityCoordinates(NCMapCoordinates target, out EntityCoordinates coordinates)
    {
        coordinates = default;
        if (!_coordinates.TryResolve(target, out var mapCoordinates) ||
            !_maps.TryGetMap(mapCoordinates.MapId, out var mapUid) || mapUid is not { } map ||
            !_maps.TryFindGridAt(map, mapCoordinates.Position, out var gridUid, out _))
            return false;
        coordinates = _transform.ToCoordinates((gridUid, null), mapCoordinates);
        return true;
    }

    private bool IsReached(EntityUid human, NCMapCoordinates target, float range) =>
        _coordinates.TryGetCoordinates(human, out var current) && current.NetworkId == target.NetworkId &&
        current.Z == target.Z && Vector2.DistanceSquared(current.Position, target.Position) <= range * range;

    private void OnInterrupted(NCHumanActionInterruptedEvent args)
    {
        if ((args.ActionId == ActionId || args.ActionId == FallbackActionId) && args.EntityUid is { } human)
            Stop(human);
    }

    private void OnBindingChanged(NCCityActorBindingChangedEvent args)
    {
        if (!args.Bound)
        {
            Stop(args.EntityUid, false);
            _cycles.Remove(args.EntityUid);
        }
    }

    private void Stop(EntityUid human, bool restoreHtn = true)
    {
        _active.Remove(human);
        StopMovement(human, restoreHtn);
    }

    private void StopMovement(EntityUid human, bool restoreHtn = true)
    {
        if (_steeringActors.Remove(human) && Exists(human))
        {
            _steering.Unregister(human);
            _steeringLease.Release(human, SteeringOwner);
        }
        if (!Exists(human))
        {
            _suspendedHtn.Remove(human);
            return;
        }
        _physical.SetInterrupt(human, NCCityPhysicalInterruptFlags.Work, false);
        if (restoreHtn && _suspendedHtn.Remove(human) && TryComp<HTNComponent>(human, out var htn))
            _htn.SetHTNEnabled((human, htn), true, htn.PlanCooldown);
    }

    private void Reset()
    {
        foreach (var human in _active.Keys.ToArray())
            Stop(human);
        _active.Clear();
        _cycles.Clear();
        _policy = null;
        _started = 0;
        _completed = 0;
        _failed = 0;
        _runtimeUnavailable = 0;
        _affordanceUnavailable = 0;
        _targetUnreachable = 0;
        _locomotionBusy = 0;
        _configurationError = null;
    }

    private static bool Validate(NCHumanRoutineActivityPolicyPrototype policy)
    {
        if (policy.MaximumCatalogCandidates <= 0 || policy.MaximumFallbackTargetAttempts <= 0 ||
            !float.IsFinite(policy.ArrivalRange) ||
            policy.ArrivalRange <= 0f || policy.MinimumDwellTime <= TimeSpan.Zero ||
            policy.MaximumDwellTime < policy.MinimumDwellTime || policy.Activities.Count == 0 ||
            policy.FallbackActivities.Count == 0)
            return false;
        var activities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return policy.Activities.Concat(policy.FallbackActivities).All(value =>
            !string.IsNullOrWhiteSpace(value.Activity) &&
            activities.Add(value.Activity) && value.Affordances.Count > 0 &&
            value.Affordances.All(Enum.IsDefined));
    }

    private static TimeSpan SelectDwell(
        NCActorId actorId,
        uint cycle,
        NCHumanRoutineActivityPolicyPrototype policy)
    {
        var range = policy.MaximumDwellTime - policy.MinimumDwellTime;
        if (range <= TimeSpan.Zero)
            return policy.MinimumDwellTime;
        var seed = StableSeed(actorId.Value, cycle, 0x9e3779b9u);
        var ratio = seed / (double) uint.MaxValue;
        return policy.MinimumDwellTime + TimeSpan.FromTicks((long) (range.Ticks * ratio));
    }

    private static uint StableSeed(Guid actorId, uint cycle, uint salt)
    {
        Span<byte> bytes = stackalloc byte[16];
        actorId.TryWriteBytes(bytes);
        var hash = 2166136261u ^ salt;
        foreach (var value in bytes)
            hash = (hash ^ value) * 16777619u;
        return (hash ^ cycle) * 16777619u;
    }

    private sealed record RoutineActivity(
        Guid InstanceId,
        string Activity,
        NCMapCoordinates Target,
        NCActiveWorldTime DwellUntil,
        bool Dwelling,
        bool Fallback,
        int TargetAttempt);
}
