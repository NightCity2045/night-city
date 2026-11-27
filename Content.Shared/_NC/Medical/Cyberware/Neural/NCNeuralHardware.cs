// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.Shared.GameStates;

namespace Content.Shared._NC.Medical.Cyberware.Neural;

// Interface (the removed META jack) keeps its slot so the numeric values of later kinds stay stable.
public enum NCNeuralKind : byte { Link, Interface, ChipSocket, Kerenzikov, Sandevistan }

[RegisterComponent]
public sealed partial class NCNeuralHardwareComponent : Component
{
    [DataField] public NCNeuralKind Kind;
    [DataField] public bool Enabled = true;
    public TimeSpan NextDodge;
}

[RegisterComponent]
public sealed partial class NCNeuralControllerComponent : Component
{
    [DataField] public bool FactoryProgram;
}

/// <summary>Public burst visual only; hardware diagnostics are disclosed to the wearer.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCNeuralVisualComponent : Component
{
    [DataField, AutoNetworkedField] public bool Burst;
}

public static class NCNeuralMath
{
    public static float DodgeChance(float efficiency) =>
        !float.IsFinite(efficiency) ? 0 : .15f * Math.Clamp(efficiency, 0, 1);
}

[Serializable, Robust.Shared.Serialization.NetSerializable]
public sealed class NCNeuralStatusEvent : EntityEventArgs
{
    public NetEntity Body;
    public float Heat;
    public int CooldownSeconds;
    public bool Active;
}
