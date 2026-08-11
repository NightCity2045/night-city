// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.UserInterface.Fragments;
using Content.Shared._NC.Agent.Shards.Navigator;
using Content.Shared.CartridgeLoader;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Agent.Shards.Navigator;

/// <summary>
/// Connects the NAVIGATOR display to its server-authoritative physical shard storage.
/// </summary>
public sealed partial class NCAgentNavigatorUi : UIFragment
{
    private NCAgentNavigatorFragment? _fragment;

    public override Control GetUIFragmentRoot()
    {
        return _fragment!;
    }

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        _fragment = new NCAgentNavigatorFragment();
        _fragment.ActionRequested += (action, id, label) => userInterface.SendMessage(
            new CartridgeUiMessage(new NCAgentNavigatorUiMessage(action, id, label)));
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is NCAgentNavigatorUiState navigator)
            _fragment?.UpdateState(navigator.Current, navigator.CurrentZone, navigator.Waypoints);
    }
}
