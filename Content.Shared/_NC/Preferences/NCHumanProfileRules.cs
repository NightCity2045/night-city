// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Preferences;

namespace Content.Shared._NC.Preferences;

/// <summary>
/// Keeps player-created Night City characters on the single supported human anatomy profile.
/// This rule is shared by the editor and the authoritative preferences server.
/// </summary>
public static class NCHumanProfileRules
{
    /// <summary>
    /// Returns a human copy when an imported, stored, or client-supplied profile names another species.
    /// Normal profile validation subsequently repairs species-dependent appearance and voice data.
    /// </summary>
    public static HumanoidCharacterProfile Normalize(HumanoidCharacterProfile profile)
    {
        return profile.Species == HumanoidCharacterProfile.DefaultSpecies
            ? profile
            : profile.WithSpecies(HumanoidCharacterProfile.DefaultSpecies);
    }
}
