// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Cognition;

[Serializable, NetSerializable]
public enum NCCognitionReferenceKind : byte
{
    None,
    Actor,
    Group,
    Party,
    Zone,
    Incident,
    Order,
    StableKey,
}

/// <summary>
/// Persistent-safe cognition reference. The canonical value is a stable domain key and can never contain EntityUid.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCCognitionSubjectRef(NCCognitionReferenceKind Kind, string StableId)
{
    public bool IsValid => Kind != NCCognitionReferenceKind.None && !string.IsNullOrWhiteSpace(StableId);
    public static NCCognitionSubjectRef None => new(NCCognitionReferenceKind.None, string.Empty);
}

[Serializable, NetSerializable]
public enum NCCognitionValueKind : byte
{
    Boolean,
    Number,
    StableKey,
}

[Serializable, NetSerializable]
public readonly record struct NCCognitionValue(
    NCCognitionValueKind Kind,
    bool Boolean,
    float Number,
    string StableKey)
{
    public static NCCognitionValue FromBoolean(bool value) =>
        new(NCCognitionValueKind.Boolean, value, 0f, string.Empty);

    public static NCCognitionValue FromNumber(float value) =>
        new(NCCognitionValueKind.Number, false, value, string.Empty);

    public static NCCognitionValue FromStableKey(string value) =>
        new(NCCognitionValueKind.StableKey, false, 0f, value);
}

[Serializable, NetSerializable]
public enum NCCognitionDomain : byte
{
    Core,
    Routine,
    Jobs,
    Economy,
    Social,
    Crime,
    Faction,
    Medical,
    Services,
    Safety,
    Cyberware,
}

[Serializable, NetSerializable]
public enum NCBeliefFreshness : byte
{
    Current,
    Aging,
    Stale,
    Disputed,
}

/// <summary>
/// One versioned observation from an authoritative domain. Facts are round-local Blackboard input.
/// </summary>
[Serializable, NetSerializable]
public sealed record NCCognitionFact(
    string Key,
    NCCognitionSubjectRef Subject,
    NCCognitionDomain Domain,
    string Source,
    long SourceVersion,
    NCCognitionValue Value,
    NCActiveWorldTime ObservedAt,
    NCActiveWorldTime ExpiresAt);

/// <summary>
/// Actor-specific interpretation of a fact. Confidence and freshness prevent omniscient NPC decisions.
/// </summary>
[Serializable, NetSerializable]
public sealed record NCBeliefSnapshot(
    Guid BeliefId,
    string Key,
    NCCognitionSubjectRef Subject,
    NCCognitionDomain Domain,
    string Source,
    long SourceVersion,
    NCCognitionValue Value,
    float Confidence,
    NCBeliefFreshness Freshness,
    NCActiveWorldTime ObservedAt,
    NCActiveWorldTime ExpiresAt,
    long Revision);

[Serializable, NetSerializable]
public enum NCCognitionTaskKind : byte
{
    Routine,
    ImmediateReaction,
    KnownProcedure,
    GroupOperation,
    StrategicChoice,
    SocialConversation,
}

[Serializable, NetSerializable]
public enum NCCognitionPlannerKind : byte
{
    StateTree,
    Reactive,
    Htn,
    PartialOrder,
    Mcts,
    ConversationStateTree,
    DeterministicFallback,
}

[Serializable, NetSerializable]
public enum NCCognitionPlanStatus : byte
{
    None,
    Planning,
    Ready,
    Executing,
    Completed,
    Invalidated,
    Failed,
}

[Serializable, NetSerializable]
public enum NCCognitionDependencyKind : byte
{
    ActorRoot,
    BeliefSet,
    Target,
    Inventory,
    Location,
    Access,
    Incident,
    Order,
    Rules,
    Group,
}

[Serializable, NetSerializable]
public sealed record NCCognitionDesireSnapshot(
    Guid DesireId,
    string CapabilityId,
    NCCognitionSubjectRef Target,
    NCCognitionTaskKind TaskKind,
    float Utility,
    int Priority,
    NCActiveWorldTime EvaluatedAt,
    NCActiveWorldTime ExpiresAt);

[Serializable, NetSerializable]
public sealed record NCCognitionIntentionSnapshot(
    Guid IntentionId,
    string CapabilityId,
    NCCognitionSubjectRef Target,
    NCCognitionTaskKind TaskKind,
    float UtilityAtCommitment,
    float Commitment,
    int Priority,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime MinimumCommitmentUntil,
    NCActiveWorldTime ExpiresAt,
    long Revision);

[Serializable, NetSerializable]
public sealed record NCBeliefSetSnapshot(
    NCActorId ActorId,
    NCBeliefSnapshot[] Beliefs,
    long DataVersion,
    Guid LastEventId);

/// <summary>
/// Durable cognition root. Blackboard, planning frontier and physical nodes intentionally do not live here.
/// </summary>
[Serializable, NetSerializable]
public sealed record NCCognitionSnapshot(
    NCActorId ActorId,
    string DecisionPolicyId,
    string CapabilityProfileId,
    string RoutingProfileId,
    long BeliefSetVersion,
    NCCognitionDesireSnapshot? CurrentDesire,
    NCCognitionIntentionSnapshot? CurrentIntention,
    NCActiveWorldTime NextDecisionAt,
    long DataVersion,
    Guid LastEventId);

public sealed record NCBeliefSetRecord(
    NCBeliefSetSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch Epoch,
    NCActiveWorldTime UpdatedAt);

public sealed record NCCognitionRecord(
    NCCognitionSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch Epoch,
    NCActiveWorldTime UpdatedAt);

[Serializable, NetSerializable]
public sealed record NCCognitionPlanDependency(
    NCCognitionDependencyKind Kind,
    string StableId,
    long Version);

[Serializable, NetSerializable]
public sealed record NCCognitionPlanStep(
    string StepId,
    string ActionId,
    NCCognitionSubjectRef Target,
    string[] DependsOn,
    bool Optional);

[Serializable, NetSerializable]
public sealed record NCCognitionPlanSnapshot(
    Guid PlanId,
    Guid IntentionId,
    NCCognitionPlannerKind Planner,
    NCCognitionPlanStatus Status,
    NCCognitionPlanDependency[] Dependencies,
    NCCognitionPlanStep[] Steps,
    int CurrentStep,
    int ExpandedNodes,
    long DeterministicSeed,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    string? Failure);

