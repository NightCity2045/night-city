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

    /// <summary>Doses administered but still being absorbed; they continue after a restart.</summary>
    [DataField]
    public List<NCPendingDoseSnapshot> PendingDoses = [];

    [DataField]
    public float LocalAnalgesia;

    [DataField]
    public float LocalHemostasis;

    [DataField]
    public TimeSpan LocalRemaining;

    [DataField]
    public int StrainEpisode;
}

/// <summary>One dose that is still arriving: what remains and how fast.</summary>
[DataDefinition]
public sealed partial class NCPendingDoseSnapshot
{
    [DataField(required: true)]
    public ProtoId<NCDrugProfilePrototype> Drug;

    [DataField]
    public float Remaining;

    [DataField]
    public float RatePerSecond;

    [DataField]
    public float ClearanceFactor = 1f;
}

/// <summary>Raised on a body when a new infection starts after it had none, so it may carry a new strain.</summary>
public readonly record struct NCInfectionEpisodeStartedEvent;

/// <summary>Raised on a body while its pharmacology is captured so the absorption depot can add its doses.</summary>
[ByRefEvent]
public readonly record struct NCPharmacologyDepotCaptureEvent(List<NCPendingDoseSnapshot> Doses);

/// <summary>Raised on a body after its pharmacology is restored so the absorption depot can resume the doses.</summary>
[ByRefEvent]
public readonly record struct NCPharmacologyDepotRestoreEvent(IReadOnlyList<NCPendingDoseSnapshot> Doses);

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

    [DataField]
    public float Comedown;
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

    [DataField]
    public float ClearanceFactor = 1f;
}
