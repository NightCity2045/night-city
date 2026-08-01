// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.City.Zones.Persistence;

/// <summary>
/// Converts typed city identities to database-safe GUID scalars and back.
/// The zone kind is deliberately verified later by NCZoneSystem against the currently loaded city data.
/// </summary>
public static class NCCityObjectIdDatabaseCodec
{
    public static Guid Encode(NCDistrictId id) => id.Value;
    public static Guid Encode(NCSectorId id) => id.Value;
    public static Guid Encode(NCStreetId id) => id.Value;
    public static Guid Encode(NCBuildingId id) => id.Value;
    public static Guid Encode(NCApartmentId id) => id.Value;

    public static bool TryDecodeDistrict(Guid value, out NCDistrictId id) =>
        NCDistrictId.TryParse(value.ToString("D"), out id);

    public static bool TryDecodeSector(Guid value, out NCSectorId id) =>
        NCSectorId.TryParse(value.ToString("D"), out id);

    public static bool TryDecodeStreet(Guid value, out NCStreetId id) =>
        NCStreetId.TryParse(value.ToString("D"), out id);

    public static bool TryDecodeBuilding(Guid value, out NCBuildingId id) =>
        NCBuildingId.TryParse(value.ToString("D"), out id);

    public static bool TryDecodeApartment(Guid value, out NCApartmentId id) =>
        NCApartmentId.TryParse(value.ToString("D"), out id);
}
