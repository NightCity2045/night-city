// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Administration;
using Content.Server._NC.City.Zones.Mapping;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates.Systems;
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
public sealed partial class NCValidateZonesCommand : LocalizedEntityCommands
{
    [Dependency] private NCZoneSystem _zones = default!;

    public override string Command => "nc-zones-validate";
    public override string Description => "Validate all Night City zone definitions and geometry.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError($"Usage: {Command}");
            return;
        }

        var errors = new List<NCZoneValidationError>();
        if (_zones.ValidateAll(errors))
        {
            shell.WriteLine("All Night City zone sets are valid.");
            return;
        }

        foreach (var error in errors)
            shell.WriteError($"{error.ZoneSet} / {error.Zone}: {error.Message}");

        shell.WriteError($"Zone validation failed with {errors.Count} error(s).");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCInspectZonesCommand : LocalizedEntityCommands
{
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCZoneSystem _zones = default!;

    public override string Command => "nc-zones-inspect";
    public override string Description => "Show the city-zone hierarchy at your current coordinates.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError($"Usage: {Command}");
            return;
        }

        if (shell.Player?.AttachedEntity is not { } entity ||
            !_coordinates.TryGetCoordinates(entity, out var coordinates))
        {
            shell.WriteError("Your attached entity is not inside a registered Z-network.");
            return;
        }

        shell.WriteLine(
            $"Network={coordinates.NetworkId}, X={coordinates.Position.X:0.###}, " +
            $"Y={coordinates.Position.Y:0.###}, Z={coordinates.Z}");

        var zones = new List<NCZoneInfo>();
        _zones.GetZones(coordinates, zones);
        if (zones.Count == 0)
        {
            shell.WriteLine("No city zones contain this position.");
            return;
        }

        foreach (var zone in zones)
            shell.WriteLine($"{zone.Kind}: {zone.Name} ({zone.Id})");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCSetZoneActivityCommand : LocalizedEntityCommands
{
    [Dependency] private NCZoneSystem _zones = default!;

    public override string Command => "nc-zone-activity";
    public override string Description => "Get or set the runtime activity mode of a district or sector.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 1 or > 2 ||
            !NCZoneId.TryParse(args[0], out var zoneId))
        {
            shell.WriteError($"Usage: {Command} <zoneId> [Active|Warm|Abstract]");
            return;
        }

        if (args.Length == 1)
        {
            if (_zones.TryGetActivityMode(zoneId, out var current))
                shell.WriteLine($"{zoneId}: {current}");
            else
                shell.WriteError("Zone does not exist or does not support activity modes.");
            return;
        }

        if (!Enum.TryParse<NCZoneActivityMode>(args[1], true, out var mode) ||
            !_zones.SetActivityMode(zoneId, mode))
        {
            shell.WriteError("Unable to change activity mode. Check the zone ID, kind, and current mode.");
            return;
        }

        shell.WriteLine($"{zoneId}: {mode}");
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
