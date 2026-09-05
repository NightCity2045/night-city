// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Neural;

/// <summary>Stable round-local identity of one server-authored reason an NPC may choose to act.</summary>
[Serializable, NetSerializable]
public readonly record struct NCWorldOpportunityId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;

    public override string ToString() => Value.ToString("D");
}

/// <summary>Whether an opportunity refers to a currently perceived subject or a known semantic location.</summary>
public enum NCWorldOpportunityTargetKind : byte
{
    Subject,
    Location,
}

/// <summary>Closed, server-verified station state predicates used by data-authored job opportunities.</summary>
public enum NCStationWorkOpportunitySource : byte
{
    PendingCargoOrder,
    ReagentDispenser,
    Microwave,
    HarvestablePlant,
    UnlockableResearch,
    DirtyPuddle,
    DamagedEntity,
}

/// <summary>
/// Bounded scoring and retention policy for the round-local World Opportunity Bus. Values rank context only; they
/// never select an NPC intent or authorize a world mutation.
/// </summary>
[Prototype("ncWorldOpportunityPolicy")]
public sealed partial class NCWorldOpportunityPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public int MaximumTotal { get; private set; } = 8192;
    [DataField] public int MaximumPerActor { get; private set; } = 64;
    [DataField] public int MaximumPerQuery { get; private set; } = 32;
    [DataField] public int MaximumAffordanceKeys { get; private set; } = 16;
    [DataField] public int RewardWeight { get; private set; } = 4;
    [DataField] public int UrgencyWeight { get; private set; } = 6;
    [DataField] public int RiskWeight { get; private set; } = 2;
    [DataField] public int UrgentEventReward { get; private set; } = 800;
    [DataField] public int UrgentEventUrgency { get; private set; } = 1000;
    [DataField] public int UrgentEventRisk { get; private set; } = 900;
    [DataField] public int JobObligationReward { get; private set; } = 700;
    [DataField] public int JobObligationUrgency { get; private set; } = 500;
    [DataField] public int JobObligationRisk { get; private set; } = 100;
    [DataField] public int NearbyAffordanceReward { get; private set; } = 350;
    [DataField] public int NearbyAffordanceUrgency { get; private set; } = 200;
    [DataField] public int NearbyAffordanceRisk { get; private set; } = 100;
    [DataField] public TimeSpan UrgentEventLifetime { get; private set; } = TimeSpan.FromMinutes(2);
    [DataField] public TimeSpan JobObligationLifetime { get; private set; } = TimeSpan.FromMinutes(30);
    [DataField] public TimeSpan NearbyAffordanceLifetime { get; private set; } = TimeSpan.FromMinutes(2);
    [DataField] public List<ProtoId<NCCognitionCapabilityPrototype>> UrgentEventCapabilities { get; private set; } =
        new() { new ProtoId<NCCognitionCapabilityPrototype>("NCCognitionRespondToThreat") };
    [DataField] public List<ProtoId<NCCognitionCapabilityPrototype>> JobObligationCapabilities { get; private set; } =
        new() { new ProtoId<NCCognitionCapabilityPrototype>("NCCognitionMoveToStationDestination") };
    [DataField] public List<ProtoId<NCCognitionCapabilityPrototype>> JobActivityCapabilities { get; private set; } =
        new() { new ProtoId<NCCognitionCapabilityPrototype>("NCCognitionInspectAssignedWorkplace") };
}

/// <summary>
/// Maps one job-specific, directly perceived event to an actor-scoped opportunity. This supplies context only;
/// the model still chooses whether to act and the normal action router remains authoritative.
/// </summary>
[Prototype("ncObservedEventOpportunityRule")]
public sealed partial class NCObservedEventOpportunityRulePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string StableKey { get; private set; } = string.Empty;
    [DataField(required: true)] public ProtoId<JobPrototype> Job { get; private set; }
    [DataField(required: true)] public string ObservedEvent { get; private set; } = string.Empty;
    [DataField(required: true)] public string OpportunityKind { get; private set; } = string.Empty;
    [DataField(required: true)] public List<string> AffordanceKeys { get; private set; } = [];
    [DataField(required: true)] public List<ProtoId<NCCognitionCapabilityPrototype>> RequiredCapabilities
        { get; private set; } = [];
    [DataField(required: true)] public List<ProtoId<NCCognitionCapabilityPrototype>> CompletionCapabilities
        { get; private set; } = [];
    [DataField] public int Reward { get; private set; } = 800;
    [DataField] public int Urgency { get; private set; } = 900;
    [DataField] public int Risk { get; private set; } = 200;
    [DataField] public TimeSpan Lifetime { get; private set; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Maps real, currently perceived station state to one job-specific reason to act. The source is only a predicate;
/// GPT still chooses the intent and the normal gameplay authority performs the selected operation.
/// </summary>
[Prototype("ncStationWorkOpportunityRule")]
public sealed partial class NCStationWorkOpportunityRulePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string StableKey { get; private set; } = string.Empty;
    [DataField(required: true)] public List<ProtoId<JobPrototype>> Jobs { get; private set; } = [];
    [DataField(required: true)] public NCStationWorkOpportunitySource Source { get; private set; }
    [DataField(required: true)] public string OpportunityKind { get; private set; } = string.Empty;
    [DataField(required: true)] public List<string> AffordanceKeys { get; private set; } = [];
    [DataField(required: true)] public List<ProtoId<NCCognitionCapabilityPrototype>> RequiredCapabilities
        { get; private set; } = [];
    [DataField(required: true)] public List<ProtoId<NCCognitionCapabilityPrototype>> CompletionCapabilities
        { get; private set; } = [];
    [DataField] public int Reward { get; private set; } = 700;
    [DataField] public int Urgency { get; private set; } = 500;
    [DataField] public int Risk { get; private set; } = 100;
    [DataField] public TimeSpan Lifetime { get; private set; } = TimeSpan.FromMinutes(2);
}
