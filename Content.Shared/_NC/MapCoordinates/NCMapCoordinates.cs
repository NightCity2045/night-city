// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Coordinates.Serialization;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Coordinates;

/// <summary>
/// Persistent city coordinates that can be resolved against the currently loaded Z-level network.
/// </summary>
[DataDefinition]
[Serializable, NetSerializable]
public partial struct NCMapCoordinates : IEquatable<NCMapCoordinates>
{
    [DataField(required: true, customTypeSerializer: typeof(NCZNetworkIdSerializer))]
    public NCZNetworkId NetworkId { get; private set; }

    /// <summary>
    /// Position in map space. It is intentionally not local to a moving or rotated grid.
    /// </summary>
    [DataField(required: true)]
    public Vector2 Position { get; private set; }

    /// <summary>
    /// Logical depth in the Z-level network.
    /// </summary>
    [DataField(required: true)]
    public int Z { get; private set; }

    public NCMapCoordinates(NCZNetworkId networkId, Vector2 position, int z)
    {
        NetworkId = networkId;
        Position = position;
        Z = z;
    }

    public bool Equals(NCMapCoordinates other)
    {
        return NetworkId == other.NetworkId && Position == other.Position && Z == other.Z;
    }

    public override bool Equals(object? obj)
    {
        return obj is NCMapCoordinates other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(NetworkId, Position, Z);
    }

    public static bool operator ==(NCMapCoordinates left, NCMapCoordinates right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(NCMapCoordinates left, NCMapCoordinates right)
    {
        return !left.Equals(right);
    }
}
