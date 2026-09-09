// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.DoAfter;
using Robust.Shared.Serialization;
using Content.Shared._NC.Vehicle.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Vehicle;

[Serializable, NetSerializable]
public enum NCVehicleServiceAction : byte { Inspect, Disconnect, Remove, Install, Connect, Hood, Lift, Right, Fasten, Unfasten }

[Serializable, NetSerializable]
public sealed class NCVehicleServiceMessage(string slot, NCVehicleServiceAction action) : BoundUserInterfaceMessage
{
    public readonly string Slot = slot;
    public readonly NCVehicleServiceAction Action = action;
}

[Serializable, NetSerializable]
public sealed partial class NCVehicleServiceDoAfterEvent : DoAfterEvent
{
    [DataField] public string Slot = string.Empty;
    [DataField] public NCVehicleServiceAction Action;
    [DataField] public uint Revision;
    public override DoAfterEvent Clone() => new NCVehicleServiceDoAfterEvent { Slot = Slot, Action = Action, Revision = Revision };
}

[Serializable, NetSerializable]
public sealed partial class NCVehicleRepairDoAfterEvent : SimpleDoAfterEvent;

[Serializable, NetSerializable]
public sealed partial class NCVehicleBodyRepairDoAfterEvent : DoAfterEvent
{
    [DataField] public string Zone = string.Empty;
    public override DoAfterEvent Clone() => new NCVehicleBodyRepairDoAfterEvent { Zone = Zone };
}

[Serializable, NetSerializable]
public sealed partial class NCVehicleRightDoAfterEvent : SimpleDoAfterEvent;

[Serializable, NetSerializable]
public enum NCVehicleRecoveryAction : byte { Load, Secure, Release, Unload }

[Serializable, NetSerializable]
public sealed partial class NCVehicleRecoveryDoAfterEvent : DoAfterEvent
{
    [DataField] public NCVehicleRecoveryAction Action;
    [DataField] public NetEntity Cargo;
    public override DoAfterEvent Clone() => new NCVehicleRecoveryDoAfterEvent { Action = Action, Cargo = Cargo };
}

[Serializable, NetSerializable]
public readonly record struct NCVehicleServiceRow(string Slot, string Name, bool Installed, bool Connected, float? Condition, bool ExactCondition = true, bool Fastened = true);

[Serializable, NetSerializable]
public sealed class NCVehicleServiceState(NCVehicleServiceRow[] rows, bool hoodOpen, bool lifted, NCVehicleCalibrationChoice[]? calibrations = null) : BoundUserInterfaceState
{
    public readonly NCVehicleServiceRow[] Rows = rows;
    public readonly bool HoodOpen = hoodOpen;
    public readonly bool Lifted = lifted;
    public readonly NCVehicleCalibrationChoice[] Calibrations = calibrations ?? [];
}

[Serializable, NetSerializable]
public readonly record struct NCVehicleCalibrationChoice(ProtoId<NCVehicleTuningPrototype> Prototype, string Name);

[Serializable, NetSerializable]
public sealed class NCVehicleTuneMessage(ProtoId<NCVehicleTuningPrototype> calibration) : BoundUserInterfaceMessage
{
    public readonly ProtoId<NCVehicleTuningPrototype> Calibration = calibration;
}

[Serializable, NetSerializable]
public sealed partial class NCVehicleTuneDoAfterEvent : DoAfterEvent
{
    [DataField] public ProtoId<NCVehicleTuningPrototype> Calibration;
    [DataField] public uint Revision;
    public override DoAfterEvent Clone() => new NCVehicleTuneDoAfterEvent { Calibration = Calibration, Revision = Revision };
}
