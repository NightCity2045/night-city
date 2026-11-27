// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Fixers;

/// <summary>An NPC fixer players talk to. It is a fixed object: no damage, no pulling.</summary>
[RegisterComponent]
public sealed partial class NCFixerComponent : Component
{
    [DataField(required: true)]
    public ProtoId<NCFixerPrototype> Fixer;
}

/// <summary>A dead drop: goods put inside are checked against missions, and cash payments appear in it.</summary>
[RegisterComponent]
public sealed partial class NCFixerDeadDropComponent : Component
{
    /// <summary>The fixer whose drop this is; null for a temporary mission drop.</summary>
    [DataField]
    public ProtoId<NCFixerPrototype>? Fixer;
}

/// <summary>A mapping marker for fixer missions. Invisible in game.</summary>
[RegisterComponent]
public sealed partial class NCMissionMarkerComponent : Component
{
    [DataField]
    public NCMissionMarkerKind Kind = NCMissionMarkerKind.Objective;

    [DataField(required: true)]
    public ProtoId<NCMissionOwnerPrototype> Owner;
}

/// <summary>Marks an entity spawned for a fixer mission so it can be recognised and cleaned up.</summary>
[RegisterComponent]
public sealed partial class NCMissionEntityComponent : Component
{
    [ViewVariables]
    public int Mission;
}
