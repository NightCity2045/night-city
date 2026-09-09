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
public enum NCVehicleWorkbenchAction : byte
{
    Inspect,
    Repair,
    Remove,
    Install,
}

/// <summary>The client reports the authored contact point; the server repeats every geometry and state check.</summary>
[Serializable, NetSerializable]
public sealed class NCVehicleWorkbenchRequest(string node, Vector2 position, uint revision,
    NCVehicleWorkbenchAction action) : BoundUserInterfaceMessage
{
    public readonly string Node = node;
    public readonly Vector2 Position = position;
    public readonly uint Revision = revision;
    public readonly NCVehicleWorkbenchAction Action = action;
}

[Serializable, NetSerializable]
public sealed class NCVehicleWorkbenchRefresh : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class NCVehicleWorkbenchState(ProtoId<NCVehicleChassisPrototype>? chassis,
    ProtoId<NCVehicleWorkshopPrototype>? profile, string rootSlot, NCVehicleWorkshopPartState[] parts,
    uint revision, LocId feedback, EntProtoId? tool, bool canAssemble, bool pending) : BoundUserInterfaceState
{
    public readonly ProtoId<NCVehicleChassisPrototype>? Chassis = chassis;
    public readonly ProtoId<NCVehicleWorkshopPrototype>? Profile = profile;
    public readonly string RootSlot = rootSlot;
    public readonly NCVehicleWorkshopPartState[] Parts = parts;
    public readonly uint Revision = revision;
    public readonly LocId Feedback = feedback;
    public readonly EntProtoId? Tool = tool;
    public readonly bool CanAssemble = canAssemble;
    public readonly bool Pending = pending;
}

[Serializable, NetSerializable]
public sealed partial class NCVehicleWorkbenchDoAfter : DoAfterEvent
{
    [DataField] public string Node = string.Empty;
    [DataField] public Vector2 Position;
    [DataField] public uint Revision;
    [DataField] public NCVehicleWorkbenchAction Action;
    public override DoAfterEvent Clone() => (DoAfterEvent) MemberwiseClone();
}
