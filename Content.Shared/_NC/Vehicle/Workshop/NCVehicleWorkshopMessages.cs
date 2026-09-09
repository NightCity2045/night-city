// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared.DoAfter;
using Content.Shared._NC.Vehicle.Prototypes;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Vehicle.Workshop;

[Serializable, NetSerializable]
public enum NCVehicleWorkshopAction : byte { Work, Inspect, Remove, Install, Fill, Drain }

[Serializable, NetSerializable]
public sealed class NCVehicleWorkshopRequest(string view, Vector2 position, string node, string point,
    uint revision, uint sequence, NCVehicleWorkshopAction action) : BoundUserInterfaceMessage
{
    public readonly string View = view;
    public readonly Vector2 Position = position;
    public readonly string Node = node;
    public readonly string Point = point;
    public readonly uint Revision = revision;
    public readonly uint Sequence = sequence;
    public readonly NCVehicleWorkshopAction Action = action;
}

[Serializable, NetSerializable]
public sealed class NCVehicleWorkshopRefresh : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class NCVehicleWorkshopTorqueRequest(float torque) : BoundUserInterfaceMessage
{
    public readonly float Torque = torque;
}

[Serializable, NetSerializable]
public readonly record struct NCVehicleWorkshopPartState(string Slot, bool Installed, uint Revision,
    Dictionary<string, bool> Points, Dictionary<string, float> PointTorque, float? Condition, bool Exact, bool Stale, bool Busy);

[Serializable, NetSerializable]
public readonly record struct NCVehicleWorkshopFluidState(NCVehicleFluidCircuit Circuit, float Fraction,
    float Purity, float Volume, float Capacity);

/// <summary>Private observer data: diagnostic measurements are never shared via the car's public BUI state.</summary>
[Serializable, NetSerializable]
public sealed class NCVehicleWorkshopSnapshot(NetEntity vehicle, ProtoId<NCVehicleWorkshopPrototype> profile,
    NCVehicleWorkshopPartState[] parts, bool hood, bool lifted, LocId feedback, uint sequence,
    bool pending, EntProtoId? tool, float? toolTorque, float? minimumTorque, float? maximumTorque, float? torqueStep,
    NCVehicleCalibrationChoice[] calibrations,
    NCVehicleWorkshopFluidState[] fluids) : EntityEventArgs
{
    public readonly NetEntity Vehicle = vehicle;
    public readonly ProtoId<NCVehicleWorkshopPrototype> Profile = profile;
    public readonly NCVehicleWorkshopPartState[] Parts = parts;
    public readonly bool Hood = hood;
    public readonly bool Lifted = lifted;
    public readonly LocId Feedback = feedback;
    public readonly uint Sequence = sequence;
    public readonly bool Pending = pending;
    public readonly EntProtoId? Tool = tool;
    public readonly float? ToolTorque = toolTorque;
    public readonly float? MinimumTorque = minimumTorque;
    public readonly float? MaximumTorque = maximumTorque;
    public readonly float? TorqueStep = torqueStep;
    public readonly NCVehicleCalibrationChoice[] Calibrations = calibrations;
    public readonly NCVehicleWorkshopFluidState[] Fluids = fluids;
}

[Serializable, NetSerializable]
public sealed partial class NCVehicleWorkshopDoAfter : DoAfterEvent
{
    [DataField] public string View = string.Empty;
    [DataField] public string Node = string.Empty;
    [DataField] public string Point = string.Empty;
    [DataField] public Vector2 Position;
    [DataField] public uint Revision;
    [DataField] public uint Sequence;
    [DataField] public NCVehicleWorkshopAction Action;
    public override DoAfterEvent Clone() => (DoAfterEvent) MemberwiseClone();
}
