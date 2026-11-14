// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Pharmacology.Batch;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Lab;

/// <summary>Raw material with its own origin and quality. Extraction turns it into usable source material.</summary>
[RegisterComponent]
public sealed partial class NCPharmaRawMaterialComponent : Component
{
    /// <summary>Units of raw material.</summary>
    [DataField]
    public float Units = 30f;

    /// <summary>Share of the extract that is the wanted compound before extraction losses.</summary>
    [DataField]
    public float Purity = 0.7f;

    /// <summary>Share of the material that is a toxic contaminant. Hidden from the player.</summary>
    [DataField]
    public float Contaminants;

    /// <summary>Where it came from, as a localization id shown when examined.</summary>
    [DataField]
    public LocId Origin = "nc-pharma-origin-unknown";

    /// <summary>The source canister the extraction yields.</summary>
    [DataField]
    public EntProtoId Output = "NCPharmaSourceCanisterStreet";

    /// <summary>
    /// If set, the material holds an unidentified compound drawn from this pool. Extraction isolates it as an
    /// experimental formula instead of producing generic source material.
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
    public TimeSpan Duration = TimeSpan.FromSeconds(15);

    /// <summary>Share of the raw units that ends up in the extract.</summary>
    [DataField]
    public float Efficiency = 0.7f;

    /// <summary>Share of the raw purity that survives the extraction.</summary>
    [DataField]
    public float PurityRetention = 0.9f;

    [DataField]
    public float WastePerRun = 2f;
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
    public TimeSpan Duration = TimeSpan.FromSeconds(25);

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
    public TimeSpan Duration = TimeSpan.FromSeconds(10);

    [DataField]
    public int DosesPerPack = 1;
}

/// <summary>Hazardous waste a machine accumulates. A full tank stops the machine until it is emptied.</summary>
[RegisterComponent, Access(typeof(NCPharmaWasteSystem))]
public sealed partial class NCPharmaWasteTankComponent : Component
{
    [DataField]
    public float Level;

    [DataField]
    public float Capacity = 10f;
}

/// <summary>A canister for carrying waste to disposal. A filled canister leaks if it is left lying around.</summary>
[RegisterComponent, Access(typeof(NCPharmaWasteSystem))]
public sealed partial class NCPharmaWasteCanisterComponent : Component
{
    [DataField]
    public bool Filled;

    /// <summary>What an empty canister turns into when it is filled.</summary>
    [DataField]
    public EntProtoId? FilledPrototype;

    /// <summary>What a filled canister turns into when it is emptied.</summary>
    [DataField]
    public EntProtoId? EmptyPrototype;

    [DataField]
    public TimeSpan LeakAfter = TimeSpan.FromMinutes(15);

    /// <summary>How long the filled canister has been lying around. Persistent, so a restart does not reset it.</summary>
    [DataField]
    public TimeSpan Age;
}

/// <summary>An incinerator for waste canisters. It needs power.</summary>
[RegisterComponent]
public sealed partial class NCPharmaWasteBinComponent : Component;
