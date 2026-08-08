// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Client.Lobby.UI;

public sealed partial class CharacterPickerButton
{
    private static bool TryGetNCDisplayDepartment(
        IPrototypeManager prototypes,
        ProtoId<JobPrototype> job,
        out DepartmentPrototype department)
    {
        foreach (var candidate in prototypes.EnumeratePrototypes<DepartmentPrototype>())
        {
            if (!candidate.NCSelectable || !candidate.Roles.Contains(job))
                continue;

            department = candidate;
            return true;
        }

        department = default!;
        return false;
    }
}
