// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Shared._NC.Organizations;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Organizations;

/// <summary>
/// Owns the personnel-file window and sends only explicitly confirmed HR requests.
/// </summary>
public sealed partial class NCHRSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;

    private NCHRWindow? _window;
    private NCHROsterWindow? _roster;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCHRPanelState>(OnPanelState);
        SubscribeNetworkEvent<NCHROnlineListState>(OnOnlineList);
        SubscribeNetworkEvent<NCEmploymentActionResponse>(OnActionResponse);
    }

    private void OnOnlineList(NCHROnlineListState state)
    {
        _roster ??= new NCHROsterWindow(_prototypes, OpenFile, RefreshRoster);
        _roster.UpdateState(state);
        _roster.OpenCentered();
    }

    private void OnPanelState(NCHRPanelState state)
    {
        _window ??= new NCHRWindow(_prototypes, Submit);
        _window.UpdateState(state);
        _window.OpenCentered();
    }

    private void OnActionResponse(NCEmploymentActionResponse response)
    {
        _window?.ShowResult(response.Success, response.Error);
    }

    private void Submit(
        NetEntity target,
        NCEmploymentActionType action,
        string organization,
        string? position,
        string reason,
        bool paidSuspension,
        long expectedVersion)
    {
        RaiseNetworkEvent(new NCEmploymentActionRequest(
            target,
            action,
            organization,
            position,
            reason,
            Guid.NewGuid(),
            paidSuspension,
            expectedVersion));
    }

    private void OpenFile(NetEntity target)
    {
        RaiseNetworkEvent(new NCHROpenFileRequest(target));
    }

    private void RefreshRoster()
    {
        RaiseNetworkEvent(new NCHROnlineListRequest());
    }
}
