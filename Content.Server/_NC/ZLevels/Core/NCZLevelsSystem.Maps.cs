/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Linq;
using Content.Server._NC.ZLevels.PVS;
using Content.Shared._NC.ZLevels.Core.Components;
using JetBrains.Annotations;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.ZLevels.Core;

public sealed partial class NCZLevelsSystem
{
    /// <summary>
    /// Creates a new zLevel Map Network entity
    /// </summary>
    [PublicAPI]
    public Entity<NCZMapNetworkComponent> CreateMapNetwork(ComponentRegistry? components = null)
    {
        var ent = Spawn();

        var zLevel = EnsureComp<NCZMapNetworkComponent>(ent);
        EnsureComp<NCPvsOverrideComponent>(ent);

        zLevel.Components = components ?? new ComponentRegistry();

        return (ent, zLevel);
    }

    /// <summary>
    /// Attempts to add the specified maps to the zNetwork network at the specified depths
    /// </summary>
    [PublicAPI]
    public bool TryAddMapsIntoNetwork(Entity<NCZMapNetworkComponent> network, Dictionary<EntityUid, int> maps)
    {
        var success = true;
        foreach (var (mapUid, depth) in maps)
        {
            if (TryGetMapNetwork(mapUid, out var otherNetwork))
            {
                Log.Error($"Failed attempt to add map {mapUid} to ZLevelNetwork {network}: This map is already in another network {otherNetwork}.");
                success = false;
            }

            if (network.Comp.ZLevels.ContainsKey(depth))
            {
                Log.Error($"Failed to add map {mapUid} to ZLevelNetwork {network}: This depth is already occupied.");
                success = false;
            }

            if (network.Comp.ZLevels.ContainsValue(mapUid))
            {
                Log.Error($"Failed attempt to add map {mapUid} to ZLevelNetwork {network} at depth {depth}: This map is already in this network.");
                success = false;
            }

            network.Comp.ZLevels[depth] = mapUid;
            network.Comp.ZLevelByEntity[mapUid] = depth;

            Dirty(network);

            // Welcome to fast api code
            QuickApiCache(network, mapUid, depth);

            var levelMapComponent = EnsureComp<NCZMapComponent>(mapUid);
            levelMapComponent.Depth = depth;
            levelMapComponent.NetworkUid = network;

            if (network.Comp.ZLevels.TryGetValue(depth + 1, out var aboveMapUid))
                levelMapComponent.MapAbove = aboveMapUid;

            if (network.Comp.ZLevels.TryGetValue(depth - 1, out var belowMapUid))
                levelMapComponent.MapBelow = belowMapUid;

            Dirty(mapUid, levelMapComponent);

            var ev = new NCMapAddedIntoZNetworkEvent(network, depth);
            RaiseLocalEvent(mapUid, ref ev);
        }

        RaiseLocalEvent(network, new NCZLevelMapNetworkUpdatedEvent());

        return success;
    }

    /// <summary>
    /// Returns the map entity at a specific depth within a z-network, or false if none exists.
    /// </summary>
    [PublicAPI]
    public bool TryGetMapAtDepth(Entity<NCZMapNetworkComponent?> network, int depth, out EntityUid mapUid)
    {
        mapUid = EntityUid.Invalid;

        if (!Resolve(network, ref network.Comp, false) ||
            !network.Comp.ZLevels.TryGetValue(depth, out var uid) ||
            uid is not { } validUid)
            return false;

        mapUid = validUid;
        return true;
    }

    /// <summary>
    /// Deletes a map z-network: queues deletion of all maps in the network, then the network entity itself.
    /// </summary>
    [PublicAPI]
    public void DeleteMapNetwork(EntityUid networkUid)
    {
        if (!TryComp<NCZMapNetworkComponent>(networkUid, out var zNet))
        {
            Log.Error($"NCZLevelsSystem: entity {networkUid} does not have NCZLevelsNetworkComponent.");
            return;
        }

        foreach (var (_, mapUid) in zNet.ZLevels)
        {
            if (mapUid != null)
                QueueDel(mapUid.Value);
        }

        QueueDel(networkUid);
    }

    private void QuickApiCache(Entity<NCZMapNetworkComponent> network, EntityUid value, int depth)
    {
        var comp = network.Comp;
        var list = comp.SortedZLevels;

        // Zero handling
        if (comp.SortedMin == depth && comp.SortedMax == depth)
        {
            list.Add(value);
            return;
        }

        var min = comp.SortedMin;
        var max = comp.SortedMax;

        if (depth < min)
        {
            var delta = min - depth;
            if (delta == 1)
            {
                list.Insert(0, value);

                comp.SortedMin = depth;
                Dirty(network);
                return;
            }

            list.InsertRange(0, Enumerable.Repeat(EntityUid.Invalid, delta - 1));
            list.Insert(0, value);

            comp.SortedMin = depth;
            Dirty(network);
            return;
        }

        if (depth > max)
        {
            var delta = depth - max;
            if (delta == 1)
            {
                list.Add(value);

                comp.SortedMax = depth;
                Dirty(network);
                return;
            }

            list.AddRange(Enumerable.Repeat(EntityUid.Invalid, delta - 1));
            list.Add(value);

            comp.SortedMax = depth;
            Dirty(network);
            return;
        }

        list[depth - min] = value;
    }
}

/// <summary>
/// Called on ZLevel Network Entity, when maps added or removed from network
/// </summary>
public sealed class NCZLevelMapNetworkUpdatedEvent : EntityEventArgs;

/// <summary>
/// Called on map, when it added to ZNetwork
/// </summary>
[ByRefEvent]
public readonly struct NCMapAddedIntoZNetworkEvent(Entity<NCZMapNetworkComponent> network, int depth)
{
    public readonly Entity<NCZMapNetworkComponent> Network = network;
    public readonly int Depth = depth;
}
