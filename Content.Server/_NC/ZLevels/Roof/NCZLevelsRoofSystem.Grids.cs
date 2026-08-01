/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Linq;
using Content.Shared._NC.ZLevels.Core.Components;
using Content.Shared._NC.ZLevels.Core.EntitySystems;
using Content.Shared._NC.ZLevels.Roof;
using Content.Shared.Light.Components;
using Content.Shared.Maps;

namespace Content.Server._NC.ZLevels.Roof;

public sealed partial class NCZLevelsRoofSystem
{
    [Dependency] private EntityQuery<NCZGridComponent> _zgridQuery = default!;
    [Dependency] private EntityQuery<NCZGridNetworkComponent> _zGridNetworkQuery = default!;

    private void InitGrids()
    {
        SubscribeLocalEvent<NCZGridComponent, MapInitEvent>(OnZGridMapInit);

        SubscribeLocalEvent<NCZGridNetworkComponent, NCZLevelGridNetworkUpdatedEvent>(OnZGridNetworkUpdate);
    }

    private void OnZGridNetworkUpdate(Entity<NCZGridNetworkComponent> ent, ref NCZLevelGridNetworkUpdatedEvent args)
    {
        RecalculateGridRoofs(ent);
    }

    private void OnZGridMapInit(Entity<NCZGridComponent> ent, ref MapInitEvent args)
    {
        EnsureComp<NCZLevelRoofComponent>(ent.Owner);
    }

    public void RecalculateGridRoofs(Entity<NCZGridNetworkComponent> network)
    {
        _roofMap.Clear();

        var sorted = network.Comp.Grids
            .Select(g => (Grid: g, Depth: ZLevel.TryGetGridZDepth(g)))
            .Where(x => x.Depth.HasValue)
            .OrderByDescending(x => x.Depth!.Value);

        foreach (var (gridUid, _) in sorted)
        {
            RemCompDeferred<ImplicitRoofComponent>(gridUid); //hack but that way we dont need edit vanilla code

            if (!GridQuery.TryComp(gridUid, out var grid))
                continue;
            var roofComp = EnsureComp<RoofComponent>(gridUid);
            var enumerator = Map.GetAllTilesEnumerator(gridUid, grid);

            while (enumerator.MoveNext(out var tileRef))
            {
                var worldTile = ZLevel.GridTileToWorldTile(gridUid, grid, tileRef.Value.GridIndices);

                Roof.SetRoof((gridUid, grid, roofComp),
                    tileRef.Value.GridIndices,
                    _roofMap.Contains(worldTile));

                var tileDef = (ContentTileDefinition)TilDefMan[tileRef.Value.Tile.TypeId];
                if (!tileDef.Transparent)
                    _roofMap.Add(worldTile);
            }
        }
    }
}
