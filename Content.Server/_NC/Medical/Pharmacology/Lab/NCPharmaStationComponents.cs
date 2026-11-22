// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Content.Shared.Chemistry.Reagent;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Lab;

/// <summary>
/// Raw material is a jug of liquid: the broth of a feedstock, diluted by impurity and spoiled by sludge. The extractor
/// reads that composition. This marks where a ready-made jug came from, and an unidentified seized sample.
/// </summary>
[RegisterComponent]
public sealed partial class NCPharmaRawMaterialComponent : Component
{
    /// <summary>Where it came from, as a localization id shown when examined.</summary>
    [DataField]
    public LocId Origin = "nc-pharma-origin-unknown";

    /// <summary>
    /// If set, the material holds an unidentified compound drawn from this pool. Extraction isolates it as an
    /// experimental formula instead of producing source material.
    /// </summary>
    [DataField]
    public string? UnknownPool;
}

[RegisterComponent, Access(typeof(NCPharmaStationSystem))]
public sealed partial class NCPharmaExtractorComponent : Component
{
    [DataField]
    public string Slot = "material";

    [DataField]
    public TimeSpan Duration = TimeSpan.FromSeconds(30);

    /// <summary>Share of the raw units that ends up in the extract.</summary>
    [DataField]
    public float Efficiency = 0.7f;

    /// <summary>Share of the raw purity that survives the extraction.</summary>
    [DataField]
    public float PurityRetention = 0.9f;

    [DataField]
    public float WastePerRun = 2f;

    // ---- brewed raw material ------------------------------------------------------------------------
    // A container whose solution holds the brewed broth is accepted instead of ready-made raw material.

    /// <summary>Less brewed broth than this is not worth running the extractor for.</summary>
    [DataField]
    public float MinimumBrew = 5f;

    /// <summary>Raw units per reagent unit of broth.</summary>
    [DataField]
    public float UnitsPerReagent = 0.6f;

    [DataField]
    public LocId BrewOrigin = "nc-pharma-origin-brewed";

    /// <summary>The empty canister the extract is poured into.</summary>
    [DataField]
    public EntProtoId BrewOutput = "NCPharmaSourceCanister";
}

/// <summary>A sterile cabinet or a formulation station: it treats a pack's whole batch.</summary>
[RegisterComponent, Access(typeof(NCPharmaStationSystem))]
public sealed partial class NCPharmaTreatmentStationComponent : Component
{
    [DataField]
    public string Slot = "pack";

    [DataField]
    public NCPharmaTreatment Treatment;

    [DataField]
    public TimeSpan Duration = TimeSpan.FromSeconds(60);

    /// <summary>Share of the gap to the quality ceiling this treatment closes.</summary>
    [DataField]
    public float GapClosed = 0.6f;

    [DataField]
    public float StabilityCost = 0.04f;

    [DataField]
    public float WastePerRun = 1f;
}

/// <summary>Repackages the doses of a pack into packs of a set size in the formula's own kind of pack.</summary>
[RegisterComponent, Access(typeof(NCPharmaStationSystem))]
public sealed partial class NCPharmaFillingStationComponent : Component
{
    [DataField]
    public string Slot = "pack";

    [DataField]
    public TimeSpan Duration = TimeSpan.FromSeconds(20);

    [DataField]
    public int DosesPerPack = 1;
}

/// <summary>
/// A machine that makes hazardous waste. The waste is poured, as a reagent, into the canister standing in its waste
/// slot; without a canister with room in it the machine will not run.
/// </summary>
[RegisterComponent, Access(typeof(NCPharmaWasteSystem))]
public sealed partial class NCPharmaWasteTankComponent : Component
{
    [DataField]
    public string Slot = "waste";

    [DataField]
    public ProtoId<ReagentPrototype> Reagent = "NCPharmaWaste";

    /// <summary>Units of waste reagent one unit of waste makes.</summary>
    [DataField]
    public float UnitsPerWaste = 1f;
}

/// <summary>
/// A canister for carrying waste to disposal. It is an ordinary reagent container; once it holds waste it leaks toxic
/// fumes if it is left lying around.
/// </summary>
[RegisterComponent, Access(typeof(NCPharmaWasteSystem))]
public sealed partial class NCPharmaWasteCanisterComponent : Component
{
    [DataField]
    public ProtoId<ReagentPrototype> Reagent = "NCPharmaWaste";

    [DataField]
    public TimeSpan LeakAfter = TimeSpan.FromMinutes(15);

    /// <summary>Units of waste a leak lets out.</summary>
    [DataField]
    public float LeakUnits = 5f;

    /// <summary>How long the canister has been lying around with waste in it. Persistent, so a restart does not reset it.</summary>
    [DataField]
    public TimeSpan Age;
}

/// <summary>An incinerator for waste canisters. It needs power.</summary>
[RegisterComponent]
public sealed partial class NCPharmaWasteBinComponent : Component
{
    /// <summary>The slot a canister goes into to be burned from the window.</summary>
    [DataField]
    public string Slot = "canister";
}
