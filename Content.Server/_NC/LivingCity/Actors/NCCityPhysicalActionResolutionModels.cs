// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.LivingCity.Actors;

public enum NCCityPhysicalActionKind : byte
{
    Activate,
    Interaction,
    Combat,
    Medical,
    Police,
}

public enum NCCityPhysicalActionOutcome : byte
{
    Succeeded,
    Failed,
    Unavailable,
    Unhandled,
}

public readonly record struct NCCityPhysicalActionResult(
    NCCityPhysicalActionKind Kind,
    NCCityPhysicalActionOutcome Outcome,
    string? Failure);

/// <summary>
/// Typed extension point for existing gameplay systems. Subscribers resolve the actual ECS action and report its real
/// outcome; no skills, dice or legacy RED formula are part of this contract.
/// </summary>
[ByRefEvent]
public record struct NCCityPhysicalActionAttemptEvent(
    EntityUid Actor,
    EntityUid Target,
    NCCityPhysicalActionKind Kind,
    bool Handled,
    NCCityPhysicalActionOutcome Outcome,
    string? Failure);

public readonly record struct NCCityPhysicalActionResolutionCounters(
    long Attempts,
    long Succeeded,
    long Failed,
    long Unavailable,
    long Unhandled);
