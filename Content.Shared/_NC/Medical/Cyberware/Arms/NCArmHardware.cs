// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared.DoAfter;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Cyberware.Arms;

[RegisterComponent]
public sealed partial class NCArmFrameComponent : Component
{
    [DataField] public float Volume = 4;
    [DataField] public bool Heavy;
    [DataField] public EntProtoId? DefaultHand;
    // Persist factory completion even if the hand is subsequently removed.
    [DataField] public bool HandInitialized;
    // Service access is deliberately closed on medical restore.
    [ViewVariables] public bool PanelOpen;
    [ViewVariables] public uint ServiceRevision;
}

[RegisterComponent]
public sealed partial class NCArmModuleComponent : Component
{
    [DataField(required: true)] public NCArmModuleKind Kind;
    [DataField] public float Volume = 1;
    [DataField] public bool RequiresHeavyFrame;
    [DataField] public bool RequiresQuickSocket;
    [DataField] public string VisualState = "tool";
    [DataField] public float DeploySeconds = .6f;
    [ViewVariables] public TimeSpan ReadyAt;
}

[RegisterComponent]
public sealed partial class NCArmDriveComponent : Component
{
    [DataField] public float Strength = 1.25f;
}

[RegisterComponent]
public sealed partial class NCArmServiceBodyComponent : Component;

[RegisterComponent]
public sealed partial class NCArmServiceToolComponent : Component;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class NCArmVisualComponent : Component
{
    [DataField, AutoNetworkedField] public string? Left;
    [DataField, AutoNetworkedField] public string? Right;
    [DataField, AutoNetworkedField] public NCArmPhase LeftPhase;
    [DataField, AutoNetworkedField] public NCArmPhase RightPhase;
    [DataField, AutoNetworkedField] public TimeSpan LeftUntil;
    [DataField, AutoNetworkedField] public TimeSpan RightUntil;
}

[Serializable, NetSerializable]
public enum NCArmModuleKind : byte { Impact, Claws, Blade, Mantis, Monowire, Pistol, Launcher, Tool, Medical, Storage, QuickSocket }
[Serializable, NetSerializable]
public enum NCArmPhase : byte { Folded, Deploy, Ready, Fire, Empty, Reload, Retract }
[Serializable, NetSerializable]
public enum NCArmServiceOperation : byte { Open, Close, Install, Remove, Calibrate, InsertPart, RemovePart }

[Serializable, NetSerializable]
public sealed partial class NCArmServiceDoAfterEvent : DoAfterEvent
{
    [DataField] public NetEntity Arm;
    [DataField] public NetEntity Module;
    [DataField] public NetEntity Tool;
    [DataField] public string Slot = "";
    [DataField] public uint Revision;
    [DataField] public uint AssemblyRevision;
    [DataField] public NCArmServiceOperation Operation;
    public override DoAfterEvent Clone() => (NCArmServiceDoAfterEvent) MemberwiseClone();
}

/// <summary>Shared volume is independent of the physical mounting bay.</summary>
public static class NCArmHardwareMath
{
    public static bool Fits(float capacity, float used, float added) =>
        float.IsFinite(capacity) && float.IsFinite(used) && float.IsFinite(added) &&
        capacity > 0 && used >= 0 && added >= 0 && used + added <= capacity;
}

[Serializable, NetSerializable]
public sealed class NCMonowireArcEvent(NetEntity source, NetCoordinates target) : EntityEventArgs
{
    public NetEntity Source = source;
    public NetCoordinates Target = target;
}

[DataDefinition]
public sealed partial class NCArmContentsSnapshot
{
    [DataField] public string Slot = "";
    [DataField] public string Yaml = "";
}
