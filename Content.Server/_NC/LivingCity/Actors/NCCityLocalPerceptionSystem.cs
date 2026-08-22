// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Simulation;
using Content.Server._NC.LivingCity.Time;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Mobs;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

public readonly record struct NCCityLocalPerceptionCounters(
    bool Active,
    int TrackedActors,
    int ScheduledUpdates,
    long Evaluations,
    long Observations,
    long RejectedConfigurations,
    string? ConfigurationError);

/// <summary>
/// Evaluates only configured materialized actors through EntityLookup spatial queries and a bounded due-time queue.
/// </summary>
public sealed partial class NCCityLocalPerceptionSystem : EntitySystem
{
    private const string DefaultPolicyId = "NCDefaultLocalPerceptionPolicy";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCAILoadGovernorSystem _governor = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private NpcFactionSystem _factions = default!;

    private readonly PriorityQueue<ScheduledEvaluation, EvaluationPriority> _queue = new();
    private readonly Dictionary<EntityUid, NCActiveWorldTime> _scheduled = [];
    private readonly HashSet<EntityUid> _tracked = [];
    private readonly HashSet<EntityUid> _observedBuffer = [];
    private NCCityLocalPerceptionPolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private long _sequence;
    private long _evaluations;
    private long _observations;
    private long _rejectedConfigurations;
    private bool _active;
    private string? _configurationError;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
        SubscribeLocalEvent<NCCityLocalPerceptionComponent, ComponentShutdown>(OnShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            return;
        }

