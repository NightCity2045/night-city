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

/// <summary>A mission defender: neutral until provoked, then hostile together with its group.</summary>
[RegisterComponent]
public sealed partial class NCMissionGuardComponent : Component
{
    [ViewVariables]
    public int Mission;

    /// <summary>Players inside this range are noticed and warned.</summary>
    [DataField]
    public float WarnRange = 6f;

    /// <summary>Players still this close after <see cref="Patience"/> are attacked.</summary>
    [DataField]
    public float CloseRange = 2.5f;

    [DataField]
    public TimeSpan Patience = TimeSpan.FromSeconds(6);

    [DataField]
    public List<LocId> Warnings = new();

    [DataField]
    public List<LocId> Alarms = new();

    [ViewVariables]
    public Dictionary<EntityUid, TimeSpan> Warned = new();

    [ViewVariables]
    public HashSet<EntityUid> Hostile = new();
}
