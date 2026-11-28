/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Linq;
using Content.Server._NC.ZLevels.PVS;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Components;
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
        return CreateMapNetwork(NCZNetworkId.NewTransient(), components);
    }

    /// <summary>
    /// Creates a new zLevel Map Network entity with an explicit persistent identity.
    /// </summary>
    [PublicAPI]
    public Entity<NCZMapNetworkComponent> CreateMapNetwork(
        NCZNetworkId networkId,
        ComponentRegistry? components = null)
    {
        if (!networkId.IsValid)
            throw new ArgumentException("A Z-level network requires a non-empty NCZNetworkId.", nameof(networkId));

        if (_ncCoordinates.ContainsNetwork(networkId))
            throw new InvalidOperationException($"NCZNetworkId {networkId} is already registered.");

        var ent = Spawn();

        var zLevel = EnsureComp<NCZMapNetworkComponent>(ent);
        EnsureComp<NCPvsOverrideComponent>(ent);

        zLevel.Components = components ?? new ComponentRegistry();

        // Supply the identity before component startup so the shared registry never observes an empty ID.
        AddComp(ent, new NCZNetworkIdentityComponent
        {
            NetworkId = networkId,
        });

        return (ent, zLevel);
    }

    /// <summary>
    /// Attempts to add the specified maps to the zNetwork network at the specified depths
    /// </summary>
    [PublicAPI]
    public bool TryAddMapsIntoNetwork(Entity<NCZMapNetworkComponent> network, Dictionary<EntityUid, int> maps)
    {
        // NC start: validate the complete batch before mutating the network so a rejected load cannot leave
        // a partially populated Z-level dictionary or sorted lookup cache behind.
        var success = true;
        var requestedDepths = new HashSet<int>();
        foreach (var (mapUid, depth) in maps)
        {
            if (TryGetMapNetwork(mapUid, out var otherNetwork))
            {
                Log.Error($"Failed attempt to add map {mapUid} to ZLevelNetwork {network}: This map is already in another network {otherNetwork}.");
                success = false;
            }

            if (network.Comp.ZLevels.ContainsKey(depth) || !requestedDepths.Add(depth))
            {
                Log.Error($"Failed to add map {mapUid} to ZLevelNetwork {network}: This depth is already occupied.");
                success = false;
            }

            if (network.Comp.ZLevels.ContainsValue(mapUid))
            {
                Log.Error($"Failed attempt to add map {mapUid} to ZLevelNetwork {network} at depth {depth}: This map is already in this network.");
                success = false;
            }
        }

        if (!success)
            return false;
        // NC end

        foreach (var (mapUid, depth) in maps)
        {
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

    // NC start: a level map that goes away must leave its network. The network replicates its maps, so a
    // deleted map left in it breaks every state the server sends for the network and its neighbours.
    private void OnZMapShutdown(Entity<NCZMapComponent> ent, ref ComponentShutdown args)
    {
        var networkUid = ent.Comp.NetworkUid;
        if (TerminatingOrDeleted(networkUid) || !TryComp<NCZMapNetworkComponent>(networkUid, out var network))
            return;

        RemoveMapFromNetwork((networkUid, network), ent.Owner);
    }

    /// <summary>
    /// Removes a map from its z-level network and unlinks the levels above and below it.
    /// </summary>
    [PublicAPI]
    public bool RemoveMapFromNetwork(Entity<NCZMapNetworkComponent> network, EntityUid mapUid)
    {
        if (!network.Comp.ZLevelByEntity.Remove(mapUid, out var depth))
            return false;

        network.Comp.ZLevels.Remove(depth);
        RemoveFromQuickApiCache(network.Comp, mapUid, depth);
        Dirty(network);

        if (network.Comp.ZLevels.TryGetValue(depth + 1, out var above) && above is { } aboveUid &&
            !TerminatingOrDeleted(aboveUid) && TryComp<NCZMapComponent>(aboveUid, out var aboveMap) &&
            aboveMap.MapBelow == mapUid)
        {
            aboveMap.MapBelow = null;
            Dirty(aboveUid, aboveMap);
        }

        if (network.Comp.ZLevels.TryGetValue(depth - 1, out var below) && below is { } belowUid &&
            !TerminatingOrDeleted(belowUid) && TryComp<NCZMapComponent>(belowUid, out var belowMap) &&
            belowMap.MapAbove == mapUid)
        {
            belowMap.MapAbove = null;
            Dirty(belowUid, belowMap);
        }

        RaiseLocalEvent(network, new NCZLevelMapNetworkUpdatedEvent());
        return true;
    }

    private static void RemoveFromQuickApiCache(NCZMapNetworkComponent comp, EntityUid value, int depth)
    {
        var list = comp.SortedZLevels;
        var index = depth - comp.SortedMin;
        if (index < 0 || index >= list.Count || list[index] != value)
            return;

        // Gaps inside the range stay as invalid placeholders, like the ones QuickApiCache inserts.
        list[index] = EntityUid.Invalid;

        while (list.Count > 0 && !list[0].IsValid())
        {
            list.RemoveAt(0);
            comp.SortedMin++;
        }

        while (list.Count > 0 && !list[^1].IsValid())
        {
            list.RemoveAt(list.Count - 1);
            comp.SortedMax--;
        }

        if (list.Count == 0)
        {
            comp.SortedMin = 0;
            comp.SortedMax = 0;
        }
    }
    // NC end

    private void QuickApiCache(Entity<NCZMapNetworkComponent> network, EntityUid value, int depth)
    {
        var comp = network.Comp;
        var list = comp.SortedZLevels;

        // NC start: default min/max values do not mean that Z=0 is already present. Seed an empty cache from
        // the first actual map so loading bottom-up, top-down, or without a ground floor is equally valid.
        if (list.Count == 0)
        {
            list.Add(value);
            comp.SortedMin = depth;
            comp.SortedMax = depth;
            Dirty(network);
            return;
        }
        // NC end

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
