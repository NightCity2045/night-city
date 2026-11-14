// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Pharmacology.Batch;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// A thermoreactor for developing experimental formulas by hand. The equipment values are the vessel's own;
/// everything else about a run comes from the reaction profile and from what the operator does.
/// </summary>
[RegisterComponent, Access(typeof(NCPharmaReactorSystem))]
public sealed partial class NCPharmaReactorComponent : Component
{
    [DataField]
    public string SourceSlot = "source";

    [DataField]
    public float EquipmentSterility = 0.85f;

    [DataField]
    public float EquipmentMixing = 0.85f;

    [DataField]
    public EntProtoId OutputContainer = "NCPharmaAutoinjector";

    [DataField]
    public int DosesPerPack = 3;

    [DataField]
    public int MaximumPacksPerRun = 6;

    /// <summary>Reagent units of fumes released at level II, before ventilation reduces them.</summary>
    [DataField]
    public float FumesPerRelease = 30f;
}

/// <summary>Present only while a run is in progress, so an idle reactor costs no update work.</summary>
[RegisterComponent, UnsavedComponent, Access(typeof(NCPharmaReactorSystem))]
public sealed partial class NCPharmaReactorRunComponent : Component
{
    public string Formula = string.Empty;
    public ProtoId<NCPharmaReactionProfilePrototype> Reaction;
    public NCReactorState State = new();
    public NCReactorControls Controls;
    public bool Ventilation;
    public int CatalystLeft;
    public float SourcePurity;
    public float SourceContaminants;
    public int Units;
    public string Operator = string.Empty;
    public TimeSpan NextStep;
    public bool Warned;
    public bool Released;
}
