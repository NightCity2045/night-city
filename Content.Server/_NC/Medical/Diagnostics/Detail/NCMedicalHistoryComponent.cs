// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Diagnostics.Detail;
using Content.Shared._NC.Medical.Emergency;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Diagnostics.Detail;

/// <summary>
/// Round-local patient timeline. It is intentionally unsaved and never enters the character database.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class NCMedicalHistoryComponent : Component
{
    [DataField]
    public int Capacity = 128;

    [ViewVariables]
    public List<NCMedicalHistoryRecord> Records = [];
}

/// <summary>
/// Server-only semantic record. It contains no actor identity, EntityUid, roll or hidden forensic payload.
/// </summary>
public sealed class NCMedicalHistoryRecord
{
    public TimeSpan Timestamp;
    public NCMedicalHistoryKind Kind;
    public ProtoId<NCTargetZonePrototype>? Zone;
    public NCWoundSeverity? WoundSeverity;
    public NCWoundType? WoundType;
    public NCTreatmentKind? Treatment;
    public NCEmergencyCareKind? EmergencyCare;
    public ProtoId<NCSurgeryProcedurePrototype>? Procedure;
    public ProtoId<NCSurgeryStepPrototype>? SurgeryStep;
    public NCSurgeryStepOutcome? SurgeryOutcome;
    public ProtoId<NCDrugProfilePrototype>? Drug;
    public ProtoId<OrganCategoryPrototype>? AutopsyRegion;
    public byte? AutopsyWoundCount;
    public List<NCWoundType>? AutopsyWoundTypes;
}
