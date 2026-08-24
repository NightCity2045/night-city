// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Cognition;

public enum NCHumanActionRuntimeState : byte
{
    Pending,
    Running,
    Completed,
    Failed,
    Interrupted,
}

public readonly record struct NCHumanActionTraceEntry(
    long Sequence,
    NCActiveWorldTime At,
    NCHumanActionRuntimeState State,
    string Event,
    string Detail);

public sealed record NCHumanActionRuntimeSnapshot(
    Guid InstanceId,
    NCActorId ActorId,
    EntityUid? EntityUid,
    Guid IntentionId,
    Guid PlanId,
    string StepId,
    string ActionId,
    NCHumanActionExecutorKind Executor,
    NCCognitionSubjectRef Target,
    NCHumanActionRuntimeState State,
    NCActiveWorldTime StartedAt,
    NCActiveWorldTime UpdatedAt,
    NCActiveWorldTime TimeoutAt,
    string? Failure,
    NCHumanActionTraceEntry[] Trace);

public readonly record struct NCHumanActionRouterCounters(
    bool Active,
    int Running,
    long Started,
    long Completed,
    long Failed,
    long Interrupted,
    long TimedOut,
    long Unhandled,
    string? ConfigurationError);

/// <summary>
/// One bounded execution pulse. A domain subscriber may claim only action IDs it owns and must report a real result.
/// Leaving both Completed and Failure empty keeps the action running until the next scheduled cognition pulse.
/// </summary>
public sealed class NCHumanActionExecutionEvent(
    Guid instanceId,
    NCActorId actorId,
    EntityUid? entityUid,
    Guid intentionId,
    Guid planId,
    NCCognitionIntentionSnapshot intention,
    NCCognitionPlanStep step,
    NCCognitionSubjectRef target,
    NCHumanActionPrototype action,
    NCActiveWorldTime startedAt,
    NCActiveWorldTime now) : EntityEventArgs
{
    public Guid InstanceId { get; } = instanceId;
    public NCActorId ActorId { get; } = actorId;
    public EntityUid? EntityUid { get; } = entityUid;
    public Guid IntentionId { get; } = intentionId;
    public Guid PlanId { get; } = planId;
    public NCCognitionIntentionSnapshot Intention { get; } = intention;
    public NCCognitionPlanStep Step { get; } = step;
    public NCCognitionSubjectRef Target { get; } = target;
    public NCHumanActionPrototype Action { get; } = action;
    public NCActiveWorldTime StartedAt { get; } = startedAt;
    public NCActiveWorldTime Now { get; } = now;
    public bool Handled { get; set; }
    public bool Completed { get; set; }
    public string? Failure { get; set; }
}

/// <summary>Cleanup notification for steering, DoAfter tracking and other executor-owned transient state.</summary>
public sealed class NCHumanActionInterruptedEvent(
    Guid instanceId,
    NCActorId actorId,
    EntityUid? entityUid,
    string actionId,
    NCHumanActionInterruptKind reason) : EntityEventArgs
{
    public Guid InstanceId { get; } = instanceId;
    public NCActorId ActorId { get; } = actorId;
    public EntityUid? EntityUid { get; } = entityUid;
    public string ActionId { get; } = actionId;
    public NCHumanActionInterruptKind Reason { get; } = reason;
}
