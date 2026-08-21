// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Cognition;

public enum NCCognitionUtilityCurveKind : byte
{
    Linear,
    InverseLinear,
    Step,
    Bell,
}

[Prototype("ncCognitionUtilityCurve")]
public sealed partial class NCCognitionUtilityCurvePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public NCCognitionUtilityCurveKind Kind { get; private set; }
    [DataField(required: true)] public float MinimumInput { get; private set; }
    [DataField(required: true)] public float MaximumInput { get; private set; }
    [DataField] public float MinimumOutput { get; private set; }
    [DataField] public float MaximumOutput { get; private set; } = 1f;
    [DataField] public float Threshold { get; private set; } = 0.5f;
}

/// <summary>
/// One data-driven contribution to Utility. Missing or stale beliefs use the explicit neutral input.
/// </summary>
[DataDefinition]
public sealed partial class NCCognitionConsideration
{
    [DataField(required: true)] public string BeliefKey { get; private set; } = string.Empty;
    [DataField(required: true)] public ProtoId<NCCognitionUtilityCurvePrototype> Curve { get; private set; }
    [DataField(required: true)] public float Weight { get; private set; }
    [DataField] public float MissingInput { get; private set; } = 0.5f;
    [DataField] public bool Required { get; private set; }
}

[Prototype("ncCognitionCapability")]
public sealed partial class NCCognitionCapabilityPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public NCCognitionDomain Domain { get; private set; }
    [DataField(required: true)] public NCCognitionTaskKind TaskKind { get; private set; }
    [DataField(required: true)] public NCCognitionPlannerKind PreferredPlanner { get; private set; }
    [DataField] public NCCognitionPlannerKind FallbackPlanner { get; private set; } =
        NCCognitionPlannerKind.DeterministicFallback;
    [DataField(required: true)] public float BaseUtility { get; private set; }
    [DataField] public int Priority { get; private set; }
    [DataField] public string[] RequiredBeliefs { get; private set; } = [];
    [DataField] public string[] ForbiddenBeliefs { get; private set; } = [];
    [DataField] public NCCognitionConsideration[] Considerations { get; private set; } = [];
    [DataField] public NCCognitionProcedureStep[] ProcedureSteps { get; private set; } = [];
    [DataField] public NCCognitionStrategicOption[] StrategicOptions { get; private set; } = [];
    [DataField] public bool RequiresMaterialization { get; private set; }
    [DataField] public bool InterruptsRoutine { get; private set; }
}

[DataDefinition]
public sealed partial class NCCognitionProcedureStep
{
    [DataField(required: true)] public string Id { get; private set; } = string.Empty;
    [DataField(required: true)] public string Action { get; private set; } = string.Empty;
    [DataField] public string[] DependsOn { get; private set; } = [];
    [DataField] public bool Optional { get; private set; }
}

/// <summary>
/// One server-authored root branch for bounded strategic search. Outcome bounds come from the owning domain;
/// cognition samples them deterministically and never invents a domain rule.
/// </summary>
[DataDefinition]
public sealed partial class NCCognitionStrategicOption
{
    [DataField(required: true)] public string Id { get; private set; } = string.Empty;
    [DataField(required: true)] public float MinimumOutcome { get; private set; }
    [DataField(required: true)] public float MaximumOutcome { get; private set; }
    [DataField(required: true)] public NCCognitionProcedureStep[] ProcedureSteps { get; private set; } = [];
}

[Prototype("ncCognitionCapabilityProfile")]
public sealed partial class NCCognitionCapabilityProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public ProtoId<NCCognitionCapabilityPrototype>[] Capabilities { get; private set; } = [];
}

[Prototype("ncCognitionDecisionPolicy")]
public sealed partial class NCCognitionDecisionPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public TimeSpan EvaluationInterval { get; private set; }
    [DataField(required: true)] public TimeSpan MinimumCommitment { get; private set; }
    [DataField(required: true)] public TimeSpan IntentionLifetime { get; private set; }
    [DataField(required: true)] public TimeSpan BeliefLifetime { get; private set; }
    [DataField(required: true)] public TimeSpan BeliefAgingTime { get; private set; }
    [DataField(required: true)] public float AgingConfidenceMultiplier { get; private set; }
    [DataField(required: true)] public float StaleConfidenceMultiplier { get; private set; }
    [DataField(required: true)] public float SwitchUtilityDelta { get; private set; }
    [DataField(required: true)] public float SwitchCost { get; private set; }
    [DataField(required: true)] public float MinimumBeliefConfidence { get; private set; }
    [DataField(required: true)] public int MaximumBeliefs { get; private set; }
    [DataField(required: true)] public int MaximumDesires { get; private set; }
}

[DataDefinition]
public sealed partial class NCCognitionPlannerBudget
{
    [DataField(required: true)] public NCCognitionPlannerKind Planner { get; private set; }
    [DataField(required: true)] public int MaximumNodesPerTick { get; private set; }
    [DataField(required: true)] public int MaximumTotalNodes { get; private set; }
    [DataField(required: true)] public float MaximumMillisecondsPerTick { get; private set; }
    [DataField(required: true)] public float ExplorationConstant { get; private set; }
}

[Prototype("ncCognitionRoutingProfile")]
public sealed partial class NCCognitionRoutingProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public Dictionary<NCCognitionTaskKind, NCCognitionPlannerKind> Routes { get; private set; } = [];
    [DataField(required: true)] public NCCognitionPlannerBudget[] Budgets { get; private set; } = [];
}

[Prototype("ncCognitionRuntimePolicy")]
public sealed partial class NCCognitionRuntimePolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public int MaximumTrackedActors { get; private set; }
    [DataField(required: true)] public int MaximumQueuedActors { get; private set; }
    [DataField(required: true)] public int MaximumDispatchesPerTick { get; private set; }
    [DataField(required: true)] public int MaximumConcurrentLoads { get; private set; }
    [DataField(required: true)] public int MaximumFacts { get; private set; }
    [DataField(required: true)] public int MaximumCapabilities { get; private set; }
    [DataField(required: true)] public int MaximumPlanSteps { get; private set; }
    [DataField(required: true)] public int MaximumTraceEntries { get; private set; }
    [DataField(required: true)] public int MaximumAbstractTraceEntries { get; private set; }
    [DataField(required: true)] public int MaximumCachedPlans { get; private set; }
    [DataField(required: true)] public int MaximumInvalidationsPerTick { get; private set; }
    [DataField(required: true)] public float MaximumPlanningMillisecondsPerTick { get; private set; }
    [DataField(required: true)] public TimeSpan LoadRetryDelay { get; private set; }
    [DataField(required: true)] public TimeSpan FailureRetryDelay { get; private set; }
    [DataField(required: true)] public TimeSpan MinimumAbstractEvaluationInterval { get; private set; }
}
