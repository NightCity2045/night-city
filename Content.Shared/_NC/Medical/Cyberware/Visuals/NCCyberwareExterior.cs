// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Cyberware.Visuals;

/// <summary>Physical cosmetic finish. Private: intact synthetic skin must disclose no extra hardware state.</summary>
[RegisterComponent]
public sealed partial class NCCyberwareExteriorComponent : Component
{
    [DataField] public ProtoId<NCCyberwareFinishPrototype> Finish = "NCFinishChrome";
    [DataField] public bool Ruptured;
    [DataField] public Color? SkinColor;
    [ViewVariables] public float? ObservedIntegrity;
    public bool ProjectionInitialized;
    public ProtoId<NCCyberwareFinishPrototype> ProjectedFinish;
    public bool ProjectedRuptured;
}

[RegisterComponent]
public sealed partial class NCCyberwareExteriorBodyComponent : Component;

[RegisterComponent]
public sealed partial class NCCyberwareFinishApplicatorComponent : Component
{
    [DataField(required: true)] public ProtoId<NCCyberwareFinishPrototype> Finish;
    [ViewVariables] public bool Consumed;
}

[Prototype("ncCyberwareFinish")]
public sealed partial class NCCyberwareFinishPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public LocId Name { get; private set; }
    [DataField] public bool SyntheticSkin { get; private set; }
    [DataField] public Color Color { get; private set; } = Color.White;
}

[DataDefinition]
public sealed partial class NCCyberwareExteriorSnapshot
{
    [DataField(required: true)] public ProtoId<NCCyberwareFinishPrototype> Finish;
    [DataField] public bool Ruptured;
    [DataField] public Color SkinColor = Color.White;
}

/// <summary>A read-only presentation query. It never changes anatomical or functional ownership.</summary>
[ByRefEvent]
public struct NCGetOrganVisualAppearanceEvent(PrototypeLayerData data)
{
    public PrototypeLayerData Data = data;
}
