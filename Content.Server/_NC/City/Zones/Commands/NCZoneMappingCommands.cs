// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Administration;
using Content.Server._NC.City.Zones.Mapping;
using Content.Shared._NC.City.Zones;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._NC.City.Zones.Commands;

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCGenerateZoneIdCommand : LocalizedEntityCommands
{
    public override string Command => "nc-zone-new-id";
    public override string Description => "Generate a new persistent NCZoneId as a YAML field.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError($"Usage: {Command}");
            return;
        }

        shell.WriteLine($"id: {new NCZoneId(Guid.NewGuid())}");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCSaveZoneEditorDraftCommand : LocalizedEntityCommands
{
    [Dependency] private NCZoneEditorServerSystem _editor = default!;

    public override string Command => "nc-zones-editor-save";
    public override string Description => "Save your current zone editor draft to server user data.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || shell.Player is not { } player)
        {
            shell.WriteError($"Usage: {Command} <fileName>");
            return;
        }

        if (!_editor.TryExportDraft(player.UserId, args[0], out var path))
        {
            shell.WriteError("No valid editor draft exists, or the file name is invalid.");
            return;
        }

        shell.WriteLine($"Zone editor draft saved to user data: {path}");
    }
}
