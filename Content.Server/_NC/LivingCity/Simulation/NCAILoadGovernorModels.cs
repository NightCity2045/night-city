// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Simulation;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Simulation;

public enum NCAILoadState : byte
{
    Normal,
    Elevated,
    Critical,
}

[Flags]
public enum NCAILoadPressureReason : ushort
{
    None = 0,
    FrameTime = 1 << 0,
    WorkBudget = 1 << 1,
    ActorSpawnQueue = 1 << 2,
    PhysicalPerceptionQueue = 1 << 5,
    PhysicalPerceptionBacklog = 1 << 6,
}

public enum NCAILoadQueueKind : byte
{
    ActorSpawn,
    PhysicalPerception,
}

public enum NCAILoadBacklogKind : byte
{
    PhysicalPerception,
}

public enum NCAILoadDispatchKind : byte
{
    ActorSpawn,
    PhysicalPerception,
}

public enum NCAILoadWorkKind : byte
{
    ActorSpawn,
    PhysicalPerception,
    DamageEvents,
}

/// <summary>
/// Immutable operational snapshot consumed by the F7 administration surface.
/// </summary>
public sealed record NCAILoadGovernorSnapshot(
    bool Active,
    NCAILoadState State,
    NCAILoadPressureReason Reasons,
    float SmoothedFrameMilliseconds,
    float AILifeWorkRatio,
    int PhysicalActors,
    float MaximumQueueRatio,
    int BackloggedSubsystems,
    int CandidateSamples,
    long TransitionCount,
    NCActiveWorldTime LastTransitionAt,
    string? ConfigurationError);

/// <summary>
/// One bounded state transition retained for administrator diagnostics.
/// </summary>
public readonly record struct NCAILoadGovernorTransition(
    NCAILoadState From,
    NCAILoadState To,
    NCAILoadPressureReason Reasons,
    NCActiveWorldTime At);

/// <summary>
/// Bounded runtime telemetry. Percentiles are calculated only when an administrator requests a snapshot.
/// </summary>
public sealed record NCAILoadTelemetrySnapshot(
    float FrameP50Milliseconds,
    float FrameP95Milliseconds,
    float FrameP99Milliseconds,
    float AILifeWorkMilliseconds,
    IReadOnlyDictionary<NCAILoadWorkKind, float> WorkMillisecondsByKind,
    long AllocatedBytesSinceLastSample,
    int Gen0CollectionsSinceLastSample,
    int Gen1CollectionsSinceLastSample,
    int Gen2CollectionsSinceLastSample,
    int FrameSamples);

internal readonly record struct NCAILoadGovernorThresholds(
    int DegradeSamples,
    int RecoverSamples,
    float ElevatedEnterFrameMilliseconds,
    float ElevatedExitFrameMilliseconds,
    float CriticalEnterFrameMilliseconds,
    float CriticalExitFrameMilliseconds,
    float ElevatedEnterWorkRatio,
    float ElevatedExitWorkRatio,
    float CriticalEnterWorkRatio,
    float CriticalExitWorkRatio,
    float ElevatedEnterQueueRatio,
    float ElevatedExitQueueRatio,
    float CriticalEnterQueueRatio,
    float CriticalExitQueueRatio,
    int ElevatedBacklogCount,
    int CriticalBacklogCount,
    int CriticalExitBacklogCount)
{
    public static NCAILoadGovernorThresholds FromPrototype(NCAILoadGovernorPolicyPrototype policy) => new(
        policy.DegradeSamples,
        policy.RecoverSamples,
        policy.ElevatedEnterFrameMilliseconds,
        policy.ElevatedExitFrameMilliseconds,
        policy.CriticalEnterFrameMilliseconds,
        policy.CriticalExitFrameMilliseconds,
        policy.ElevatedEnterWorkRatio,
        policy.ElevatedExitWorkRatio,
        policy.CriticalEnterWorkRatio,
        policy.CriticalExitWorkRatio,
        policy.ElevatedEnterQueueRatio,
        policy.ElevatedExitQueueRatio,
        policy.CriticalEnterQueueRatio,
        policy.CriticalExitQueueRatio,
        policy.ElevatedBacklogCount,
        policy.CriticalBacklogCount,
        policy.CriticalExitBacklogCount);
}

