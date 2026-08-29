// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Affordances;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Actors;

public enum NCCityPhysicalMode : byte
{
    SafeIdle,
    Traveling,
    Combat,
    Medical,
    Police,
    Conversation,
    Scripted,
    Incapacitated,
}

public enum NCCityPhysicalCondition : byte
{
    TravelIntent,
    CombatActive,
    ThreatObserved,
    MedicalClaim,
    PoliceClaim,
    ConversationClaim,
    ScriptedClaim,
    Detained,
    Incapacitated,
}

public enum NCCityPhysicalBehaviorOwner : byte
{
    None,
    RouteExecutor,
    ExistingNpc,
    ExternalSystem,
}

public enum NCCityPhysicalFallback : byte
{
    SafeIdle,
    Replan,
    Retreat,
    ReturnToPublicZone,
    AbortPlan,
}

/// <summary>
/// One prioritized physical state rule. Conditions are objective ECS facts supplied by adapters, never skill checks.
/// </summary>
[DataDefinition]
public sealed partial class NCCityPhysicalStateRule
{
    [DataField(required: true)]
    public NCCityPhysicalMode Mode { get; private set; }

    [DataField(required: true)]
    public int Priority { get; private set; }

    [DataField]
    public HashSet<NCCityPhysicalCondition> RequiredAll { get; private set; } = [];

    [DataField]
    public HashSet<NCCityPhysicalCondition> Forbidden { get; private set; } = [];

    [DataField(required: true)]
    public NCCityPhysicalBehaviorOwner Owner { get; private set; }

    [DataField(required: true)]
    public TimeSpan EvaluationInterval { get; private set; }

}

/// <summary>
/// Cheap event/scheduler-driven HFSM definition for materialized actors.
/// </summary>
[Prototype("ncCityPhysicalStateTree")]
public sealed partial class NCCityPhysicalStateTreePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public int MaximumRules { get; private set; }

    [DataField(required: true)]
    public List<NCCityPhysicalStateRule> Rules { get; private set; } = [];

    [DataField(required: true)]
    public NCCityPhysicalFallback BlockedFallback { get; private set; }
}

/// <summary>
/// Local observation throughput policy. It is separate from cognition and never creates persistent beliefs directly.
/// </summary>
[Prototype("ncCityLocalPerceptionProfile")]
public sealed partial class NCCityLocalPerceptionProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public float Radius { get; private set; }

    [DataField(required: true)]
    public int MaximumObservedEntities { get; private set; }

    [DataField(required: true)]
    public int MaximumLineOfSightChecks { get; private set; }

    [DataField(required: true)]
    public TimeSpan EvaluationInterval { get; private set; }

    [DataField(required: true)]
    public TimeSpan WitnessedEventLifetime { get; private set; }
}

[Prototype("ncCityLocalPerceptionPolicy")]
public sealed partial class NCCityLocalPerceptionPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public int MaximumUpdatesPerTick { get; private set; }

    [DataField]
    public int MaximumIncidentCorrelations { get; private set; } = 1024;
}

/// <summary>
/// Data-driven local activity policy shared by every physical Human. Values limit work per actor, never population.
/// </summary>
[Prototype("ncHumanRoutineActivityPolicy")]
public sealed partial class NCHumanRoutineActivityPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public int MaximumCatalogCandidates { get; private set; }

    [DataField(required: true)]
    public float ArrivalRange { get; private set; }

    [DataField(required: true)]
    public TimeSpan MinimumDwellTime { get; private set; }

    [DataField(required: true)]
    public TimeSpan MaximumDwellTime { get; private set; }

    [DataField(required: true)]
    public int MaximumFallbackTargetAttempts { get; private set; }

    [DataField(required: true)]
    public List<NCHumanRoutineActivityDefinition> Activities { get; private set; } = [];

    /// <summary>Safe physical activities used after a routine target or route becomes unavailable.</summary>
    [DataField(required: true)]
    public List<NCHumanRoutineActivityDefinition> FallbackActivities { get; private set; } = [];
}

[DataDefinition]
public sealed partial class NCHumanRoutineActivityDefinition
{
    [DataField(required: true)]
    public string Activity { get; private set; } = string.Empty;

    [DataField(required: true)]
    public List<NCCityAffordanceKind> Affordances { get; private set; } = [];

    /// <summary>For work, prefer the affordance authored by the resident livelihood profile.</summary>
    [DataField]
    public bool PreferLivelihoodAffordance { get; private set; }
}
