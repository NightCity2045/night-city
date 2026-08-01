// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.City.Zones;

/// <summary>
/// Persistent identity of a district. It wraps the district's stable zone identity without duplicating it.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCDistrictId(NCZoneId ZoneId)
{
    public Guid Value => ZoneId.Value;
    public bool IsValid => ZoneId.IsValid;
    public override string ToString() => ZoneId.ToString();

    public static bool TryParse(string value, out NCDistrictId id)
    {
        if (NCZoneId.TryParse(value, out var zoneId))
        {
            id = new NCDistrictId(zoneId);
            return true;
        }

        id = default;
        return false;
    }
}

/// <summary>
/// Persistent identity of a sector. It wraps the sector's stable zone identity without duplicating it.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCSectorId(NCZoneId ZoneId)
{
    public Guid Value => ZoneId.Value;
    public bool IsValid => ZoneId.IsValid;
    public override string ToString() => ZoneId.ToString();

    public static bool TryParse(string value, out NCSectorId id)
    {
        if (NCZoneId.TryParse(value, out var zoneId))
        {
            id = new NCSectorId(zoneId);
            return true;
        }

        id = default;
        return false;
    }
}

/// <summary>
/// Persistent identity of a street. It wraps the street's stable zone identity without duplicating it.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCStreetId(NCZoneId ZoneId)
{
    public Guid Value => ZoneId.Value;
    public bool IsValid => ZoneId.IsValid;
    public override string ToString() => ZoneId.ToString();

    public static bool TryParse(string value, out NCStreetId id)
    {
        if (NCZoneId.TryParse(value, out var zoneId))
        {
            id = new NCStreetId(zoneId);
            return true;
        }

        id = default;
        return false;
    }
}

/// <summary>
/// Persistent identity of a building. It wraps the building's stable zone identity without duplicating it.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCBuildingId(NCZoneId ZoneId)
{
    public Guid Value => ZoneId.Value;
    public bool IsValid => ZoneId.IsValid;
    public override string ToString() => ZoneId.ToString();

    public static bool TryParse(string value, out NCBuildingId id)
    {
        if (NCZoneId.TryParse(value, out var zoneId))
        {
            id = new NCBuildingId(zoneId);
            return true;
        }

        id = default;
        return false;
    }
}

/// <summary>
/// Persistent identity of an apartment. It wraps the apartment's stable zone identity without duplicating it.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCApartmentId(NCZoneId ZoneId)
{
    public Guid Value => ZoneId.Value;
    public bool IsValid => ZoneId.IsValid;
    public override string ToString() => ZoneId.ToString();

    public static bool TryParse(string value, out NCApartmentId id)
    {
        if (NCZoneId.TryParse(value, out var zoneId))
        {
            id = new NCApartmentId(zoneId);
            return true;
        }

        id = default;
        return false;
    }
}
