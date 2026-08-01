/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Linq;
using Content.Shared._NC.ZLevels.Core.Components;
using Content.Shared._NC.ZLevels.Core.EntitySystems;
using JetBrains.Annotations;
using Robust.Shared.Map.Components;

namespace Content.Server._NC.ZLevels.Core;

public sealed partial class NCZLevelsSystem
{
    [PublicAPI]
    public Entity<NCZGridNetworkComponent> CreateGridNetwork()
    {
        var ent  = Spawn();

        var comp = EnsureComp<NCZGridNetworkComponent>(ent);
        comp.NetworkId = Guid.NewGuid().ToString("N");
        Dirty(ent, comp);

        return (ent, comp);
    }

    [PublicAPI]
    public bool TryAddGridToNetwork(Entity<NCZGridNetworkComponent> gridNetwork, EntityUid grid)
    {
        if (!_mapGridQuery.HasComp(grid))
        {
            Log.Error($"ZGrid: {grid} is not a MapGrid.");
            return false;
        }

        if (TryGetGridNetwork(grid, out var existing))
        {
            Log.Error($"ZGrid: grid {grid} already in network {existing.Owner}.");
            return false;
        }

        gridNetwork.Comp.Grids.Add(grid);
        Dirty(gridNetwork);

        var zGridComp = EnsureComp<NCZGridComponent>(grid);
        zGridComp.NetworkId = gridNetwork.Comp.NetworkId;
        zGridComp.Network   = gridNetwork.Owner;
        Dirty(grid, zGridComp);

        var ev = new NCGridAddedIntoZNetworkEvent(gridNetwork);
        RaiseLocalEvent(grid, ref ev);

        RaiseLocalEvent(gridNetwork, new NCZLevelGridNetworkUpdatedEvent());

        return true;
    }

    [PublicAPI]
    public bool TryRemoveGridFromNetwork(EntityUid grid)
    {
        if (!TryGetGridNetwork(grid, out var gridNetwork))
            return false;

        gridNetwork.Comp.Grids.Remove(grid);
        RemComp<NCZGridComponent>(grid);

        if (!TerminatingOrDeleted(gridNetwork.Owner))
            Dirty(gridNetwork);

        var ev = new NCGridRemovedFromZNetworkEvent(gridNetwork);
        RaiseLocalEvent(grid, ref ev);

        if (gridNetwork.Comp.Grids.Count == 0 && !TerminatingOrDeleted(gridNetwork.Owner))
            QueueDel(gridNetwork);
        else
        {
            RaiseLocalEvent(gridNetwork, new NCZLevelGridNetworkUpdatedEvent());
        }

        return true;
    }

    /// <summary>
    /// Explicit teardown: removes every grid (raising <see cref="NCGridRemovedFromZNetworkEvent"/> per grid)
    /// and queues the manager for deletion.
    /// </summary>
    [PublicAPI]
    public void DeleteGridNetwork(Entity<NCZGridNetworkComponent> network)
    {
        // TryRemoveGridFromNetwork mutates Grids, so iterate a snapshot.
        foreach (var grid in network.Comp.Grids.ToList())
        {
            TryRemoveGridFromNetwork(grid);
        }

        if (!TerminatingOrDeleted(network.Owner))
            QueueDel(network);
    }
}
