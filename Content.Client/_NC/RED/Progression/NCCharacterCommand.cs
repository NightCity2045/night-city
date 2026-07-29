// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Robust.Shared.Console;

namespace Content.Client._NC.RED.Progression;

public sealed partial class NCCharacterCommand : IConsoleCommand
{
    [Dependency] private IEntitySystemManager _systems = default!;

    public string Command => "nc_character";
    public string Description => Loc.GetString("nc-character-command-description");
    public string Help => Command;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        _systems.GetEntitySystem<NCCharacterSystem>().Open();
    }
}
