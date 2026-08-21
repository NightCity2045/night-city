// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Cognition;

public readonly record struct NCCognitionPerceptionInput(
    long Revision,
    bool HostilePresent,
    bool CriticalPersonPresent,
    bool ConversationRequested);

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
    string? LastFailure);

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
    NCCognitionIntentionSnapshot intention,
    NCCognitionPlanStep step) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public EntityUid? EntityUid { get; } = entityUid;
    public NCCognitionIntentionSnapshot Intention { get; } = intention;
    public NCCognitionPlanStep Step { get; } = step;
    public bool Handled { get; set; }
    public bool Completed { get; set; }
    public string? Failure { get; set; }
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
