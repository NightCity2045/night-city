// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Physiology;

/// <summary>
/// Authoritative circulation state. Upstream bloodstream remains a reagent container, not the source of
/// truth for blood volume or death.
/// </summary>
[RegisterComponent]
public sealed partial class NCCirculationComponent : Component
{
    [DataField]
    public ProtoId<NCPhysiologyProfilePrototype> Profile;

    [DataField]
    // This is the initialized state of the circulation model, distinct from the ECS component lifecycle.
    public new bool Initialized;

    [DataField]
    public float BloodVolume;

    [DataField]
    public float EffectiveCirculatingVolume;

    [DataField]
    public float CardiacOutput = 1f;

    [DataField]
    public float Perfusion = 1f;

    [DataField]
    public NCBloodPressureState BloodPressureState = NCBloodPressureState.Normal;

    [DataField]
    public NCHeartRhythm HeartRhythm = NCHeartRhythm.Normal;

    [DataField]
    public string TransfusionCompatibility = string.Empty;

    [ViewVariables]
    public TimeSpan LastEvaluation;
}

public enum NCBloodPressureState : byte
{
    Normal,
    CompensatedLow,
    Hypotensive,
    Critical,
    Absent,
}

public enum NCHeartRhythm : byte
{
    Normal,
    Tachycardia,
    Bradycardia,
    Unstable,
    ShockableArrest,
    NonShockableArrest,
}
