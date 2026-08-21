// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Economy.Shop;
using Robust.Client.GameObjects;

namespace Content.Client._NC.LivingCity.Economy.Shop;

public sealed class NCEconomicShopBoundUserInterface(EntityUid owner, Enum uiKey)
    : BoundUserInterface(owner, uiKey)
{
    private NCEconomicShopWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = new NCEconomicShopWindow();
        _window.OnPurchase += (kind, assetId, quantity) =>
            SendMessage(new NCEconomicShopPurchaseMessage(kind, assetId, quantity));
        _window.OpenCentered();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is NCEconomicShopState shopState)
            _window?.UpdateState(shopState);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _window?.Dispose();
    }
}
