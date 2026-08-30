// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Persistence.Identity;

/// <summary>
/// Stable identity of a persistent unique item. It is independent from a materialized EntityUid.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCItemId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;

    public static NCItemId New()
    {
        return new NCItemId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
