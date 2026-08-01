// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.City.Zones;

/// <summary>
/// Persistent identity of a logical city zone.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCZoneId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;

    public override string ToString()
    {
        return Value.ToString("D");
    }

    public static bool TryParse(string value, out NCZoneId zoneId)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            zoneId = new NCZoneId(guid);
            return true;
        }

        zoneId = default;
        return false;
    }
}
