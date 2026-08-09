// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Identity;

namespace Content.Server._NC.Agent.Components;

/// <summary>
/// Stable owner binding carried by a physical Agent. It remains unchanged when the device is stolen.
/// </summary>
[RegisterComponent]
public sealed partial class NCAgentIdentityComponent : Component
{
    [ViewVariables]
    public NCCharacterId CharacterId;
}
