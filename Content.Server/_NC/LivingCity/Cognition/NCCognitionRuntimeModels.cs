// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Cognition;

public readonly record struct NCCognitionPerceptionInput(
    long Revision,
    bool HostilePresent,
    bool CriticalPersonPresent,
    bool ConversationRequested,
    IReadOnlyList<NCCognitionObservedSubject>? Subjects = null);

public readonly record struct NCCognitionObservedSubject(
    NCCognitionSubjectRef Subject,
    string FactKey,
    float Confidence,
    NCActiveWorldTime ObservedAt,
    NCActiveWorldTime ExpiresAt);

public sealed record NCCognitionCapabilityCandidate(
    ProtoId<NCCognitionCapabilityPrototype> Capability,
    NCCognitionSubjectRef Target,
    float Utility,
    int Priority,
    NCCognitionTaskKind TaskKind,
    NCCognitionPlannerKind PreferredPlanner,
    NCCognitionPlannerKind FallbackPlanner,
    bool InterruptsRoutine,
    bool RequiresMaterialization);

public readonly record struct NCCognitionTraceEntry(
    long Sequence,
    NCActiveWorldTime At,
    string Stage,
    string Event,
    string Detail);

public enum NCHumanActivityRuntimeState : byte
{
    Active,
    WaitingUntilDue,
    NoAffordance,
    BlockedRoute,
    Recovering,
    SocialIdle,
    UnemployedFreeTime,
    NoValidPlan,
}

public readonly record struct NCHumanActivityRuntimeSnapshot(
    NCHumanActivityRuntimeState State,
    string Reason,
    string FallbackAction,
    NCActiveWorldTime NextWakeAt);

public sealed record NCCognitionRuntimeSnapshot(
    NCActorId ActorId,
    EntityUid? EntityUid,
    long ActorVersion,
    long CognitionVersion,
    long BeliefSetVersion,
    NCCognitionFact[] Facts,
    NCBeliefSnapshot[] Beliefs,
    NCCognitionDesireSnapshot[] Desires,
    NCCognitionIntentionSnapshot? Intention,
    NCCognitionPlanSnapshot? Plan,
    NCCognitionTraceEntry[] Trace,
    NCActiveWorldTime LastDecisionAt,
    NCActiveWorldTime NextDecisionAt,
    string? LastFailure,
    NCHumanActivityRuntimeSnapshot HumanActivity);

public readonly record struct NCCognitionCounters(
    bool Active,
    int TrackedActors,
    int ScheduledActors,
    int LoadsInFlight,
    int PlanningFrontiers,
    int CachedPlans,
    int PendingInvalidations,
    long Decisions,
    long IntentionSwitches,
    long PlansBuilt,
    long PlansInvalidated,
    long FallbackPlans,
    long MctsNodesExpanded,
    string? ConfigurationError);

/// <summary>
/// Round-local resumable MCTS frontier. It is never persisted and contains no world authority or EntityUid.
/// </summary>
public sealed class NCCognitionMctsFrontierState(Guid planId, NCCognitionMctsBranchState[] branches)
{
    public Guid PlanId { get; } = planId;
    public long RolloutSequence;
    public int TotalVisits;
    public NCCognitionMctsBranchState[] Branches { get; } = branches;
}

public sealed class NCCognitionMctsBranchState(string optionId)
{
    public string OptionId { get; } = optionId;
    public int Visits;
    public double TotalReward;
}

/// <summary>
/// Extensible authoritative provider boundary. Subscribers may add bounded facts; they cannot commit beliefs or act.
/// </summary>
public sealed class NCCognitionCollectFactsEvent(
    NCActorId actorId,
    NCCityActorRecord actor,
    NCActiveWorldTime now,
    NCCognitionFactSink sink) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public NCCityActorRecord Actor { get; } = actor;
    public NCActiveWorldTime Now { get; } = now;
    public NCCognitionFactSink Sink { get; } = sink;
}

