// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Cargo.Pawn;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Cargo.Pawn;

public sealed class NCPawnShopBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private NCPawnShopWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<NCPawnShopWindow>();
        _window.AppraiseRequested += () => SendMessage(new NCPawnShopAppraiseMessage());
        _window.SellRequested += quote => SendMessage(new NCPawnShopSellMessage(quote));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is NCPawnShopState pawnState)
            _window?.UpdateState(pawnState);
    }
}
