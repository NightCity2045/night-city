// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Bleeding;
using Content.Server._NC.Medical.Emergency;
using Content.Server._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Wounds;
using Robust.Shared.Timing;
using DiagnosticsStopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server._NC.Medical.Physiology;

/// <summary>
/// Central event/dirty scheduler. Only bodies present in this queue are considered during Update; organs
/// and wounds never receive independent per-tick processing.
/// </summary>
public sealed partial class NCPhysiologySchedulerSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private NCBleedingSystem _bleeding = default!;
    [Dependency] private NCEmergencyCareSystem _emergencyCare = default!;
    [Dependency] private NCSystemicSystem _systemic = default!;
    [Dependency] private NCPhysiologySystem _physiology = default!;

    // The indexed min-heap exposes only the next due body. Update never scans the complete active-body
    // set, and recurring physiology boundaries reuse the list storage instead of allocating snapshots/nodes.
    private readonly List<ScheduledUpdate> _updateHeap = new();
    private readonly Dictionary<EntityUid, int> _heapIndices = new();
    private readonly HashSet<EntityUid> _processingBodies = new();
    private long _processedBodies;
    private long _budgetDeferrals;
    private int _lastBatchBodies;
    private double _lastBodyMilliseconds;
    private double _maximumBodyMilliseconds;
    private double _lastBatchMilliseconds;
    private double _maximumBatchMilliseconds;

    public NCPhysiologySchedulerCounters GetRuntimeCounters()
    {
        return new NCPhysiologySchedulerCounters(
            _updateHeap.Count,
            _updateHeap.Count > 0 && _updateHeap[0].Due <= _timing.CurTime,
            _processedBodies,
            _budgetDeferrals,
            _lastBatchBodies,
            _lastBodyMilliseconds,
            _maximumBodyMilliseconds,
            _lastBatchMilliseconds,
            _maximumBatchMilliseconds);
    }

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCMedicalBodyComponent, ComponentShutdown>(OnBodyShutdown);
        SubscribeLocalEvent<NCCirculationComponent, NCMedicalBodyChangedEvent>(OnBodyChanged);
    }

    private void OnBodyShutdown(Entity<NCMedicalBodyComponent> entity, ref ComponentShutdown args)
    {
        Unschedule(entity.Owner);
    }

    private void OnBodyChanged(Entity<NCCirculationComponent> entity, ref NCMedicalBodyChangedEvent args)
    {
        // Scheduled subsystems still broadcast their mutation for diagnostics and persistence. The body is
        // already inside its authoritative boundary, so a due-now duplicate would only add a zero-time pass.
        if (_processingBodies.Contains(entity.Owner))
            return;

        // An unscheduled body has been event-driven and healthy since its last evaluation. New wounds and
        // bleeding sources begin at this mutation boundary, so they must never be integrated over that idle
        // interval. Bodies already in the heap retain their anchor because their existing timed state has
        // genuinely been accruing since the previous evaluation.
        if (!_heapIndices.ContainsKey(entity.Owner))
            entity.Comp.LastEvaluation = _timing.CurTime;

        Schedule(entity.Owner);
    }

    public void Schedule(EntityUid body, TimeSpan? delay = null)
    {
        if (!Exists(body))
            return;

        var due = _timing.CurTime + (delay ?? TimeSpan.Zero);
        if (_heapIndices.TryGetValue(body, out var index))
        {
            if (due >= _updateHeap[index].Due)
                return;

            _updateHeap[index] = new ScheduledUpdate(body, due);
            SiftUp(index);
            return;
        }

        _heapIndices.Add(body, _updateHeap.Count);
        _updateHeap.Add(new ScheduledUpdate(body, due));
        SiftUp(_updateHeap.Count - 1);
    }

    public TimeSpan? GetRemainingDelay(EntityUid body)
    {
        return _heapIndices.TryGetValue(body, out var index)
            ? _updateHeap[index].Due - _timing.CurTime
            : null;
    }

    /// <summary>
    /// Explicit deterministic hook for tests, admin tooling and future abstract simulation.
    /// </summary>
    public bool ProcessNow(EntityUid body, TimeSpan elapsed)
    {
        if (!Exists(body) || !HasComp<NCMedicalBodyComponent>(body))
            return false;

        // Explicit processing supersedes any older due entry (admin and test callers included).
        Unschedule(body);
        if (!_processingBodies.Add(body))
            return false;

        try
        {
            var scheduledUpdate = new NCMedicalScheduledUpdateEvent(elapsed);
            RaiseLocalEvent(body, ref scheduledUpdate);
            _bleeding.AdvanceBody(body, elapsed);
            _systemic.AdvanceBody(body, elapsed);
            if (!_physiology.EvaluateBody(body, elapsed))
                return false;

            // Support applies through the interval ending at this scheduler boundary. If it expires exactly
            // here, immediately recalculate with zero elapsed so stale assisted values cannot persist.
            if (_emergencyCare.AdvanceInterventions(body, elapsed))
                _physiology.EvaluateBody(body, TimeSpan.Zero);

            ScheduleNext(body);
            return true;
        }
        finally
        {
            _processingBodies.Remove(body);
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_updateHeap.Count == 0)
            return;

        var now = _timing.CurTime;
        var batchStarted = DiagnosticsStopwatch.GetTimestamp();
        var processed = 0;
        var maximumBodies = int.MaxValue;
        var maximumWorkTime = TimeSpan.MaxValue;
        while (_updateHeap.Count > 0)
        {
            var scheduled = _updateHeap[0];
            if (scheduled.Due > now)
                break;

            var body = scheduled.Body;
            if (!TryComp<NCCirculationComponent>(body, out var circulation))
            {
                RemoveScheduledAt(0);
                continue;
            }

            var profile = _physiology.GetProfile(body);
            maximumBodies = Math.Min(maximumBodies, profile.MaximumScheduledBodiesPerTick);
            maximumWorkTime = maximumWorkTime <= profile.MaximumSchedulerWorkTime
                ? maximumWorkTime
                : profile.MaximumSchedulerWorkTime;
            if (processed > 0 &&
                (processed >= maximumBodies ||
                 DiagnosticsStopwatch.GetElapsedTime(batchStarted) >= maximumWorkTime))
            {
                _budgetDeferrals++;
                break;
            }

            RemoveScheduledAt(0);
            var elapsed = now > circulation.LastEvaluation
                ? now - circulation.LastEvaluation
                : TimeSpan.Zero;
            var bodyStarted = DiagnosticsStopwatch.GetTimestamp();
            if (ProcessNow(body, elapsed))
                _processedBodies++;
            _lastBodyMilliseconds = DiagnosticsStopwatch.GetElapsedTime(bodyStarted).TotalMilliseconds;
            _maximumBodyMilliseconds = Math.Max(_maximumBodyMilliseconds, _lastBodyMilliseconds);
            processed++;
        }

        _lastBatchBodies = processed;
        _lastBatchMilliseconds = DiagnosticsStopwatch.GetElapsedTime(batchStarted).TotalMilliseconds;
        _maximumBatchMilliseconds = Math.Max(_maximumBatchMilliseconds, _lastBatchMilliseconds);
    }

    private void ScheduleNext(EntityUid body)
    {
        if (!TryComp<NCPhysiologyComponent>(body, out var physiology))
            return;

        var profile = _physiology.GetProfile(body);
        TimeSpan? next = null;
        if (physiology.Condition is NCPhysiologicalCondition.Critical or
            NCPhysiologicalCondition.ClinicalArrest)
        {
            next = profile.CriticalUpdateInterval;
        }
        else if (_bleeding.HasActiveBleeding(body))
        {
            next = profile.ActiveBleedingUpdateInterval;
        }
        else if (TryComp<NCWoundContainerComponent>(body, out var wounds) &&
                 (wounds.Wounds.Count > 0 || wounds.OverflowTraumaByZone.Count > 0))
        {
            next = profile.StableInjuredUpdateInterval;
        }

        if (next is { } recurring)
            next = AddStablePhase(body, recurring, profile.SchedulingPhaseWindow);

        var interventionExpiry = _emergencyCare.GetNextInterventionDelay(body);
        if (interventionExpiry != null && (next == null || interventionExpiry < next))
            next = interventionExpiry;

        var systemicUpdate = _systemic.GetNextUpdateDelay(body);
        if (systemicUpdate != null && (next == null || systemicUpdate < next))
            next = systemicUpdate;

        var extension = new NCMedicalNextUpdateEvent(next);
        RaiseLocalEvent(body, ref extension);
        // Extension handlers are advisory and may only shorten a built-in deadline. Enforcing the minimum
        // here keeps one faulty subsystem from suppressing or postponing another subsystem's due boundary.
        if (extension.Delay is { } extensionDelay && extensionDelay > TimeSpan.Zero &&
            (next == null || extensionDelay < next))
        {
            next = extensionDelay;
        }

        if (next != null)
            Schedule(body, next);
        // Healthy bodies without timed support remain event-driven and allocate no recurring work.
    }

    private static TimeSpan AddStablePhase(EntityUid body, TimeSpan interval, TimeSpan configuredWindow)
    {
        // Keep critical cadence precise: phase spreading may consume at most one eighth of the base interval.
        var windowTicks = Math.Min(configuredWindow.Ticks, interval.Ticks / 8);
        if (windowTicks <= 0)
            return interval;

        var hash = unchecked((uint) body.Id * 2654435761u);
        var phaseTicks = (long) ((ulong) hash % (ulong) windowTicks);
        return interval + TimeSpan.FromTicks(phaseTicks);
    }

    private void Unschedule(EntityUid body)
    {
        if (!_heapIndices.TryGetValue(body, out var index))
            return;

        RemoveScheduledAt(index);
    }

    private readonly record struct ScheduledUpdate(EntityUid Body, TimeSpan Due);

    private void RemoveScheduledAt(int index)
    {
        var lastIndex = _updateHeap.Count - 1;
        var removed = _updateHeap[index];
        _heapIndices.Remove(removed.Body);
        if (index == lastIndex)
        {
            _updateHeap.RemoveAt(lastIndex);
            return;
        }

        var replacement = _updateHeap[lastIndex];
        _updateHeap[index] = replacement;
        _updateHeap.RemoveAt(lastIndex);
        _heapIndices[replacement.Body] = index;

        if (index > 0 && Compare(replacement, _updateHeap[(index - 1) / 2]) < 0)
            SiftUp(index);
        else
            SiftDown(index);
    }

    private void SiftUp(int index)
    {
        while (index > 0)
        {
            var parent = (index - 1) / 2;
            if (Compare(_updateHeap[index], _updateHeap[parent]) >= 0)
                return;

            Swap(index, parent);
            index = parent;
        }
    }

    private void SiftDown(int index)
    {
        while (true)
        {
            var left = index * 2 + 1;
            if (left >= _updateHeap.Count)
                return;

            var right = left + 1;
            var child = right < _updateHeap.Count && Compare(_updateHeap[right], _updateHeap[left]) < 0
                ? right
                : left;
            if (Compare(_updateHeap[child], _updateHeap[index]) >= 0)
                return;

            Swap(index, child);
            index = child;
        }
    }

    private void Swap(int left, int right)
    {
        (_updateHeap[left], _updateHeap[right]) = (_updateHeap[right], _updateHeap[left]);
        _heapIndices[_updateHeap[left].Body] = left;
        _heapIndices[_updateHeap[right].Body] = right;
    }

    private static int Compare(ScheduledUpdate left, ScheduledUpdate right)
    {
        var deadlineComparison = left.Due.CompareTo(right.Due);
        return deadlineComparison != 0
            ? deadlineComparison
            : left.Body.CompareTo(right.Body);
    }
}

public readonly record struct NCPhysiologySchedulerCounters(
    int QueuedBodies,
    bool HasOverdueBodies,
    long ProcessedBodies,
    long BudgetDeferrals,
    int LastBatchBodies,
    double LastBodyMilliseconds,
    double MaximumBodyMilliseconds,
    double LastBatchMilliseconds,
    double MaximumBatchMilliseconds);
