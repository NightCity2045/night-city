// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Meta.Demo;

/// <summary>Isolated physical acceptance content, never a placement instruction for production maps.</summary>
[Prototype("ncMetaDemoScene")]
public sealed partial class NCMetaDemoScenePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField(required: true)] public LocId Name;
    [DataField(required: true)] public List<NCMetaDemoObject> Objects = [];
    [DataField] public List<NCMetaDemoCable> Cables = [];
    [DataField] public string[] Templates = [];
}

[DataDefinition]
public sealed partial class NCMetaDemoObject
{
    [DataField(required: true)] public string Key = string.Empty;
    [DataField(required: true)] public EntProtoId Prototype;
    [DataField] public Vector2 Offset;
}

[DataDefinition]
public sealed partial class NCMetaDemoCable
{
    [DataField(required: true)] public string From = string.Empty;
    [DataField(required: true)] public string To = string.Empty;
}
