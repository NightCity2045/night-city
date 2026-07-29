// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Shared._NC.Organizations;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Prototypes;
using System.Numerics;

namespace Content.Client._NC.Organizations;

/// <summary>
/// Lists only currently connected candidates and members visible to the actor's organization.
/// </summary>
public sealed class NCHROsterWindow : DefaultWindow
{
    private readonly IPrototypeManager _prototypes;
    private readonly Action<NetEntity> _openFile;
    private readonly BoxContainer _rows;

    public NCHROsterWindow(
        IPrototypeManager prototypes,
        Action<NetEntity> openFile,
        Action refresh)
    {
        _prototypes = prototypes;
        _openFile = openFile;
        Title = Loc.GetString("nc-hr-roster-title");
        MinSize = SetSize = new Vector2(460, 500);

        var root = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 8,
            Margin = new Thickness(12),
        };
        var refreshButton = new Button { Text = Loc.GetString("nc-hr-roster-refresh") };
        refreshButton.OnPressed += _ => refresh();
        root.AddChild(refreshButton);

        var scroll = new ScrollContainer();
        _rows = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 5,
        };
        scroll.AddChild(_rows);
        root.AddChild(scroll);
        ContentsContainer.AddChild(root);
    }

    public void UpdateState(NCHROnlineListState state)
    {
        _rows.RemoveAllChildren();
        if (state.Characters.Length == 0)
        {
            _rows.AddChild(new Label { Text = Loc.GetString("nc-hr-roster-empty") });
            return;
        }

        foreach (var character in state.Characters)
        {
            var button = new Button
            {
                Text = Loc.GetString(
                    "nc-hr-roster-entry",
                    ("name", character.Name),
                    ("position", GetPositionName(character.PositionPrototypeId))),
            };
            var target = character.Character;
            button.OnPressed += _ => _openFile(target);
            _rows.AddChild(button);
        }
    }

    private string GetPositionName(string? prototypeId)
    {
        return prototypeId != null &&
               _prototypes.TryIndex<NCPositionPrototype>(prototypeId, out var position)
            ? Loc.GetString(position.Name)
            : Loc.GetString("nc-hr-unemployed");
    }
}
