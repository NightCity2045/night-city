// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._NC.LivingCity.Neural.Commands;

/// <summary>Administrative console bridge for the command-only OpenAI dialogue test actor.</summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCSpawnNeuralNcpdCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "ncspawnneuralncpd";
    public string Description => "Spawn the current round's neural NCPD API test officer beside you.";
    public string Help => "ncspawnneuralncpd";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0 || shell.Player is not { } player)
        {
            shell.WriteError("Run ncspawnneuralncpd without arguments from an attached admin player.");
            return;
        }

        _entities.System<NCNeuralTestNpcSystem>().RequestSpawn(player, result =>
        {
            if (!result.Success)
            {
                shell.WriteError($"Neural NCPD test spawn failed: {result.Error}");
                return;
            }

            shell.WriteLine($"Neural NCPD test officer spawned: actor {result.ActorId}, entity {result.EntityUid}.");
        });
    }
}
