// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Pharmacology.Batch;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// A production station for known formulas. Its quality values describe the equipment: a street lab and a
/// clinical line share the same code and differ only in these numbers.
/// </summary>
[RegisterComponent, Access(typeof(NCPharmaSynthesisSystem))]
public sealed partial class NCPharmaSynthesizerComponent : Component
{
    [DataField]
    public string SourceSlot = "source";

    /// <summary>Sterility of the equipment, before the production mode scales it.</summary>
    [DataField]
    public float EquipmentSterility = 0.9f;

    /// <summary>How evenly the equipment mixes a batch.</summary>
    [DataField]
    public float EquipmentMixing = 0.9f;

    /// <summary>Systematic concentration error of the equipment. A poorly calibrated line drifts off target.</summary>
    [DataField]
    public float CalibrationError;

    [DataField]
    public EntProtoId OutputContainer = "NCPharmaAutoinjector";

    /// <summary>Doses per physical pack; a large run is split into several packs.</summary>
    [DataField]
    public int DosesPerPack = 5;

    [DataField]
    public int MaximumUnitsPerRun = 40;

    [DataField]
    public int MaximumPacksPerRun = 12;
}

/// <summary>Present only while a run is in progress, so an idle station costs no update work.</summary>
[RegisterComponent, UnsavedComponent, Access(typeof(NCPharmaSynthesisSystem))]
public sealed partial class NCPharmaSynthesisRunComponent : Component
{
    public ProtoId<NCPharmaFormulaPrototype> Formula;
    public ProtoId<NCPharmaProductionProfilePrototype> Profile;
    public TimeSpan StartedAt;
    public TimeSpan FinishAt;
    public NCPharmaProductionInputs Inputs;
    public string Producer = string.Empty;
}
