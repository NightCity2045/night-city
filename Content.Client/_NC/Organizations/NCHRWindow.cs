// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Shared._NC.Organizations;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Prototypes;
using System.Linq;
using System.Numerics;

namespace Content.Client._NC.Organizations;

/// <summary>
/// Personnel-file UI. Choosing an action only prepares it; the separate confirmation button submits it.
/// </summary>
public sealed class NCHRWindow : DefaultWindow
{
    private readonly IPrototypeManager _prototypes;
    private readonly Action<NetEntity, NCEmploymentActionType, string, string?, string, bool, long> _submit;
    private readonly BoxContainer _content;
    private readonly OptionButton _position = new();
    private readonly LineEdit _reason = new();
    private readonly CheckBox _paidSuspension = new();
    private readonly Label _pending = new();
    private readonly Label _result = new();
    private readonly Button _confirm = new();

    private readonly List<string> _positionIds = [];
    private NCHRPanelState? _state;
    private NCEmploymentActionType? _pendingAction;

    public NCHRWindow(
        IPrototypeManager prototypes,
        Action<NetEntity, NCEmploymentActionType, string, string?, string, bool, long> submit)
    {
        _prototypes = prototypes;
        _submit = submit;
        Title = Loc.GetString("nc-hr-title");
        MinSize = SetSize = new Vector2(540, 580);

        var scroll = new ScrollContainer();
        _content = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 7,
            Margin = new Thickness(12),
        };
        scroll.AddChild(_content);
        ContentsContainer.AddChild(scroll);

