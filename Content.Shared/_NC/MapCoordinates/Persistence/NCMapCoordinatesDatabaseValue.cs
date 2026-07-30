// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Coordinates.Persistence;

/// <summary>
/// Database representation of <see cref="NCMapCoordinates"/>.
/// These four scalar values can be embedded into any persistent entity without storing runtime map identifiers.
/// </summary>
public readonly record struct NCMapCoordinatesDatabaseValue(Guid NetworkId, float X, float Y, int Z);

/// <summary>
/// Converts persistent coordinates to and from database scalar values.
/// </summary>
public static class NCMapCoordinatesDatabaseCodec
{
    public static NCMapCoordinatesDatabaseValue Encode(NCMapCoordinates coordinates)
    {
        return new NCMapCoordinatesDatabaseValue(
            coordinates.NetworkId.Value,
            coordinates.Position.X,
            coordinates.Position.Y,
            coordinates.Z);
    }

    public static bool TryDecode(NCMapCoordinatesDatabaseValue value, out NCMapCoordinates coordinates)
    {
        var networkId = new NCZNetworkId(value.NetworkId);
        if (!networkId.IsValid ||
            !float.IsFinite(value.X) ||
            !float.IsFinite(value.Y))
        {
            coordinates = default;
            return false;
        }

        coordinates = new NCMapCoordinates(networkId, new System.Numerics.Vector2(value.X, value.Y), value.Z);
        return true;
    }
}
