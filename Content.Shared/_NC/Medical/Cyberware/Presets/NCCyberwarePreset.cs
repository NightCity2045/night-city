// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Cyberware.Presets;

/// <summary>
/// Cyberware a role comes with. Each character gets a preset once, the first time it enters a role that carries it;
/// after that the implants are ordinary hardware that can be removed, broken or sold.
/// </summary>
[Prototype("ncCyberwarePreset")]
public sealed partial class NCCyberwarePresetPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>Cyberware items to fit. Whatever they depend on (power, neural link, support grafts) comes with them.</summary>
    [DataField(required: true)]
    public List<EntProtoId> Items = new();
}

/// <summary>Added to a job's mob at spawn: fit this preset once the character's medical state has loaded.</summary>
[RegisterComponent]
public sealed partial class NCCyberwarePresetComponent : Component
{
    [DataField(required: true)]
    public ProtoId<NCCyberwarePresetPrototype> Preset;
}