        _position.OnItemSelected += args => _position.SelectId(args.Id);
        _paidSuspension.Text = Loc.GetString("nc-hr-paid-suspension");
        _reason.PlaceHolder = Loc.GetString("nc-hr-reason-placeholder");
        _confirm.Text = Loc.GetString("nc-hr-confirm");
        _confirm.Disabled = true;
        _confirm.OnPressed += _ => Confirm();
    }

    public void UpdateState(NCHRPanelState state)
    {
        _state = state;
        _pendingAction = null;
        _content.RemoveAllChildren();
        _positionIds.Clear();
        _position.Clear();
        _result.Text = string.Empty;
        _pending.Text = Loc.GetString("nc-hr-no-action");
        _confirm.Disabled = true;

        _content.AddChild(new Label
        {
            Text = Loc.GetString("nc-hr-target", ("name", state.TargetName)),
        });
        _content.AddChild(new Label
        {
            Text = Loc.GetString(
                "nc-hr-current-position",
                ("position", GetPositionName(state.CurrentPositionPrototypeId)),
                ("state", GetEmploymentState(state.EmploymentState))),
        });

        _content.AddChild(new Label { Text = Loc.GetString("nc-hr-position") });
        AddPositions(state);
        _content.AddChild(_position);
        _content.AddChild(new Label { Text = Loc.GetString("nc-hr-reason") });
        _content.AddChild(_reason);
        _content.AddChild(_paidSuspension);

        var actions = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            SeparationOverride = 5,
        };
        AddActionButtons(actions, state);
        _content.AddChild(actions);
        _content.AddChild(_pending);
        _content.AddChild(_confirm);
        _content.AddChild(_result);

        _content.AddChild(new Label { Text = Loc.GetString("nc-hr-history") });
        if (state.History.Length == 0)
        {
            _content.AddChild(new Label { Text = Loc.GetString("nc-hr-history-empty") });
            return;
        }

        foreach (var entry in state.History)
        {
            _content.AddChild(new Label
            {
                Text = Loc.GetString(
                    "nc-hr-history-entry",
                    ("time", entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
                    ("action", GetActionName(entry.Action)),
                    ("old", GetPositionName(entry.OldPositionPrototypeId)),
                    ("new", GetPositionName(entry.NewPositionPrototypeId)),
                    ("reason", entry.Reason)),
            });
        }
    }

    public void ShowResult(bool success, string? error)
    {
        _result.Text = success
            ? Loc.GetString("nc-hr-result-success")
            : Loc.GetString(error ?? "nc-employment-error-unknown");
        if (success)
        {
            _pendingAction = null;
            _confirm.Disabled = true;
        }
    }

    private void AddPositions(NCHRPanelState state)
    {
        var positions = _prototypes.EnumeratePrototypes<NCPositionPrototype>()
            .Where(position => position.Organization == state.OrganizationPrototypeId)
            .Where(position => state.MaxPromotableRankWeight == null ||
                               position.RankWeight <= state.MaxPromotableRankWeight)
            .OrderBy(position => position.RankWeight)
            .ThenBy(position => Loc.GetString(position.Name));

        foreach (var position in positions)
        {
            var index = _positionIds.Count;
            _positionIds.Add(position.ID);
            _position.AddItem(Loc.GetString(position.Name), index);
            if (position.ID == state.CurrentPositionPrototypeId)
                _position.SelectId(index);
        }

        if (_positionIds.Count > 0 && _position.SelectedId < 0)
            _position.SelectId(0);
    }

    private void AddActionButtons(BoxContainer row, NCHRPanelState state)
    {
        var unemployed = state.EmploymentState is 3 or 4;
        if (state.CanHire && unemployed)
            AddActionButton(row, NCEmploymentActionType.Hire);
        if (state.CanPromote && !unemployed)
            AddActionButton(row, NCEmploymentActionType.Promote);
        if (state.CanDemote && !unemployed)
            AddActionButton(row, NCEmploymentActionType.Demote);
        if (state.CanTransfer && !unemployed)
            AddActionButton(row, NCEmploymentActionType.Transfer);
        if (state.CanSuspend && state.EmploymentState == 0)
            AddActionButton(row, NCEmploymentActionType.Suspend);
        if (state.CanSuspend && state.EmploymentState is 1 or 2)
            AddActionButton(row, NCEmploymentActionType.Reinstate);
        if (state.CanDismiss && !unemployed)
            AddActionButton(row, NCEmploymentActionType.Dismiss);
    }

    private void AddActionButton(BoxContainer row, NCEmploymentActionType action)
    {
        var button = new Button { Text = GetActionName(action.ToString()) };
        button.OnPressed += _ =>
        {
            _pendingAction = action;
            _pending.Text = Loc.GetString("nc-hr-pending", ("action", GetActionName(action.ToString())));
            _confirm.Disabled = string.IsNullOrWhiteSpace(_reason.Text);
        };
        row.AddChild(button);
    }

    private void Confirm()
    {
        if (_state == null ||
            _pendingAction == null ||
            string.IsNullOrWhiteSpace(_reason.Text))
        {
            _result.Text = Loc.GetString("nc-employment-error-invalid-reason");
            return;
        }

        string? position = null;
        if (_pendingAction is NCEmploymentActionType.Hire or
            NCEmploymentActionType.Promote or
            NCEmploymentActionType.Demote or
            NCEmploymentActionType.Transfer)
        {
            if (_position.SelectedId < 0 || _position.SelectedId >= _positionIds.Count)
                return;
            position = _positionIds[_position.SelectedId];
        }

        _confirm.Disabled = true;
        _submit(
            _state.Target,
            _pendingAction.Value,
            _state.OrganizationPrototypeId,
            position,
            _reason.Text.Trim(),
            _pendingAction == NCEmploymentActionType.Suspend && _paidSuspension.Pressed,
            _state.EmploymentVersion);
    }

    private string GetPositionName(string? prototypeId)
    {
        return prototypeId != null &&
               _prototypes.TryIndex<NCPositionPrototype>(prototypeId, out var position)
            ? Loc.GetString(position.Name)
            : Loc.GetString("nc-hr-unemployed");
    }

    private static string GetEmploymentState(byte state)
    {
        return Loc.GetString(state switch
        {
            0 => "nc-hr-state-active",
            1 => "nc-hr-state-suspended-paid",
            2 => "nc-hr-state-suspended-unpaid",
            3 => "nc-hr-state-terminated",
            _ => "nc-hr-state-invalid",
        });
    }

    private static string GetActionName(string action)
    {
        return Loc.GetString($"nc-hr-action-{action.ToLowerInvariant()}");
    }
}
