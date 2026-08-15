// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server.Administration;
using Content.Server._NC.City.Zones;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.City.Connectivity.Commands;

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityGraphSummaryCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityConnectivitySystem _city = default!;
    [Dependency] private NCRoomPortalGraphSystem _rooms = default!;
    [Dependency] private NCCitySpawnCandidateSystem _spawns = default!;
    [Dependency] private NCCityConnectivityValidationSystem _validation = default!;
    [Dependency] private NCZoneSystem _zones = default!;

    public override string Command => "nccitygraph-summary";
    public override string Description => "Show version and size summary for Night City navigation indices.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError($"Usage: {Command}");
            return;
        }

        var zones = new List<NCZoneInfo>();
        var rooms = new List<NCRoomNodeInfo>();
        var roomPortals = new List<NCRoomPortalInfo>();
        var cityPortals = new List<NCCityPortalInfo>();
        var issues = new List<NCCityConnectivityValidationIssue>();
        _zones.GetZones(zones);
        _rooms.GetRooms(rooms);
        _rooms.GetPortals(roomPortals);
        var cityPortalCount = 0;
        foreach (var zone in zones)
        {
            _city.GetOutgoingPortals(zone.Id, cityPortals);
            cityPortalCount += cityPortals.Count;
        }

        var validation = _validation.Validate(issues);
        shell.WriteLine(
            $"CityGraph v{_city.GraphVersion}: {zones.Count} nodes, {cityPortalCount} directed portals.");
        shell.WriteLine(
            $"RoomGraph v{_rooms.GraphVersion}: {rooms.Count} rooms, {roomPortals.Count} portals.");
        shell.WriteLine(
            $"SpawnIndex v{_spawns.IndexVersion}; validation v{validation.ValidationVersion}: " +
            $"{validation.Errors} error(s), {validation.Warnings} warning(s).");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityGraphValidateCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityConnectivityValidationSystem _validation = default!;

    public override string Command => "nccitygraph-validate";
    public override string Description => "Run the complete derived CityGraph/RoomGraph/spawn validation report.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError($"Usage: {Command}");
            return;
        }

        var issues = new List<NCCityConnectivityValidationIssue>();
        var summary = _validation.Validate(issues);
        foreach (var issue in issues)
        {
            var line = $"{issue.Code} / {issue.ZoneId}: {issue.Message}";
            if (issue.Severity == NCCityConnectivityIssueSeverity.Error)
                shell.WriteError(line);
            else
                shell.WriteLine($"WARNING: {line}");
        }

        shell.WriteLine(
            $"Validation v{summary.ValidationVersion} against CityGraph v{summary.CityGraphVersion}, " +
            $"RoomGraph v{summary.RoomGraphVersion}, SpawnIndex v{summary.SpawnIndexVersion}: " +
            $"{summary.Errors} error(s), {summary.Warnings} warning(s).");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityGraphInspectZoneCommand : LocalizedEntityCommands
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCCityConnectivitySystem _city = default!;
    [Dependency] private NCRoomPortalGraphSystem _rooms = default!;
    [Dependency] private NCCitySpawnCandidateSystem _spawns = default!;
    [Dependency] private NCZoneSystem _zones = default!;

    public override string Command => "nccitygraph-zone";
    public override string Description => "Inspect navigation rooms, directed portals and spawn candidates for one NCZoneId.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !NCZoneId.TryParse(args[0], out var zoneId))
        {
            shell.WriteError($"Usage: {Command} <zoneId>");
            return;
        }

        if (!_zones.TryGetZone(zoneId, out var zone))
        {
            shell.WriteError($"Unknown city zone {zoneId}.");
            return;
        }

        shell.WriteLine($"{zone.ContextSlot}: {zone.Name} ({zone.Id}), parent={zone.Parent}");
        var rooms = new List<NCRoomNodeInfo>();
        _rooms.GetRooms(zoneId, rooms);
        shell.WriteLine($"Rooms: {rooms.Count}");
        foreach (var room in rooms)
        {
            shell.WriteLine(
                $"  room {room.Id.Value}: z={room.Anchor.Z}, anchor={room.Anchor.Position}, " +
                $"navigationNodes={room.NavigationNodeCount}");
        }

        var portals = new List<NCCityPortalInfo>();
        _city.GetOutgoingPortals(zoneId, portals);
        shell.WriteLine($"Outgoing city portals: {portals.Count}");
        foreach (var portal in portals)
        {
            shell.WriteLine(
                $"  portal {portal.Id.Value}: {portal.Kind} -> {portal.ToZone}, " +
                $"z {portal.EntryTarget.Z}->{portal.ExitTarget.Z}, " +
                $"cost={portal.BaseTravelSeconds + portal.ThreatCost:0.###}, enabled={portal.Enabled}, " +
                $"stable={(portal.StableId.IsValid ? portal.StableId.ToString() : "no")}");
        }

        var candidates = new List<NCCitySpawnCandidateInfo>();
        foreach (var profile in _prototypes.EnumeratePrototypes<NCCitySpawnCandidateProfilePrototype>()
                     .OrderBy(static profile => profile.ID, StringComparer.Ordinal))
        {
            _spawns.GetCandidates(profile.ID, zoneId, candidates);
            shell.WriteLine($"Spawn candidates {profile.ID}: {candidates.Count}");
        }
    }
}
