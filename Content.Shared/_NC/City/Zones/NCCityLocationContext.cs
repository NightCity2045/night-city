// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Coordinates;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.City.Zones;

/// <summary>
/// Stable semantic location of an entity. Empty IDs mean that the relevant level is not defined at this position.
/// Exact X/Y is deliberately excluded so the value changes only when the semantic location changes.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCCityLocationContext(
    NCZNetworkId NetworkId,
    int Z,
    NCDistrictId DistrictId,
    NCSectorId SectorId,
    NCStreetId StreetId,
    NCBuildingId BuildingId,
    NCApartmentId ApartmentId)
{
    public bool IsValid => NetworkId.IsValid;

    public NCZoneId MostSpecific =>
        ApartmentId.IsValid ? ApartmentId.ZoneId :
        BuildingId.IsValid ? BuildingId.ZoneId :
        StreetId.IsValid ? StreetId.ZoneId :
        SectorId.IsValid ? SectorId.ZoneId :
        DistrictId.ZoneId;
}

/// <summary>
/// Raised directly on a tracked entity after its semantic city location changes.
/// </summary>
[ByRefEvent]
public readonly record struct NCZoneChangedEvent(
    NCCityLocationContext OldLocation,
    NCCityLocationContext NewLocation);

public enum NCZoneContextSlot : byte
{
    None,
    District,
    Sector,
    Street,
    Building,
    Apartment,
}

public enum NCZoneActivityMode : byte
{
    Active,
    Warm,
    Abstract,
}

/// <summary>
/// Raised as a broadcast server event when a district or sector changes simulation mode.
/// </summary>
[ByRefEvent]
public readonly record struct NCZoneActivityChangedEvent(
    NCZoneId ZoneId,
    NCZoneActivityMode OldMode,
    NCZoneActivityMode NewMode);
