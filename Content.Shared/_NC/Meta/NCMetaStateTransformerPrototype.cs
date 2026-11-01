// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Meta;

/// <summary>Only game content can register schema transitions; META source/markup cannot supply these steps.</summary>
[Prototype("ncMetaStateTransformer")]
public sealed partial class NCMetaStateTransformerPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField(required: true)] public string Package = string.Empty;
    /// <summary>An empty source means this same package. Cross-package firmware replacement requires an explicit source.</summary>
    [DataField] public string FromPackage = string.Empty;
    [DataField(required: true)] public int FromVersion;
    [DataField(required: true)] public int ToVersion;
    [DataField(required: true)] public List<NCMetaStateTransformStep> Steps = [];
}

[DataDefinition]
public sealed partial class NCMetaStateTransformStep
{
    [DataField(required: true)] public NCMetaStateTransformKind Kind;
    [DataField(required: true)] public string Path = string.Empty;
    [DataField] public string Target = string.Empty;
    [DataField] public string Text = string.Empty;
}

// The shared prototype carries configuration only; executing transformations belongs to the server.
public enum NCMetaStateTransformKind : byte { Rename, Delete, SetDefault }