internal readonly record struct NCAILoadGovernorSample(
    float FrameMilliseconds,
    float WorkRatio,
    float QueueRatio,
    int BacklogCount);

internal readonly record struct NCAILoadGovernorDecision(
    NCAILoadState State,
    NCAILoadPressureReason Reasons,
    int CandidateSamples,
    bool Transitioned);

/// <summary>
/// Pure hysteresis state machine. Keeping transition policy free of ECS state makes boundary behavior testable.
/// </summary>
internal sealed class NCAILoadGovernorStateMachine
{
    private NCAILoadState _candidate;
    private int _candidateSamples;

    public NCAILoadState State { get; private set; }

    public int CandidateSamples => _candidateSamples;

    public void Reset()
    {
        State = NCAILoadState.Normal;
        _candidate = NCAILoadState.Normal;
        _candidateSamples = 0;
    }

    public NCAILoadGovernorDecision Step(
        NCAILoadGovernorSample sample,
        NCAILoadPressureReason reportedReasons,
        NCAILoadGovernorThresholds thresholds)
    {
        var desired = DesiredState(sample, thresholds);
        var target = State switch
        {
            NCAILoadState.Normal => desired,
            NCAILoadState.Elevated when desired == NCAILoadState.Critical => NCAILoadState.Critical,
            NCAILoadState.Elevated when RecoveredFromElevated(sample, thresholds) => NCAILoadState.Normal,
            NCAILoadState.Critical when RecoveredFromCritical(sample, thresholds) => NCAILoadState.Elevated,
            _ => State,
        };

        if (target == State)
        {
            _candidate = State;
            _candidateSamples = 0;
            return new NCAILoadGovernorDecision(State, reportedReasons, 0, false);
        }

        if (_candidate != target)
        {
            _candidate = target;
            _candidateSamples = 0;
        }

        _candidateSamples++;
        var required = target > State ? thresholds.DegradeSamples : thresholds.RecoverSamples;
        if (_candidateSamples < required)
            return new NCAILoadGovernorDecision(State, reportedReasons, _candidateSamples, false);

        State = target;
        _candidate = State;
        _candidateSamples = 0;
        return new NCAILoadGovernorDecision(State, reportedReasons, 0, true);
    }

    private static NCAILoadState DesiredState(
        NCAILoadGovernorSample sample,
        NCAILoadGovernorThresholds thresholds)
    {
        if (sample.FrameMilliseconds >= thresholds.CriticalEnterFrameMilliseconds ||
            sample.WorkRatio >= thresholds.CriticalEnterWorkRatio ||
            sample.QueueRatio >= thresholds.CriticalEnterQueueRatio ||
            sample.BacklogCount >= thresholds.CriticalBacklogCount)
        {
            return NCAILoadState.Critical;
        }

        if (sample.FrameMilliseconds >= thresholds.ElevatedEnterFrameMilliseconds ||
            sample.WorkRatio >= thresholds.ElevatedEnterWorkRatio ||
            sample.QueueRatio >= thresholds.ElevatedEnterQueueRatio ||
            sample.BacklogCount >= thresholds.ElevatedBacklogCount)
        {
            return NCAILoadState.Elevated;
        }

        return NCAILoadState.Normal;
    }

    private static bool RecoveredFromElevated(
        NCAILoadGovernorSample sample,
        NCAILoadGovernorThresholds thresholds) =>
        sample.FrameMilliseconds <= thresholds.ElevatedExitFrameMilliseconds &&
        sample.WorkRatio <= thresholds.ElevatedExitWorkRatio &&
        sample.QueueRatio <= thresholds.ElevatedExitQueueRatio &&
        sample.BacklogCount < thresholds.ElevatedBacklogCount;

    private static bool RecoveredFromCritical(
        NCAILoadGovernorSample sample,
        NCAILoadGovernorThresholds thresholds) =>
        sample.FrameMilliseconds <= thresholds.CriticalExitFrameMilliseconds &&
        sample.WorkRatio <= thresholds.CriticalExitWorkRatio &&
        sample.QueueRatio <= thresholds.CriticalExitQueueRatio &&
        sample.BacklogCount < thresholds.CriticalExitBacklogCount;
}
