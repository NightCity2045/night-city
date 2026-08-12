// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.EntityEffects;
using Content.Shared._NC.Medical.Cyberware;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Pharmacology;

/// <summary>
/// Upstream-metabolism bridge. The effect carries only a prototype and measured metabolized quantity; all
/// physiological behavior is resolved by the server-authoritative pharmacology system.
/// </summary>
public sealed partial class NCApplyMedicalDrug : EntityEffectBase<NCApplyMedicalDrug>
{
    [DataField(required: true)]
    public ProtoId<NCDrugProfilePrototype> Profile;

    [DataField]
    public float ConcentrationPerUnit = 1f;
}

/// <summary>
/// Metabolism bridge for a cyberware service agent circulating through an authoritative medical body.
/// The cyberware system performs bounded target selection and applies only real module deficits.
/// </summary>
public sealed partial class NCApplyCyberwareServiceAgent : EntityEffectBase<NCApplyCyberwareServiceAgent>
{
    [DataField(required: true)]
    public ProtoId<NCCyberwareRepairProfilePrototype> ServiceProfile;

    [DataField]
    public float UnitsPerReagent = 1f;
}
