// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Trauma;
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

[ByRefEvent]
public readonly record struct NCWoundProgressedEvent(
    Guid WoundId,
    NCWoundStage PreviousStage,
    NCWoundStage Stage,
    NCInfectionStage PreviousInfectionStage,
    NCInfectionStage InfectionStage,
    bool Reopened,
    bool ResolvedToScar);
