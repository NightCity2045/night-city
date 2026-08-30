// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Identity;

/// <summary>
/// Stable character identity backed by the existing profile row.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCCharacterId(int Value)
{
    public bool IsValid => Value > 0;

    public override string ToString()
    {
        return Value.ToString();
    }

    public static bool TryParse(string value, out NCCharacterId characterId)
    {
        if (int.TryParse(value, out var parsed) && parsed > 0)
        {
            characterId = new NCCharacterId(parsed);
            return true;
        }

        characterId = default;
        return false;
    }
}
