// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Coordinates;

/// <summary>
/// Persistent identity of a Z-level network.
/// Unlike an <see cref="EntityUid"/>, this value is safe to store between rounds and map reloads.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCZNetworkId(Guid Value)
{
    /// <summary>
    /// An empty GUID is reserved for an unconfigured network.
    /// </summary>
    public bool IsValid => Value != Guid.Empty;

    /// <summary>
    /// Creates an identity for a transient network that will not be referenced by persistent data.
    /// </summary>
    public static NCZNetworkId NewTransient()
    {
        return new NCZNetworkId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }

    public static bool TryParse(string value, out NCZNetworkId networkId)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            networkId = new NCZNetworkId(guid);
            return true;
        }

        networkId = default;
        return false;
    }
}
