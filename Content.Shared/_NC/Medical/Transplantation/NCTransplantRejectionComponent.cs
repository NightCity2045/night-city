// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Persistence;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Transplantation;

/// <summary>
/// Semantic rejection state on the root of one attached organic graft. Runtime body or organ identities are
/// never stored here; provenance is copied from stable persistence identities.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class NCTransplantRejectionComponent : Component
{
    [DataField]
    public ProtoId<NCTransplantRejectionProfilePrototype> Profile;

    [DataField("initialized")]
    public bool StateInitialized;

    [DataField]
    public NCMedicalStableOwnerKind DonorKind;

    [DataField]
    public string DonorKey = string.Empty;

    [DataField]
    public NCMedicalStableOwnerKind RecipientKind;

    [DataField]
    public string RecipientKey = string.Empty;

    [DataField]
    public float Burden;

    [DataField]
    public NCTransplantRejectionStage Stage;
}

public enum NCTransplantRejectionStage : byte
{
    None,
    Early,
    Active,
    Severe,
    Critical,
}
