// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;

namespace Content.Shared._NC.Medical.Physiology;

/// <summary>
/// Event-only invalidation path used by the central physiology scheduler.
/// </summary>
[ByRefEvent]
public readonly record struct NCMedicalBodyChangedEvent(NCMedicalDirtyDomains Domains);

/// <summary>
/// One bounded update boundary emitted by the central scheduler. Longitudinal subsystems subscribe with
/// their own data component key; the scheduler does not depend on wound, drug or infection systems.
/// </summary>
[ByRefEvent]
public readonly record struct NCMedicalScheduledUpdateEvent(TimeSpan Elapsed);

/// <summary>
/// Extensibility query for systems that own timed state even when the body has no wound or acute
/// physiological instability. Handlers may only shorten <see cref="Delay"/> to their next due boundary.
/// </summary>
[ByRefEvent]
public record struct NCMedicalNextUpdateEvent(TimeSpan? Delay);

[ByRefEvent]
public readonly record struct NCPhysiologyChangedEvent(
    NCPhysiologicalCondition PreviousCondition,
    NCPhysiologicalCondition Condition,
    NCConsciousnessState PreviousConsciousness,
    NCConsciousnessState Consciousness);

/// <summary>
/// Raised after every successful authoritative physiology evaluation, including evaluations whose coarse state
/// did not change. Derived systems use it to consume current pain/consciousness without rescheduling physiology.
/// </summary>
[ByRefEvent]
public readonly record struct NCPhysiologyEvaluatedEvent;
