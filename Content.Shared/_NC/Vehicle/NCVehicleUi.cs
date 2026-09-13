// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Vehicle;

[Serializable, NetSerializable]
public enum NCVehicleUiKey : byte { Driver, Service, Ownership, Workbench }

[Serializable, NetSerializable]
public enum NCVehicleSelector : byte { Park, Reverse, Neutral, Drive, Low }

[Serializable, NetSerializable]
public enum NCVehicleShiftStatus : byte { None, ClutchRequired, WaitingForClutch }

[Serializable, NetSerializable]
public sealed class NCVehicleInputMessage(uint authority, uint sequence, float steering,
    bool gas, bool brake, bool clutch, bool handbrake, bool steeringHeld = false) : BoundUserInterfaceMessage
{
    public readonly uint Authority = authority;
    public readonly uint Sequence = sequence;
    public readonly float Steering = steering;
    public readonly bool Gas = gas;
    public readonly bool Brake = brake;
    public readonly bool Clutch = clutch;
    public readonly bool Handbrake = handbrake;
    public readonly bool SteeringHeld = steeringHeld;
}

[Serializable, NetSerializable]
public enum NCVehicleDriverAction : byte { Ignition, GearUp, GearDown, Reverse, Neutral, Exit, GearLeft, GearRight, Lights, Horn, Wash }

[Serializable, NetSerializable]
public sealed class NCVehicleDriverActionMessage(uint authority, NCVehicleDriverAction action) : BoundUserInterfaceMessage
{
    public readonly uint Authority = authority;
    public readonly NCVehicleDriverAction Action = action;
}

/// <summary>Separate authority-only message: each component/event pair has a single ECS subscriber.</summary>
[Serializable, NetSerializable]
public sealed class NCVehicleExitMessage(uint authority) : BoundUserInterfaceMessage
{
    public readonly uint Authority = authority;
}

[Serializable, NetSerializable]
public sealed class NCVehicleBeltMessage(uint authority) : BoundUserInterfaceMessage
{
    public readonly uint Authority = authority;
}

/// <summary>Shared lifecycle notification lets the authoritative seat owner evacuate before removal.</summary>
[ByRefEvent]
public readonly record struct NCVehicleDriveShutdownEvent;

[ByRefEvent]
public readonly record struct NCVehicleFuelConsumedEvent(float Litres);

[ByRefEvent]
public readonly record struct NCVehicleOverturnedEvent;

[Serializable, NetSerializable]
public sealed class NCVehicleInfrastructureMessage(uint authority, bool travel) : BoundUserInterfaceMessage
{
    public readonly uint Authority = authority;
    public readonly bool Travel = travel;
}
