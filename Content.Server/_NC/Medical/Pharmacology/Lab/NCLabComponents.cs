// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Lab;

/// <summary>A tool that takes a sample from a patient: a syringe for blood, a swab for a wound.</summary>
[RegisterComponent, Access(typeof(NCLabSystem))]
public sealed partial class NCLabSamplerComponent : Component
{
    [DataField]
    public NCLabSampleKind Kind;

    [DataField]
    public int Charges = 5;

    [DataField]
    public TimeSpan Duration = TimeSpan.FromSeconds(3);

    [DataField]
    public EntProtoId Output = "NCLabSampleVial";
}

/// <summary>What a sample actually holds. It never leaves the server; only machines read it.</summary>
[RegisterComponent, Access(typeof(NCLabSystem))]
public sealed partial class NCLabSampleDataComponent : Component
{
    [DataField]
    public string BloodGroup = string.Empty;

    [DataField]
    public List<NCLabSampleDrug> Drugs = [];

    [DataField]
    public float WoundInfection;

    [DataField]
    public ProtoId<NCPathogenStrainPrototype>? Strain;
}

/// <summary>One compound found in a sample, with its measured concentration.</summary>
[DataDefinition]
public sealed partial class NCLabSampleDrug
{
    [DataField(required: true)]
    public ProtoId<NCDrugProfilePrototype> Drug;

    [DataField]
    public float Concentration;
}

public enum NCLabMethod : byte
{
    /// <summary>Cultures a sample for infection and sensitivity, and tests a compound against living tissue.</summary>
    Culture,

    /// <summary>Reads the blood chemistry of a sample and studies its compounds in a living organism's response.</summary>
    Metabolic,
}

[RegisterComponent, Access(typeof(NCLabSystem))]
public sealed partial class NCLabAnalyzerComponent : Component
{
    [DataField]
    public NCLabMethod Method;

    [DataField]
    public string Slot = "sample";

    [DataField]
    public TimeSpan Duration = TimeSpan.FromSeconds(8);

    /// <summary>Infection below this is not seen in a blood culture; a swab sees much smaller amounts.</summary>
    [DataField]
    public float BloodCultureThreshold = 0.5f;

    [DataField]
    public float SwabThreshold = 0.05f;
}
