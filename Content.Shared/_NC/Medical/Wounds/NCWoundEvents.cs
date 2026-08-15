// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Wounds;

[ByRefEvent]
public readonly record struct NCWoundCreatedEvent(
    Guid WoundId,
    ProtoId<NCTargetZonePrototype> TargetZone,
    NCWoundSeverity Severity,
    NCWoundType WoundType);

[ByRefEvent]
public readonly record struct NCWoundChangedEvent(
    Guid WoundId,
    NCWoundSeverity PreviousSeverity,
    NCWoundSeverity Severity);

/// <summary>
/// Server-local notification emitted only after one physical impact has produced an authoritative organic wound.
/// Consumers may add secondary consequences without subscribing to the trauma request owned by NCWoundSystem.
/// </summary>
[ByRefEvent]
public readonly record struct NCWoundImpactAppliedEvent(
    NCCombatTraumaRequest Request,
    NCWoundType WoundType,
    NCWoundSeverity Severity,
    float ExternalBleedRate);

/// <summary>
/// Dedicated server-local handoff to the severance resolver. A distinct event type is required because
/// Robust permits only one component/event subscription pair and the blood-impact system owns the event above.
/// </summary>
[ByRefEvent]
public readonly record struct NCSeveranceAssessmentEvent(
    NCCombatTraumaRequest Request,
    NCWoundType WoundType,
    NCWoundSeverity Severity,
    float AppliedTrauma);

/// <summary>
/// Raised after the live organ subtree has been detached so WoundSystem can create the remaining stump wound.
/// </summary>
[ByRefEvent]
public readonly record struct NCTraumaticAmputationEvent(
    NCCombatTraumaRequest SourceRequest,
    ProtoId<OrganCategoryPrototype> DetachedCategory,
    ProtoId<OrganCategoryPrototype> ParentCategory,
    ProtoId<NCTargetZonePrototype> TargetZone,
    ProtoId<NCTraumaProfilePrototype> StumpTraumaProfile,
    float StumpTrauma,
    bool Mechanical);

[ByRefEvent]
public readonly record struct NCWoundProgressedEvent(
    Guid WoundId,
    NCWoundStage PreviousStage,
    NCWoundStage Stage,
    NCInfectionStage PreviousInfectionStage,
    NCInfectionStage InfectionStage,
    bool Reopened,
    bool ResolvedToScar);
