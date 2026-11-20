// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.Eui;
using Content.Shared._NC.Skill;
using Content.Shared.Eui;

namespace Content.Client._NC.Skill;

public sealed class NCSkillsEui : BaseEui
{
    private readonly NCSkillWindow _window = new(false);

    public NCSkillsEui()
    {
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
        _window.Purchase += skill => SendMessage(new NCLearnSkillMessage(skill));
    }

    public override void Opened() => _window.OpenCentered();
    public override void Closed() => _window.Close();

    public override void HandleState(EuiStateBase state)
    {
        if (state is NCSkillsState skills)
            _window.SetProgress(skills);
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        if (msg is NCLearnSkillResult result)
            _window.FinishPurchase(result.Success);
    }
}

public sealed class NCSkillTreeEditorEui : BaseEui
{
    private readonly NCSkillWindow _window = new(true);

    public NCSkillTreeEditorEui()
    {
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
        _window.EditRequested += (tree, nodes) => SendMessage(new NCSkillEditorMessage(tree, nodes));
    }

    public override void Opened() => _window.OpenCentered();
    public override void Closed() => _window.Close();

    public override void HandleMessage(EuiMessageBase msg)
    {
        if (msg is NCSkillEditorResult result)
            _window.FinishEdit(result);
    }
}
