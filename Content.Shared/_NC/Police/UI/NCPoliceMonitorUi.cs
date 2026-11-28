// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.City.Map;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Police.UI;

[Serializable, NetSerializable]
public enum NCPoliceMonitorUiKey : byte
{
    Key,
}

/// <summary>The city map of the console's floor, sent when the console opens.</summary>
[Serializable, NetSerializable]
public sealed class NCPoliceMonitorMapMessage(NCCityMapSnapshotEvent snapshot) : BoundUserInterfaceMessage
{
    public readonly NCCityMapSnapshotEvent Snapshot = snapshot;
}

/// <summary>People the console sees and the open NCPD incidents.</summary>
[Serializable, NetSerializable]
public sealed class NCPoliceMonitorContactsMessage(List<NCCityMapContact> contacts, List<NCCityMapMarker> incidents)
    : BoundUserInterfaceMessage
{
    public readonly List<NCCityMapContact> Contacts = contacts;
    public readonly List<NCCityMapMarker> Incidents = incidents;
}
