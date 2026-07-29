// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using System.Numerics;

namespace Content.Client._NC.RED.Progression;

/// <summary>
/// Explicit final review for an irreversible skill-point purchase.
/// </summary>
public sealed class NCSkillConfirmationWindow : DefaultWindow
{
    public NCSkillConfirmationWindow(
        string skillName,
        int currentRank,
        int targetRank,
        int cost,
        int remainingAfter,
        Action confirm)
    {
        Title = Loc.GetString("nc-character-skill-confirm-title");
        MinSize = SetSize = new Vector2(360, 180);

        var content = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 8,
            Margin = new Thickness(12),
        };
        content.AddChild(new Label
        {
            Text = Loc.GetString(
                "nc-character-skill-confirm-summary",
                ("skill", skillName),
                ("current", currentRank),
                ("target", targetRank),
                ("cost", cost),
                ("remaining", remainingAfter)),
        });

        var buttons = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            SeparationOverride = 8,
        };
        var confirmButton = new Button
        {
            Text = Loc.GetString("nc-character-skill-confirm"),
        };
        confirmButton.OnPressed += _ =>
        {
            confirm();
            Close();
        };
        var cancelButton = new Button
        {
            Text = Loc.GetString("nc-character-skill-cancel"),
        };
        cancelButton.OnPressed += _ => Close();
        buttons.AddChild(confirmButton);
        buttons.AddChild(cancelButton);
        content.AddChild(buttons);
        ContentsContainer.AddChild(content);
    }
}
