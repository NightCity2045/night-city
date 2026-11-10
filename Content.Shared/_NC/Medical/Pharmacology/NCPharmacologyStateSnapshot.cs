// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Pharmacology;

/// <summary>
/// Semantic, bounded pharmacology state embedded in medical persistence. Exposure age is relative to the
/// capture boundary, so offline time never clears drugs or advances overdose damage.
/// </summary>
[DataDefinition]
public sealed partial class NCPharmacologyStateSnapshot
{
    [DataField(required: true)]
    public ProtoId<NCPharmacologyProfilePrototype> Profile;

    [DataField]
    public bool Initialized;

    [DataField]
    public float OverdoseBurden;

    [DataField]
    public List<NCActiveDrugExposureSnapshot> ActiveDrugs = [];

    [DataField]
    public List<NCDrugDependenceSnapshot> Dependence = [];
}

/// <summary>
/// One persisted dependence record. The time since the last dose is relative to the capture boundary, so time
/// spent offline neither starts withdrawal nor lets dependence fade.
/// </summary>
[DataDefinition]
public sealed partial class NCDrugDependenceSnapshot
{
    [DataField(required: true)]
    public ProtoId<NCDrugProfilePrototype> Profile;

    [DataField]
    public float Tolerance;

    [DataField]
    public float Dependence;

    [DataField]
    public float Craving;

    [DataField]
    public TimeSpan TimeSinceLastDose;

    [DataField]
    public NCWithdrawalStage Stage;
}

/// <summary>
/// One persisted measured exposure. It contains prototype identity and scalars only, never a reagent
/// solution, entity, session or action-deduplication key.
/// </summary>
[DataDefinition]
public sealed partial class NCActiveDrugExposureSnapshot
{
    [DataField(required: true)]
    public ProtoId<NCDrugProfilePrototype> Profile;

    [DataField]
    public float Concentration;

    [DataField]
    public float CumulativeDose;

    [DataField]
    public TimeSpan TimeSinceAdministration;
}
