// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Server.Administration;
using Content.Shared._NC.Fixers;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Fixers.Commands;

/// <summary>
/// Shows or sets the street reputation of a player's current character.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCStreetReputationCommand : LocalizedEntityCommands
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCStreetReputationSystem _reputation = default!;

    public override string Command => "ncrep";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            shell.WriteError(Loc.GetString("cmd-ncrep-error-args"));
            return;
        }

        if (!_players.TryGetSessionByUsername(args[0], out var session))
        {
            shell.WriteError(Loc.GetString("parse-session-fail", ("username", args[0])));
            return;
        }

        if (session.AttachedEntity is not { } entity || !_reputation.TryGetProfile(entity, out var profileId))
        {
            shell.WriteError(Loc.GetString("cmd-ncrep-error-no-character", ("username", args[0])));
            return;
        }

        if (args.Length == 2)
        {
            if (!int.TryParse(args[1], out var value) || value < 0 || value > NCStreetReputationSystem.MaximumReputation)
            {
                shell.WriteError(Loc.GetString("cmd-ncrep-error-value", ("max", NCStreetReputationSystem.MaximumReputation)));
                return;
            }

            _reputation.AddReputation(profileId, value - _reputation.GetReputation(profileId));
        }

        var reputation = _reputation.GetReputation(profileId);
        shell.WriteLine(Loc.GetString("cmd-ncrep-result",
            ("username", args[0]),
            ("value", reputation),
            ("level", LevelName(reputation))));
    }

    private string LevelName(int reputation)
    {
        if (!_prototypes.TryIndex<NCStreetReputationPrototype>(NCStreetReputationPrototype.DefaultId, out var levels))
            return string.Empty;

        var level = levels.Level(reputation);
        return level < levels.LevelNames.Count ? Loc.GetString(levels.LevelNames[level]) : string.Empty;
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHintOptions(CompletionHelper.SessionNames(players: _players),
                Loc.GetString("cmd-ncrep-arg-user")),
            2 => CompletionResult.FromHint(Loc.GetString("cmd-ncrep-arg-value",
                ("max", NCStreetReputationSystem.MaximumReputation))),
            _ => CompletionResult.Empty,
        };
    }
}
