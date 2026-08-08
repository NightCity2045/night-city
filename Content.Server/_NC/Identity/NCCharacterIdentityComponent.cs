// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Identity;

namespace Content.Server._NC.Identity;

/// <summary>
/// Stable character identity attached to the mind, which survives body replacement during a launch.
/// </summary>
[RegisterComponent]
public sealed partial class NCCharacterIdentityComponent : Component
{
    [DataField, ViewVariables]
    public NCCharacterId CharacterId;
}
