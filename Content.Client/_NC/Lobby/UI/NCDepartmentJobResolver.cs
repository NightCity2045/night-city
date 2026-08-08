// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Client.Lobby.UI;

/// <summary>
/// Resolves the concrete entry job used to display a character's selected department.
/// Employment remains server-authoritative; this helper is presentation-only.
/// </summary>
internal static class NCDepartmentJobResolver
{
    public static bool TryResolve(
        IPrototypeManager prototypes,
        HumanoidCharacterProfile profile,
        out JobPrototype job)
    {
        job = default!;
        if (profile.NCDepartmentPreference is not { } departmentId ||
            !prototypes.TryIndex(departmentId, out var department) ||
            department.NCEntryJob is not { } entryJobId ||
            !prototypes.TryIndex(entryJobId, out var entryJob))
        {
            return false;
        }

        job = entryJob;
        return true;
    }
}
