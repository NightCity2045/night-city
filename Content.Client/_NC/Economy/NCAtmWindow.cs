// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Shared._NC.Economy.Components;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using System.Numerics;

namespace Content.Client._NC.Economy;

public sealed class NCAtmWindow : DefaultWindow
{
    private readonly Action<int> _withdraw;
    private readonly Label _balance = new();
    private readonly Label _fee = new();
    private readonly LineEdit _amount = new();
    private readonly Label _review = new();
    private readonly Button _confirm = new();
    private int _maximumWithdrawal;
    private int? _pendingAmount;

    public NCAtmWindow(Action<int> withdraw)
    {
        _withdraw = withdraw;
        Title = Loc.GetString("nc-atm-title");
        MinSize = SetSize = new Vector2(380, 280);
        var content = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 8,
            Margin = new Thickness(12),
        };
        content.AddChild(_balance);
        content.AddChild(_fee);
        content.AddChild(new Label { Text = Loc.GetString("nc-atm-deposit-help") });
        _amount.PlaceHolder = Loc.GetString("nc-atm-withdraw-amount");
        content.AddChild(_amount);
        var prepare = new Button { Text = Loc.GetString("nc-atm-withdraw-review") };
        prepare.OnPressed += _ => Prepare();
        content.AddChild(prepare);
        content.AddChild(_review);
        _confirm.Text = Loc.GetString("nc-atm-withdraw-confirm");
        _confirm.Disabled = true;
        _confirm.OnPressed += _ =>
        {
            if (_pendingAmount is not { } amount)
                return;
            _confirm.Disabled = true;
            _withdraw(amount);
        };
        content.AddChild(_confirm);
        ContentsContainer.AddChild(content);
    }

    public void UpdateState(NCAtmUiState state)
    {
        _balance.Text = Loc.GetString("nc-atm-balance", ("balance", state.Balance));
        _fee.Text = Loc.GetString(
            "nc-atm-fee",
            ("fee", Math.Round(state.DepositFee * 100f, 1)),
            ("maximum", state.MaximumWithdrawal));
        _maximumWithdrawal = state.MaximumWithdrawal;
        _pendingAmount = null;
        _confirm.Disabled = true;
        _review.Text = string.Empty;
    }

    private void Prepare()
    {
        if (!int.TryParse(_amount.Text, out var amount) ||
            amount <= 0 ||
            amount > _maximumWithdrawal)
        {
            _review.Text = Loc.GetString("nc-bank-error-invalid-transfer");
            return;
        }

        _pendingAmount = amount;
        _review.Text = Loc.GetString("nc-atm-withdraw-pending", ("amount", amount));
        _confirm.Disabled = false;
    }
}
