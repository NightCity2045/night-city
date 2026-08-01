// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Administration;
using Content.Shared._NC.Coordinates;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._NC.Coordinates.Commands;

/// <summary>
/// Generates a persistent Z-network identity and prints it in a form ready to paste into YAML.
/// </summary>
[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed class NCGenerateZNetworkIdCommand : LocalizedEntityCommands
{
    public override string Command => "nc-znetwork-new-id";
    public override string Description => "Generate a new persistent NCZNetworkId as a YAML field.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError($"Usage: {Command}");
            return;
        }

        var networkId = new NCZNetworkId(Guid.NewGuid());
        shell.WriteLine($"networkId: {networkId}");
    }
}
