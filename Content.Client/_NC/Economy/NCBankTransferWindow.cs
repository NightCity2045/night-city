// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Shared._NC.Economy;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using System.Numerics;

namespace Content.Client._NC.Economy;

/// <summary>
/// Two-step personal transfer UI. The client never receives account IDs or credentials.
/// </summary>
public sealed class NCBankTransferWindow : DefaultWindow
{
    private readonly Action<NetEntity, long, string> _transfer;
    private readonly Label _summary = new();
    private readonly LineEdit _amount = new();
    private readonly LineEdit _reason = new();
    private readonly Label _result = new();
    private readonly Button _confirm = new();
    private NCBankTransferPanelState? _state;
    private long? _pendingAmount;

    public NCBankTransferWindow(Action<NetEntity, long, string> transfer)
    {
        _transfer = transfer;
        Title = Loc.GetString("nc-bank-transfer-title");
        MinSize = SetSize = new Vector2(400, 280);

        var content = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 8,
            Margin = new Thickness(12),
        };
        content.AddChild(_summary);
        _amount.PlaceHolder = Loc.GetString("nc-bank-transfer-amount");
        _reason.PlaceHolder = Loc.GetString("nc-bank-transfer-reason");
        content.AddChild(_amount);
        content.AddChild(_reason);

        var review = new Button { Text = Loc.GetString("nc-bank-transfer-review") };
        review.OnPressed += _ => Review();
        content.AddChild(review);

        _confirm.Text = Loc.GetString("nc-bank-transfer-confirm");
        _confirm.Disabled = true;
        _confirm.OnPressed += _ => Confirm();
        content.AddChild(_confirm);
        content.AddChild(_result);
        ContentsContainer.AddChild(content);
    }

    public void UpdateState(NCBankTransferPanelState state)
    {
        _state = state;
        _pendingAmount = null;
        _confirm.Disabled = true;
        _result.Text = string.Empty;
        _summary.Text = Loc.GetString(
            "nc-bank-transfer-summary",
            ("target", state.TargetName),
            ("balance", state.Balance));
    }

    public void ShowResult(NCBankStateEvent state)
    {
        _pendingAmount = null;
        _confirm.Disabled = true;
        _result.Text = state.Error == null
            ? Loc.GetString("nc-bank-transfer-success", ("balance", state.Balance))
            : Loc.GetString(state.Error);
    }

    private void Review()
    {
        if (_state == null ||
            !long.TryParse(_amount.Text, out var amount) ||
            amount <= 0 ||
            amount > _state.Balance ||
            string.IsNullOrWhiteSpace(_reason.Text))
        {
            _result.Text = Loc.GetString("nc-bank-error-invalid-transfer");
            return;
        }

        _pendingAmount = amount;
        _result.Text = Loc.GetString(
            "nc-bank-transfer-pending",
            ("amount", amount),
            ("target", _state.TargetName));
        _confirm.Disabled = false;
    }

    private void Confirm()
    {
        if (_state == null || _pendingAmount == null)
            return;

        _confirm.Disabled = true;
        _transfer(_state.Target, _pendingAmount.Value, _reason.Text.Trim());
    }
}
