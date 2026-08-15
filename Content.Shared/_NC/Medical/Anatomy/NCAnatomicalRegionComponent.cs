// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Anatomy;

/// <summary>
/// Data describing one materialized region in the existing SS14 organ graph.
/// Tissue layers remain prototype data instead of becoming additional entities.
/// </summary>
[RegisterComponent]
public sealed partial class NCAnatomicalRegionComponent : Component
{
    [DataField(required: true)]
    public ProtoId<NCTissueProfilePrototype> TissueProfile;

    [DataField]
    public NCVitalImportance VitalImportance = NCVitalImportance.Supporting;

    [DataField]
    public float MovementContribution;

    [DataField]
    public float GripContribution;

    [DataField]
    public NCSurgicalAccessDepth ClosedAccessDepth = NCSurgicalAccessDepth.Closed;

    [DataField]
    public HashSet<NCArmorLocation> ArmorLocations = [];
}

public enum NCVitalImportance : byte
{
    Supporting,
    Important,
    Vital,
}

public enum NCSurgicalAccessDepth : byte
{
    Closed,
    Incised,
    BleedersControlled,
    SoftTissueRetracted,
    CavityOpened,
    EncasedStructureOpened,
    OrganExposed,
    ProcedureInProgress,
    ReadyForClosure,
    ClosedPostOp,
}
