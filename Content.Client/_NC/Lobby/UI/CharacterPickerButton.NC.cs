// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Client.Lobby.UI;

public sealed partial class CharacterPickerButton
{
    private static bool TryGetNCDisplayJob(
        IPrototypeManager prototypes,
        HumanoidCharacterProfile profile,
        out JobPrototype job)
    {
        return NCDepartmentJobResolver.TryResolve(prototypes, profile, out job);
    }
}
