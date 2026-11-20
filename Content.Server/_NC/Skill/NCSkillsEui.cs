// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.EUI;
using Content.Shared._NC.Skill;
using Content.Shared.Administration;
using Content.Shared.Eui;
using Robust.Shared.Console;

namespace Content.Server._NC.Skill;

public sealed class NCSkillsEui(NCSkillSystem skills) : BaseEui
{
    private bool _buying;

    public override void Opened() => StateDirty();
    public override void Closed() => skills.CloseWindow(Player);
    public override EuiStateBase GetNewState() => skills.GetState(Player);

    public override async void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);
        if (msg is not NCLearnSkillMessage learn || _buying)
            return;

        _buying = true;
        var success = await skills.Learn(Player, learn.Skill);
        _buying = false;
        if (!IsShutDown)
            SendMessage(new NCLearnSkillResult(success));
    }
}

[AnyCommand]
public sealed partial class NCSkillsCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "ncskills";
    public string Description => Loc.GetString("nc-skill-open-command");
    public string Help => Command;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is { } player)
            _entities.System<NCSkillSystem>().OpenWindow(player);
    }
}
