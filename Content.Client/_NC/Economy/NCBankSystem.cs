// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Shared._NC.Economy;

namespace Content.Client._NC.Economy;

public sealed partial class NCBankSystem : EntitySystem
{
    private NCBankTransferWindow? _window;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCBankTransferPanelState>(OnPanelState);
        SubscribeNetworkEvent<NCBankStateEvent>(OnBankState);
    }

    private void OnPanelState(NCBankTransferPanelState state)
    {
        _window ??= new NCBankTransferWindow(Transfer);
        _window.UpdateState(state);
        _window.OpenCentered();
    }

    private void OnBankState(NCBankStateEvent state)
    {
        _window?.ShowResult(state);
    }

    private void Transfer(NetEntity target, long amount, string reason)
    {
        RaiseNetworkEvent(new NCBankTransferRequest(target, amount, reason, Guid.NewGuid()));
    }
}
