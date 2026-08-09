// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Housing.Components;

/// <summary>
/// Data-only limits for the player-facing real-estate terminal.
/// Per-user filters, selections and operations remain server-owned.
/// </summary>
[RegisterComponent]
public sealed partial class NCPropertyConsoleComponent : Component
{
    [DataField]
    public int PageSize = 20;

    [DataField]
    public uint MaximumSearchLength = 64;
}
