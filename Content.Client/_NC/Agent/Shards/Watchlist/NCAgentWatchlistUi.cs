// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.UserInterface.Fragments;
using Content.Shared.CartridgeLoader.Cartridges;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Agent.Shards.Watchlist;

/// <summary>
/// Presents the vanilla criminal-record query as the Agent WATCHLIST reader.
/// </summary>
public sealed partial class NCAgentWatchlistUi : UIFragment
{
    private NCAgentWatchlistFragment? _fragment;

    public override Control GetUIFragmentRoot() => _fragment!;

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        _fragment = new NCAgentWatchlistFragment();
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is WantedListUiState watchlist)
            _fragment?.UpdateState(watchlist.Records);
    }
}