/// <summary>
/// Typed execution request. Subscribers must explicitly claim a known action; arbitrary rules or mutation payloads
/// are never accepted by this neutral cognition boundary.
/// </summary>
public sealed class NCCognitionExecuteStepEvent(
    NCActorId actorId,
    EntityUid? entityUid,
    Guid planId,
    NCCognitionIntentionSnapshot intention,
    NCCognitionPlanStep step) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public EntityUid? EntityUid { get; } = entityUid;
    public Guid PlanId { get; } = planId;
    public NCCognitionIntentionSnapshot Intention { get; } = intention;
    public NCCognitionPlanStep Step { get; } = step;
    public bool Handled { get; set; }
    public bool Completed { get; set; }
    public string? Failure { get; set; }
}

/// <summary>
/// Authoritative physical-work boundary shared by every profession. A role adapter may handle only its own profile
/// and must execute through ordinary gameplay systems; cognition itself cannot simulate a successful interaction.
/// </summary>
public sealed class NCProfessionalWorkRequestedEvent(
    NCActorId actorId,
    EntityUid worker,
    ProtoId<DepartmentPrototype> organization,
    ProtoId<JobPrototype> job,
    NCProfessionProfilePrototype profile) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public EntityUid Worker { get; } = worker;
    public ProtoId<DepartmentPrototype> Organization { get; } = organization;
    public ProtoId<JobPrototype> Job { get; } = job;
    public NCProfessionProfilePrototype Profile { get; } = profile;
    public bool Handled { get; set; }
    public bool Completed { get; set; }
    public string? Failure { get; set; }
}

/// <summary>
/// Read-only post-decision boundary. Optional advisers may request a future reevaluation, but cannot replace the
/// authoritative BDI result or execute a gameplay action from this event.
/// </summary>
public sealed class NCCognitionDecisionAppliedEvent(
    NCActorId actorId,
    NCCityActorCognitionTier cognitionTier,
    long actorVersion,
    NCCognitionDesireSnapshot[] desires,
    NCCognitionIntentionSnapshot? intention,
    NCActiveWorldTime decidedAt) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public NCCityActorCognitionTier CognitionTier { get; } = cognitionTier;
    public long ActorVersion { get; } = actorVersion;
    public NCCognitionDesireSnapshot[] Desires { get; } = desires;
    public NCCognitionIntentionSnapshot? Intention { get; } = intention;
    public NCActiveWorldTime DecidedAt { get; } = decidedAt;
}

public sealed class NCCognitionFactSink
{
    private readonly int _capacity;
    private readonly Dictionary<(string Key, NCCognitionSubjectRef Subject, string Source), NCCognitionFact> _facts = [];

    public NCCognitionFactSink(int capacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public bool Add(NCCognitionFact fact)
    {
        if (string.IsNullOrWhiteSpace(fact.Key) || string.IsNullOrWhiteSpace(fact.Source) ||
            fact.SourceVersion < 0 || !fact.ObservedAt.IsValid || fact.ExpiresAt < fact.ObservedAt)
            return false;
        var key = (fact.Key.Trim(), fact.Subject, fact.Source.Trim());
        if (!_facts.ContainsKey(key) && _facts.Count >= _capacity)
            return false;
        if (!_facts.TryGetValue(key, out var current) || current.SourceVersion <= fact.SourceVersion)
            _facts[key] = fact with { Key = key.Item1, Source = key.Item3 };
        return true;
    }

    public NCCognitionFact[] ToArray() => _facts.Values
        .OrderBy(value => value.Key, StringComparer.Ordinal)
        .ThenBy(value => value.Subject.StableId, StringComparer.Ordinal)
        .ThenBy(value => value.Source, StringComparer.Ordinal)
        .ToArray();
}

internal sealed record NCCognitionDecisionInput(
    NCCityActorRecord Actor,
    NCCognitionRecord Cognition,
    NCBeliefSetRecord Beliefs,
    NCCognitionFact[] Facts,
    bool Materialized,
    NCActiveWorldTime Now);

internal sealed record NCCognitionDecisionOutput(
    NCCognitionSnapshot Cognition,
    NCBeliefSetSnapshot Beliefs,
    NCCognitionDesireSnapshot[] Desires,
    NCCognitionPlanSnapshot? Plan,
    bool IntentionSwitched,
    bool UsedFallback,
    string? Error);
