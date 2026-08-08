// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Administration.Managers;
using Content.Server.GameTicking;
using Content.Shared.Administration;
using Content.Shared.GameTicking;
using Robust.Shared.Console;
using Robust.Shared.Enums;

namespace Content.Server._NC.Jobs.Commands;

/// <summary>
/// Late-joins without exposing a role picker. GameTicker resolves the character's employment server-side.
/// </summary>
[AnyCommand]
public sealed partial class NCJoinGameCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "ncjoingame";
    public string Description => "Join Night City using the selected character's persistent employment.";
    public string Help => "ncjoingame";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0 || shell.Player is not { } player)
            return;

        var ticker = _entities.System<GameTicker>();
        if (ticker.PlayerGameStatuses.TryGetValue(player.UserId, out var status) &&
            status == PlayerGameStatus.JoinedGame)
        {
            return;
        }

        if (ticker.RunLevel == GameRunLevel.PreRoundLobby)
            return;

        ticker.MakeJoinGame(player, EntityUid.Invalid);
    }
}
