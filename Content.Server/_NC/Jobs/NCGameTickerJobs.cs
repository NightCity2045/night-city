// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.GameTicking;
using Content.Shared.Roles;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using System.Linq;

namespace Content.Server.GameTicking;

public sealed partial class GameTicker
{
    /// <summary>
    /// Ignores any client-requested late-join role and resolves the selected character's server-owned employment.
    /// </summary>
    private ProtoId<JobPrototype> ResolveNCJoinJob(ICommonSession player)
    {
        if (!_prefsManager.TryGetSelectedNCEmployment(player.UserId, out var employment) ||
            !IsNCJob(employment) ||
            (_banManager.GetJobBans(player.UserId)?.Contains(employment) ?? false))
        {
            return FallbackOverflowJob;
        }

        return employment;
    }

    private bool IsNCJob(ProtoId<JobPrototype> job)
    {
        return ProtoMan.EnumeratePrototypes<DepartmentPrototype>()
            .Any(department => department.NCSelectable && department.Roles.Contains(job));
    }
}
