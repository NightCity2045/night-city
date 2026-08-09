// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Identity;

namespace Content.Server._NC.Agent.Components;

/// <summary>
/// Runtime call relay marker attached to the current holder of a connected Agent.
/// </summary>
[RegisterComponent]
public sealed partial class NCActiveAgentCallComponent : Component
{
    [ViewVariables]
    public NCCharacterId DeviceOwner;

    [ViewVariables]
    public EntityUid Agent;
}
