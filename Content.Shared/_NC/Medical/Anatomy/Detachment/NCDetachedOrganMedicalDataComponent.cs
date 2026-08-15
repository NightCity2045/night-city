// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Anatomy.Detachment;

/// <summary>
/// Persistence-safe medical payload carried by the root of a detached live organ subtree.
/// The payload contains stable identifiers and prototype IDs only; the organ relation graph remains
/// exclusively owned by SS14's ParentOrgan/ChildOrgan components.
/// </summary>
[RegisterComponent]
public sealed partial class NCDetachedOrganMedicalDataComponent : Component
{
    /// <summary>
    /// Source anatomy family used for deterministic graft compatibility without retaining the donor body.
    /// </summary>
    [DataField]
    public ProtoId<NCAnatomyProfilePrototype> SourceBodyProfile;

    /// <summary>
    /// The category at the severed boundary edge. It validates reattachment without copying the subtree graph.
    /// </summary>
    [DataField]
    public ProtoId<OrganCategoryPrototype> RequiredParentCategory;

    [DataField]
    public ProtoId<NCWoundSystemProfilePrototype> WoundProfile;

    [DataField]
    public ProtoId<NCTreatmentProfilePrototype> TreatmentProfile;

    [DataField]
    public Dictionary<Guid, NCWoundRecord> Wounds = new();

    [DataField]
    public List<NCScarRecord> Scars = [];

    [DataField]
    public Dictionary<ProtoId<NCTargetZonePrototype>, float> OverflowTraumaByZone = new();

    [DataField]
    public float OverflowPain;

    [DataField]
    public float OverflowExternalBleedRate;

    [DataField]
    public float OverflowInternalBleedRate;

    [DataField]
    public Dictionary<ProtoId<OrganCategoryPrototype>, float> OverflowInternalBleedRatesByRegion = new();

    [DataField]
    public List<NCBleedingSourceRecord> BleedingSources = [];

    [DataField]
    public Dictionary<Guid, NCWoundTreatmentRecord> WoundTreatments = new();

    [DataField]
    public Dictionary<ProtoId<NCTargetZonePrototype>, NCTourniquetRecord> Tourniquets = new();
}
