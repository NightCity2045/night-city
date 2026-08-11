// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;

namespace Content.Shared._NC.Agent.Components;

/// <summary>
/// Data-only network coverage attached to a grid. Future city and netrunning systems can update it.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCAgentNetworkCoverageComponent : Component
{
    [DataField, AutoNetworkedField]
    public NCAgentNetworkState State = NCAgentNetworkState.Online;

    [DataField, AutoNetworkedField]
    public string NetworkId = "CITYNET";
}
