// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
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

[ByRefEvent]
public readonly record struct NCWoundProgressedEvent(
    Guid WoundId,
    NCWoundStage PreviousStage,
    NCWoundStage Stage,
    NCInfectionStage PreviousInfectionStage,
    NCInfectionStage InfectionStage,
    bool Reopened,
    bool ResolvedToScar);
