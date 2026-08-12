// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Anatomy;

/// <summary>
/// Ordered, discrete tissue layers used by trauma and surgery. These values are gameplay coefficients,
/// not physical millimetres, joules or a second collision simulation.
/// </summary>
[Prototype("ncTissueProfile")]
public sealed partial class NCTissueProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public List<NCTissueLayerDefinition> Layers { get; private set; } = [];
}

[DataDefinition]
public sealed partial class NCTissueLayerDefinition
{
    [DataField(required: true)]
    public NCTissueLayerKind Kind { get; private set; }

    [DataField]
    public float IntegrityCapacity { get; private set; } = 10f;

    [DataField]
    public float BleedingMultiplier { get; private set; } = 1f;

    [DataField]
    public float PainMultiplier { get; private set; } = 1f;

    [DataField]
    public NCSurgicalAccessDepth AccessDepth { get; private set; }
}

public enum NCTissueLayerKind : byte
{
    Surface,
    SoftTissue,
    Vascular,
    Structural,
    Cavity,
    InternalOrgan,
    Mechanical,
}
