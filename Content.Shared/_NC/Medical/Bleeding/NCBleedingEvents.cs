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
