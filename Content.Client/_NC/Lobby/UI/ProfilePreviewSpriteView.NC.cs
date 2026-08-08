// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Preferences;
using Content.Shared.Roles;

namespace Content.Client.Lobby.UI.ProfileEditorControls;

public sealed partial class ProfilePreviewSpriteView
{
    private bool TryGetNCPreviewJob(HumanoidCharacterProfile profile, out JobPrototype job)
    {
        return NCDepartmentJobResolver.TryResolve(_prototypeManager, profile, out job);
    }
}
