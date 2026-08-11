// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.UserInterface.Fragments;
using Content.Shared.CartridgeLoader;
using Content.Shared.CartridgeLoader.Cartridges;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Agent.Shards.Newswire;

/// <summary>
/// Presents the vanilla station news feed as a dedicated CityNet NEWSWIRE reader.
/// </summary>
public sealed partial class NCAgentNewswireUi : UIFragment
{
    private NCAgentNewswireFragment? _fragment;

    public override Control GetUIFragmentRoot() => _fragment!;

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        _fragment = new NCAgentNewswireFragment();
        _fragment.ActionRequested += action => userInterface.SendMessage(
            new CartridgeUiMessage(new NewsReaderUiMessageEvent(action)));
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        switch (state)
        {
            case NewsReaderBoundUserInterfaceState news:
                _fragment?.UpdateArticle(news.Article, news.TargetNum, news.TotalNum, news.NotificationOn);
                break;
            case NewsReaderEmptyBoundUserInterfaceState empty:
                _fragment?.UpdateEmpty(empty.NotificationOn);
                break;
        }
    }
}
