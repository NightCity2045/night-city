// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;

namespace Content.Shared._NC.Medical.Bleeding;

/// <summary>
/// Authoritative fluid state stored on a live cavity-region entity. The component deliberately has no
/// update logic: the body's bounded bleeding pass owns accumulation and future surgical drainage.
/// </summary>
[RegisterComponent]
public sealed partial class NCCavityFluidComponent : Component
{
    [DataField(required: true)]
    public NCBodyCavity Cavity;

    /// <summary>
    /// Blood currently outside the circulation and retained in this anatomical cavity, in millilitres.
    /// </summary>
    [DataField]
    public float BloodVolume;

    /// <summary>
    /// Bounded storage used by persistence validation and pathological overflow handling.
    /// </summary>
    [DataField(required: true)]
    public float MaximumBloodVolume;

    /// <summary>
    /// Volume at which normalized cavity pressure reaches one.
    /// </summary>
    [DataField(required: true)]
    public float PressureFullScaleVolume;

    /// <summary>
    /// Volume at which normalized organ compression reaches one.
    /// </summary>
    [DataField(required: true)]
    public float CompressionFullScaleVolume;

    /// <summary>
    /// Active surgical drainage in millilitres per second. Zero means that no drain is operating.
    /// </summary>
    [DataField]
    public float DrainRate;

    [ViewVariables]
    public float Pressure;

    [ViewVariables]
    public float OrganCompression;
}

