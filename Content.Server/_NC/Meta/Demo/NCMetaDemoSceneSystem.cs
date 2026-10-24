// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.ObjectModel;
using System.Numerics;
using Content.Server.Atmos.EntitySystems;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Domains;
using Content.Server._NC.Meta.Network;
using Content.Shared.Atmos;
using Content.Shared._NC.Meta.Demo;
using Content.Shared._NC.Meta.Domains;
using Content.Shared._NC.Meta.Network;
using Content.Shared.GameTicking;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Meta.Demo;

public sealed record NCMetaDemoSceneInstance(Guid Id, string Scene, EntityUid Map, EntityUid Grid,
    EntityUid Operator, EntityUid PowerSupply, IReadOnlyDictionary<string, EntityUid> Objects, EntityUid[] Cables);
public sealed record NCMetaDemoSceneStatus(Guid Id, bool HostPowered, bool RoutesReady, int RoutedCables,
    int DataCables, float SupplyJoules, int UnclaimedDevices, int CommissionedWaypoints);

/// <summary>
/// Trusted isolated fixture factory. It spawns actual prototypes, charged APCs and connected physical cables;
/// ownership, grants, patients, automation modes and loaded software still need normal native commissioning.
/// </summary>
public sealed partial class NCMetaDemoSceneSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private ITileDefinitionManager _tiles = default!;
    [Dependency] private SharedTransformSystem _transforms = default!;
    [Dependency] private AtmosphereSystem _atmosphere = default!;
    [Dependency] private NCMetaNetworkSystem _network = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private SharedBatterySystem _batteries = default!;
    private readonly Dictionary<Guid, NCMetaDemoSceneInstance> _scenes = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRestart);
    }

    private void OnRestart(RoundRestartCleanupEvent args)
    {
        foreach (var scene in _scenes.Values) if (Exists(scene.Map)) QueueDel(scene.Map);
        _scenes.Clear();
    }

    public NCMetaDemoSceneInstance[] GetScenes()
    {
        PruneDeletedScenes();
        return _scenes.Values.ToArray();
    }

    public NCMetaDemoSceneStatus? GetStatus(Guid id)
    {
        PruneDeletedScenes();
        if (!_scenes.TryGetValue(id, out var scene) || !scene.Objects.TryGetValue("host", out var host)) return null;
        var routed = scene.Cables.Count(uid => TryComp<NCMetaNetworkCableComponent>(uid, out var cable) &&
            cable.PortA is { } from && cable.PortB is { } to && _network.CanRoute(from, to));
        var unclaimed = scene.Objects.Values.Count(uid => _devices.TryGetEntry(uid, out var device) && device.Owner.Length == 0);
        var waypoints = scene.Objects.Values.Sum(uid => TryComp<NCMetaDroneComponent>(uid, out var drone) ? drone.Waypoints.Count : 0);
        return new(id, _devices.IsPowered(host), routed == scene.Cables.Length, routed, scene.Cables.Length,
            _batteries.GetCharge(scene.PowerSupply), unclaimed, waypoints);
    }

    private void PruneDeletedScenes()
    {
        foreach (var id in _scenes.Where(value => !Exists(value.Value.Map)).Select(value => value.Key).ToArray()) _scenes.Remove(id);
    }

    public bool TryDestroy(Guid id)
    {
        if (!_scenes.Remove(id, out var scene)) return false;
        if (Exists(scene.Map)) QueueDel(scene.Map);
        return true;
    }

    /// <summary>Called only by a trusted native fixture/admin entry point; there is no guest or client spawn message.</summary>
    public bool TryCreate(ProtoId<NCMetaDemoScenePrototype> id, out NCMetaDemoSceneInstance scene)
    {
        scene = default!;
        PruneDeletedScenes();
        if (_scenes.Count >= 8 || !_prototypes.TryIndex(id, out var definition) || !IsValid(definition)) return false;
        var map = _maps.CreateMap();
        try
        {
            var grid = _maps.CreateGridEntity(map);
            var tile = new Tile(_tiles["FloorSteel"].TileId);
            // 121 floor tiles and 121 real LV cable segments are a fixed bounded fixture footprint.
            for (var x = -5; x <= 5; x++)
            for (var y = -5; y <= 5; y++)
            {
                _maps.SetTile(grid, new Vector2i(x, y), tile);
                Spawn("CableApcExtension", new EntityCoordinates(grid.Owner, new Vector2(x + 0.5f, y + 0.5f)));
            }
            _maps.SetAmbientLight(Transform(map).MapID, Color.White);
            var air = new GasMixture(Atmospherics.CellVolume) { Temperature = Atmospherics.T20C };
            air.AdjustMoles(Gas.Oxygen, Atmospherics.OxygenMolesStandard);
            air.AdjustMoles(Gas.Nitrogen, Atmospherics.NitrogenMolesStandard);
            _atmosphere.SetMapAtmosphere(map, false, air);
            var power = Spawn("APCHyperCapacity", new EntityCoordinates(grid.Owner, new Vector2(-4.5f, -4.5f)));
            Spawn("Poweredlight", new EntityCoordinates(grid.Owner, new Vector2(0.5f, 4.5f)));
            var actor = Spawn("MobHuman", new EntityCoordinates(grid.Owner, new Vector2(0.5f, -1.5f)));
            var objects = new Dictionary<string, EntityUid>(StringComparer.Ordinal);
            foreach (var item in definition.Objects)
                objects.Add(item.Key, Spawn(item.Prototype, new EntityCoordinates(grid.Owner, item.Offset + new Vector2(0.5f, 0.5f))));
            var cables = new List<EntityUid>();
            foreach (var pair in definition.Cables)
            {
                var from = objects[pair.From]; var to = objects[pair.To];
                if (!_devices.TryGetEntry(from, out _) || !_devices.TryGetEntry(to, out _))
                    throw new InvalidOperationException("A demo data cable endpoint has no native device identity.");
                var midpoint = (Transform(from).Coordinates.Position + Transform(to).Coordinates.Position) / 2;
                _transforms.SetCoordinates(actor, new EntityCoordinates(grid.Owner, midpoint));
                var cable = Spawn("NCMetaNetworkCable", new EntityCoordinates(grid.Owner, midpoint));
                if (!_network.TryConnectCable(cable, actor, from) || !_network.TryConnectCable(cable, actor, to))
                    throw new InvalidOperationException("Native physical demo cable connection rejected.");
                cables.Add(cable);
            }
            _transforms.SetCoordinates(actor, new EntityCoordinates(grid.Owner, new Vector2(0.5f, -1.5f)));
            scene = new(Guid.NewGuid(), definition.ID, map, grid.Owner, actor, power,
                new ReadOnlyDictionary<string, EntityUid>(objects), cables.ToArray());
            _scenes.Add(scene.Id, scene);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Log.Error($"Isolated META scene {id} failed: {exception}");
            QueueDel(map);
            return false;
        }
    }

    private bool IsValid(NCMetaDemoScenePrototype scene) => scene.Objects.Count is > 0 and <= 32 && scene.Cables.Count <= 32 &&
        scene.Objects.Count + scene.Cables.Count <= 30 &&
        scene.Templates.Length <= 16 && scene.Objects.All(value => value.Key.Length is > 0 and <= 64 &&
            float.IsFinite(value.Offset.X) && float.IsFinite(value.Offset.Y) && Math.Abs(value.Offset.X) <= 4 && Math.Abs(value.Offset.Y) <= 4 &&
            _prototypes.HasIndex(value.Prototype)) &&
        scene.Objects.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() == scene.Objects.Count &&
        scene.Cables.All(value => value.From != value.To && scene.Objects.Any(item => item.Key == value.From) &&
            scene.Objects.Any(item => item.Key == value.To));
}
