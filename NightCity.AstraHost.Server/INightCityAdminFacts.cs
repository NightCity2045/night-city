// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.AstraGraph;

/// <summary>
/// Admin lookup Content can implement without naming AstraGraph types.
/// </summary>
public interface INightCityAdminFacts
{
    bool TryGetAdmin(string userId, out uint adminFlags, out string? rank, out bool isSandbox);
}
