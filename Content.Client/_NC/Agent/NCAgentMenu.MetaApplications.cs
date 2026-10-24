// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._NC.Agent;

public sealed partial class NCAgentMenu
{
    private string _metaBrowserAddress = "citi://";
    private readonly List<string> _metaBrowserHistory = [];
    private int _metaBrowserIndex = -1;
    private Label? _metaBrowserStatus;
    private string _metaBrowserNotice = "";
    public event Action<string>? MetaSiteRequested;

    public void ShowMetaSiteNotice(string notice)
    {
        _metaBrowserNotice = notice;
        if (_metaBrowserStatus is not null)
            _metaBrowserStatus.Text = notice;
    }

    private Control CreateMetaBrowserEntry()
    {
        var row = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal };
        var address = new LineEdit { Text = _metaBrowserAddress, PlaceHolder = Loc.GetString("nc-meta-browser-address"), HorizontalExpand = true };
        address.OnTextChanged += _ => _metaBrowserAddress = address.Text;
        var back = new Button { Text = Loc.GetString("nc-meta-browser-back"), Disabled = _metaBrowserIndex <= 0 };
        var forward = new Button { Text = Loc.GetString("nc-meta-browser-forward"), Disabled = _metaBrowserIndex < 0 || _metaBrowserIndex >= _metaBrowserHistory.Count - 1 };
        var open = new Button { Text = Loc.GetString("nc-meta-browser-open") };
        void Navigate(bool remember)
        {
            if (address.Text.Length > 192)
                return;
            if (remember)
            {
                if (_metaBrowserIndex + 1 < _metaBrowserHistory.Count)
                    _metaBrowserHistory.RemoveRange(_metaBrowserIndex + 1, _metaBrowserHistory.Count - _metaBrowserIndex - 1);
                if (_metaBrowserHistory.Count >= 32)
                    _metaBrowserHistory.RemoveAt(0);
                _metaBrowserHistory.Add(address.Text);
                _metaBrowserIndex = _metaBrowserHistory.Count - 1;
            }
            back.Disabled = _metaBrowserIndex <= 0;
            forward.Disabled = _metaBrowserIndex >= _metaBrowserHistory.Count - 1;
            MetaSiteRequested?.Invoke(address.Text);
        }
        back.OnPressed += _ =>
        {
            if (_metaBrowserIndex <= 0)
                return;
            address.Text = _metaBrowserHistory[--_metaBrowserIndex];
            Navigate(false);
        };
        forward.OnPressed += _ =>
        {
            if (_metaBrowserIndex >= _metaBrowserHistory.Count - 1)
                return;
            address.Text = _metaBrowserHistory[++_metaBrowserIndex];
            Navigate(false);
        };
        open.OnPressed += _ => Navigate(true);
        address.OnTextEntered += _ => Navigate(true);
        row.AddChild(back);
        row.AddChild(forward);
        row.AddChild(address);
        row.AddChild(open);
        var container = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };
        container.AddChild(row);
        _metaBrowserStatus = new Label { Text = _metaBrowserNotice, ClipText = true };
        container.AddChild(_metaBrowserStatus);
        return container;
    }
}
