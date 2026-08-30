// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;

namespace Content.Shared._NC.Medical.Bleeding;

[ByRefEvent]
public readonly record struct NCBleedingChangedEvent(
    float BloodLost,
    float ExternalBloodLost,
    float InternalBloodLost,
    float ExternalRate,
    float InternalRate,
    float ArterialRate);

/// <summary>
/// Raised on a cavity-region entity after authoritative internal blood accumulation changes it.
/// </summary>
[ByRefEvent]
public readonly record struct NCCavityFluidChangedEvent(
    NCBodyCavity Cavity,
    float PreviousBloodVolume,
    float BloodVolume);

/// <summary>
/// Bounded aggregate consumed by physiology after inspecting the body's live cavity regions.
/// </summary>
public readonly record struct NCCavityPhysiologyEffects(
    float CardiacOutputMultiplier,
    float RespiratoryTransferMultiplier,
    float BrainFunctionMultiplier,
    float Pain,
    float Shock)
{
    public static readonly NCCavityPhysiologyEffects None = new(1f, 1f, 1f, 0f, 0f);
}