        using var work = _governor.MeasureWork(NCAILoadWorkKind.PhysicalPerception);
        var limit = _governor.TakeDispatchLimit(
            NCAILoadDispatchKind.PhysicalPerception,
            _policy.MaximumUpdatesPerTick);
        var dispatched = 0;
        while (dispatched < limit && TryDequeueDue(now, out var entityUid))
        {
            Evaluate(entityUid, now);
            dispatched++;
        }
        _governor.ReportQueue(
            NCAILoadQueueKind.PhysicalPerception,
            _scheduled.Count,
            _policy.MaximumTrackedActors);
        _governor.ReportBacklog(NCAILoadBacklogKind.PhysicalPerception, HasDue(now));
    }

    public bool Configure(EntityUid entityUid, ProtoId<NCCityLocalPerceptionProfilePrototype> profileId)
    {
        if (!_active || _policy == null || !Exists(entityUid) ||
            !_prototypes.TryIndex(profileId, out var profile) || profile == null || !Validate(profile))
        {
            _rejectedConfigurations++;
            return false;
        }
        if (!_tracked.Contains(entityUid) && _tracked.Count >= _policy.MaximumTrackedActors)
        {
            _rejectedConfigurations++;
            return false;
        }

        var component = EnsureComp<NCCityLocalPerceptionComponent>(entityUid);
        component.Profile = profileId;
        _tracked.Add(entityUid);
        if (_clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch)
            Schedule(entityUid, now);
        return true;
    }

    public NCCityLocalPerceptionCounters GetCounters() => new(
        _active,
        _tracked.Count,
        _scheduled.Count,
        _evaluations,
        _observations,
        _rejectedConfigurations,
        _configurationError);

    public void Unconfigure(EntityUid entityUid) => Cleanup(entityUid, true);

    private void Evaluate(EntityUid entityUid, NCActiveWorldTime now)
    {
        if (!Exists(entityUid) || !TryComp<NCCityLocalPerceptionComponent>(entityUid, out var component) ||
            !_prototypes.TryIndex(component.Profile, out var profile) || profile == null || !Validate(profile))
        {
            Cleanup(entityUid, true);
            return;
        }

        component.Observations.Clear();
        component.HostileCount = 0;
        component.CriticalCount = 0;
        component.NearestHostile = EntityUid.Invalid;
        component.NearestCritical = EntityUid.Invalid;
        var nearestHostileDistance = float.MaxValue;
        var nearestCriticalDistance = float.MaxValue;
        var lineOfSightChecks = 0;
        var origin = _transform.GetMapCoordinates(entityUid);
        _observedBuffer.Clear();
        if (TryComp<NpcFactionMemberComponent>(entityUid, out var ownFaction))
        {
            TryComp<FactionExceptionComponent>(entityUid, out var exception);
            foreach (var hostile in _factions.GetNearbyHostiles((entityUid, ownFaction, exception), profile.Radius))
            {
                if (component.Observations.Count >= profile.MaximumObservedEntities)
                    break;
                if (!_observedBuffer.Add(hostile))
                {
                    continue;
                }
                if (lineOfSightChecks >= profile.MaximumLineOfSightChecks)
                    break;
                lineOfSightChecks++;
                if (!_interaction.InRangeUnobstructed(entityUid, hostile, profile.Radius))
                    continue;

                var target = _transform.GetMapCoordinates(hostile);
                var distanceSquared = System.Numerics.Vector2.DistanceSquared(origin.Position, target.Position);
                component.Observations.Add(new NCCityLocalObservation(
                    hostile,
                    NCCityLocalObservationKind.Hostile,
                    distanceSquared));
                component.HostileCount++;
                if (distanceSquared < nearestHostileDistance)
                {
                    nearestHostileDistance = distanceSquared;
                    component.NearestHostile = hostile;
                }
            }
        }

        foreach (var candidate in _lookup.GetEntitiesInRange<MobStateComponent>(origin, profile.Radius))
        {
            if (component.Observations.Count >= profile.MaximumObservedEntities)
                break;
            if (candidate.Owner == entityUid || candidate.Comp.CurrentState != MobState.Critical ||
                !_observedBuffer.Add(candidate.Owner))
            {
                continue;
            }
            if (lineOfSightChecks >= profile.MaximumLineOfSightChecks)
                break;
            lineOfSightChecks++;
            if (!_interaction.InRangeUnobstructed(entityUid, candidate.Owner, profile.Radius))
                continue;
            var target = _transform.GetMapCoordinates(candidate.Owner);
            var distanceSquared = System.Numerics.Vector2.DistanceSquared(origin.Position, target.Position);
            component.Observations.Add(new NCCityLocalObservation(
                candidate.Owner,
                NCCityLocalObservationKind.Critical,
                distanceSquared));
            component.CriticalCount++;
            if (distanceSquared < nearestCriticalDistance)
            {
                nearestCriticalDistance = distanceSquared;
                component.NearestCritical = candidate.Owner;
            }
        }

        component.LastEvaluatedAt = now;
        component.Revision++;
        _evaluations++;
        _observations += component.Observations.Count;
        var interval = _governor.ScaleEvaluationInterval(profile.EvaluationInterval);
        component.NextEvaluationAt = now.Advance(interval);
        Schedule(entityUid, component.NextEvaluationAt);
    }

    private void Schedule(EntityUid entityUid, NCActiveWorldTime dueAt)
    {
        if (_scheduled.TryGetValue(entityUid, out var existing) && existing <= dueAt)
            return;
        _scheduled[entityUid] = dueAt;
        var item = new ScheduledEvaluation(entityUid, dueAt, ++_sequence);
        _queue.Enqueue(item, new EvaluationPriority(dueAt.Ticks, item.Sequence));
    }

    private bool TryDequeueDue(NCActiveWorldTime now, out EntityUid entityUid)
    {
        while (_queue.TryPeek(out var item, out var priority) && priority.DueTicks <= now.Ticks)
        {
            _queue.Dequeue();
            if (!_scheduled.TryGetValue(item.EntityUid, out var dueAt) || dueAt != item.DueAt)
                continue;
            _scheduled.Remove(item.EntityUid);
            entityUid = item.EntityUid;
            return true;
        }
        entityUid = EntityUid.Invalid;
        return false;
    }

    private bool HasDue(NCActiveWorldTime now) =>
        _queue.TryPeek(out _, out var priority) && priority.DueTicks <= now.Ticks;

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (!_prototypes.TryIndex(
                new ProtoId<NCCityLocalPerceptionPolicyPrototype>(DefaultPolicyId),
                out var policy) || policy == null || policy.MaximumTrackedActors <= 0 || policy.MaximumUpdatesPerTick <= 0)
        {
            _configurationError = "default-local-perception-policy-invalid";
            return;
        }
        _policy = policy;
        _epoch = args.Epoch;
        _active = true;
    }

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args) => Reset();

    private void OnClockFailed(NCActiveWorldClockFailedEvent args) => Reset();

    private void OnShutdown(Entity<NCCityLocalPerceptionComponent> entity, ref ComponentShutdown args) =>
        Cleanup(entity.Owner, false);

    private void Cleanup(EntityUid entityUid, bool removeComponent)
    {
        _tracked.Remove(entityUid);
        _scheduled.Remove(entityUid);
        if (removeComponent && Exists(entityUid) && HasComp<NCCityLocalPerceptionComponent>(entityUid))
            RemCompDeferred<NCCityLocalPerceptionComponent>(entityUid);
    }

    private void Reset()
    {
        _active = false;
        var tracked = new EntityUid[_tracked.Count];
        _tracked.CopyTo(tracked);
        foreach (var entityUid in tracked)
            Cleanup(entityUid, true);
        _tracked.Clear();
        _scheduled.Clear();
        _queue.Clear();
        _policy = null;
        _epoch = default;
        _sequence = 0;
        _evaluations = 0;
        _observations = 0;
        _rejectedConfigurations = 0;
        _configurationError = null;
        _governor.ReportQueue(NCAILoadQueueKind.PhysicalPerception, 0, 1);
        _governor.ReportBacklog(NCAILoadBacklogKind.PhysicalPerception, false);
    }

    internal static bool Validate(NCCityLocalPerceptionProfilePrototype profile) =>
        float.IsFinite(profile.Radius) && profile.Radius > 0f &&
        profile.MaximumObservedEntities > 0 && profile.MaximumLineOfSightChecks > 0 &&
        profile.MaximumLineOfSightChecks <= profile.MaximumObservedEntities &&
        profile.EvaluationInterval > TimeSpan.Zero;

    private readonly record struct ScheduledEvaluation(EntityUid EntityUid, NCActiveWorldTime DueAt, long Sequence);

    private readonly record struct EvaluationPriority(long DueTicks, long Sequence) : IComparable<EvaluationPriority>
    {
        public int CompareTo(EvaluationPriority other)
        {
            var due = DueTicks.CompareTo(other.DueTicks);
            return due != 0 ? due : Sequence.CompareTo(other.Sequence);
        }
    }
}
