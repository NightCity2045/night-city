// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.Pinpointer;
using Robust.Shared.Console;
using Robust.Shared.Map.Components;

namespace Content.Server._NC.City.Atlas;

/// <summary>
/// Shows what building detection sees on the caller's floor, so a mapper can tell why buildings are missing:
/// no NavMap, no street to flood from, or a sealed area too large to be one building.
/// </summary>
[AdminCommand(AdminFlags.Mapping)]
public sealed partial class NCCityBuildingsCommand : LocalizedEntityCommands
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private NCCityAtlasSystem _atlas = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override string Command => "nccitybuildings";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player?.AttachedEntity is not { } user ||
            _entities.GetComponent<TransformComponent>(user).MapUid is not { } map)
        {
            shell.WriteError(Loc.GetString("cmd-nccitybuildings-no-floor"));
            return;
        }

        var grids = _entities.EntityQueryEnumerator<NavMapComponent, MapGridComponent, TransformComponent>();
        while (grids.MoveNext(out var uid, out var navMap, out _, out var xform))
        {
            if (xform.MapUid != map)
                continue;

            var (position, rotation) = _transform.GetWorldPositionRotation(uid);
            shell.WriteLine(Loc.GetString("cmd-nccitybuildings-grid",
                ("grid", _entities.ToPrettyString(uid)),
                ("chunks", navMap.Chunks.Count),
                ("x", position.X),
                ("y", position.Y),
                ("rotation", rotation.Degrees)));
        }

        var index = _atlas.GetBuildings(map);
        var stats = index.Stats;
        shell.WriteLine(Loc.GetString("cmd-nccitybuildings-tiles",
            ("grids", stats.Grids),
            ("floor", stats.FloorTiles),
            ("walls", stats.WallTiles),
            ("doors", stats.DoorTiles)));
        shell.WriteLine(Loc.GetString("cmd-nccitybuildings-street",
            ("seeds", stats.StreetSeeds),
            ("street", stats.StreetTiles)));
        shell.WriteLine(Loc.GetString("cmd-nccitybuildings-regions",
            ("regions", stats.Regions),
            ("largest", stats.LargestRegion),
            ("small", stats.DroppedSmall),
            ("large", stats.DroppedLarge),
            ("limit", NCCityBuildingIndex.MaximumTiles)));
        shell.WriteLine(Loc.GetString("cmd-nccitybuildings-result", ("buildings", index.Buildings.Count)));
    }
}
