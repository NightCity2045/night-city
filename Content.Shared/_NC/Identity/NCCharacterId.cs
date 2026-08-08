// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Identity;

/// <summary>
/// Stable character identity backed by the existing profile row.
/// </summary>
public readonly record struct NCCharacterId(int Value)
{
    public bool IsValid => Value > 0;
}
