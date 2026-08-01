// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Coordinates.Components;
using Content.Shared._NC.ZLevels.Core.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Shared._NC.Coordinates.Systems;

/// <summary>
/// Resolves runtime engine coordinates against stable Night City Z-network coordinates.
/// Public queries only expose identifiers and coordinates, never component references.
/// </summary>
public sealed partial class NCMapCoordinatesSystem : EntitySystem
{
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    [Dependency] private EntityQuery<MapComponent> _mapQuery = default!;
    [Dependency] private EntityQuery<NCZMapComponent> _zMapQuery = default!;
    [Dependency] private EntityQuery<NCZMapNetworkComponent> _zNetworkQuery = default!;
    // These indices avoid global entity scans when persistent coordinates are resolved.
    private readonly Dictionary<NCZNetworkId, EntityUid> _networksById = new();
    private readonly Dictionary<EntityUid, NCZNetworkId> _idsByNetwork = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCZNetworkIdentityComponent, ComponentStartup>(OnIdentityStartup);
        SubscribeLocalEvent<NCZNetworkIdentityComponent, ComponentShutdown>(OnIdentityShutdown);
    }

    private void OnIdentityStartup(Entity<NCZNetworkIdentityComponent> ent, ref ComponentStartup args)
    {
        var networkId = ent.Comp.NetworkId;
        if (!networkId.IsValid)
        {
            Log.Error($"Z-network entity {ToPrettyString(ent)} has an empty NCZNetworkId and cannot be registered.");
            return;
        }

        if (_networksById.TryGetValue(networkId, out var existing) && existing != ent.Owner)
        {
            Log.Error(
                $"Duplicate NCZNetworkId {networkId} on {ToPrettyString(ent)}; already owned by {ToPrettyString(existing)}.");
            return;
        }

        _networksById[networkId] = ent.Owner;
        _idsByNetwork[ent.Owner] = networkId;
    }

    private void OnIdentityShutdown(Entity<NCZNetworkIdentityComponent> ent, ref ComponentShutdown args)
    {
        if (!_idsByNetwork.Remove(ent.Owner, out var networkId))
            return;

        if (_networksById.TryGetValue(networkId, out var registered) && registered == ent.Owner)
            _networksById.Remove(networkId);
    }

    /// <summary>
    /// Returns whether an identity is already assigned to a live runtime network.
    /// </summary>
    public bool ContainsNetwork(NCZNetworkId networkId)
    {
        return _networksById.ContainsKey(networkId);
    }

    /// <summary>
    /// Finds the runtime Z-network entity without exposing its components.
    /// </summary>
    public bool TryGetNetwork(NCZNetworkId networkId, out EntityUid networkUid)
    {
        if (_networksById.TryGetValue(networkId, out networkUid) &&
            !TerminatingOrDeleted(networkUid) &&
            _zNetworkQuery.HasComp(networkUid))
        {
            return true;
        }

        networkUid = EntityUid.Invalid;
        return false;
    }

    /// <summary>
    /// Gets the stable identity assigned to a runtime Z-network.
    /// </summary>
    public bool TryGetNetworkId(EntityUid networkUid, out NCZNetworkId networkId)
    {
        return _idsByNetwork.TryGetValue(networkUid, out networkId);
    }

    /// <summary>
    /// Captures an entity's current map-space position.
    /// SharedTransformSystem applies all parent grid translations and rotations.
    /// </summary>
    public bool TryGetCoordinates(EntityUid uid, out NCMapCoordinates coordinates)
    {
        coordinates = default;
        if (TerminatingOrDeleted(uid))
            return false;

        return TryConvert(_transform.GetMapCoordinates(uid), out coordinates);
    }

    /// <summary>
    /// Converts engine map coordinates into persistent city coordinates.
    /// </summary>
    public bool TryConvert(MapCoordinates mapCoordinates, out NCMapCoordinates coordinates)
    {
        coordinates = default;

        if (!_maps.TryGetMap(mapCoordinates.MapId, out var mapUid) ||
            mapUid is not { } map ||
            !_zMapQuery.TryComp(map, out var zMap) ||
            !_idsByNetwork.TryGetValue(zMap.NetworkUid, out var networkId))
        {
            return false;
        }

        coordinates = new NCMapCoordinates(networkId, mapCoordinates.Position, zMap.Depth);
        return true;
    }

    /// <summary>
    /// Resolves persistent city coordinates against the currently loaded map at their depth.
    /// </summary>
    public bool TryResolve(NCMapCoordinates coordinates, out MapCoordinates mapCoordinates)
    {
        mapCoordinates = default;

        if (!coordinates.NetworkId.IsValid ||
            !float.IsFinite(coordinates.Position.X) ||
            !float.IsFinite(coordinates.Position.Y) ||
            !TryGetNetwork(coordinates.NetworkId, out var networkUid) ||
            !_zNetworkQuery.TryComp(networkUid, out var network) ||
            !network.ZLevels.TryGetValue(coordinates.Z, out var mapUid) ||
            mapUid is not { } map ||
            !_mapQuery.TryComp(map, out var mapComponent))
        {
            return false;
        }

        mapCoordinates = new MapCoordinates(coordinates.Position, mapComponent.MapId);
        return true;
    }

    /// <summary>
    /// Resolves a stable network and depth to the current map entity.
    /// </summary>
    public bool TryGetMap(NCZNetworkId networkId, int z, out EntityUid mapUid)
    {
        mapUid = EntityUid.Invalid;
        if (!TryGetNetwork(networkId, out var networkUid) ||
            !_zNetworkQuery.TryComp(networkUid, out var network) ||
            !network.ZLevels.TryGetValue(z, out var found) ||
            found is not { } map ||
            TerminatingOrDeleted(map))
        {
            return false;
        }

        mapUid = map;
        return true;
    }
}
