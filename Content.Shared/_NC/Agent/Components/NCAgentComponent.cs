// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;

namespace Content.Shared._NC.Agent.Components;

/// <summary>
/// Marks a vanilla PDA as the Night City Agent while keeping all PDA and ID-card mechanics intact.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class NCAgentComponent : Component
{
    /// <summary>
    /// Display name applied to the PDA entity.
    /// </summary>
    [DataField]
    public LocId AgentName = "nc-agent-name";

    /// <summary>
    /// Display name applied to an ordinary ID card while it is inserted into the Agent.
    /// </summary>
    [DataField]
    public LocId VinCardName = "nc-agent-vin-card-name";
}
