// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Vehicle;

[Serializable, NetSerializable]
public enum NCVehicleOwnershipAction : byte { Offer, Withdraw, Accept, Rekey }

[Serializable, NetSerializable]
public sealed class NCVehicleOwnershipMessage(NCVehicleOwnershipAction action, int price, uint revision) : BoundUserInterfaceMessage
{
    public readonly NCVehicleOwnershipAction Action = action;
    public readonly int Price = price;
    public readonly uint Revision = revision;
}

[Serializable, NetSerializable]
public sealed class NCVehicleOwnershipState(string owner, int price, uint revision) : BoundUserInterfaceState
{
    public readonly string Owner = owner;
    public readonly int Price = price;
    public readonly uint Revision = revision;
}

[Serializable, NetSerializable]
public sealed partial class NCVehicleTheftDoAfterEvent : DoAfterEvent
{
    [DataField] public bool Hotwire;
    [DataField] public uint Generation;
    public override DoAfterEvent Clone() => new NCVehicleTheftDoAfterEvent { Hotwire = Hotwire, Generation = Generation };
}
