// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.Server._NC.Bank;
using Content.Server._NC.City.Atlas;
using Content.Server._NC.LivingCity.Time;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.Housing;
using Content.Shared._NC.Persistence.Time;
using Content.Server.Atmos.Components;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Popups;
using Content.Shared.Tag;
using Robust.Server.Player;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Housing;

/// <summary>A rentable property found on the map.</summary>
public sealed class NCPropertyRecord
{
    public string Id = string.Empty;
    public string Name = string.Empty;
    public int RentPerHour;
    public EntityUid Grid;
    public readonly HashSet<Vector2i> Tiles = new();

    /// <summary>The doors that lock, in a stable order, with the key printed on their cards.</summary>
    public readonly List<(EntityUid Door, string Key)> Doors = new();

    public string District = string.Empty;
    public string Building = string.Empty;
    public int Floor;
}

/// <summary>The lease of a property; a property without a tenant is vacant.</summary>
public sealed class NCPropertyLease
{
    public string PropertyId = string.Empty;
    public int? Tenant;
    public NCActiveWorldTime PaidUntil;
    public int KeyVersion;
    public bool Warned;
}

/// <summary>
/// Rentable properties: apartments, garages, restaurants, ripperdoc clinics. Mappers mark them; this system finds
/// their floor and doors, keeps the leases in the database and locks the doors to key cards of the current lease.
/// Rent is prepaid in hours of active world time; when it runs out the tenant is evicted and the locks change.
/// Whatever was left inside stays there.
/// </summary>
public sealed partial class NCPropertySystem : EntitySystem
{
    public static readonly int[] Terms = [2, 6, 12, 24];
    public static readonly EntProtoId CardPrototype = "NCPropertyKeyCard";
    private static readonly TimeSpan IndexDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WarningTime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DeniedPopupCooldown = TimeSpan.FromSeconds(2);
    private static readonly ProtoId<TagPrototype> WallTag = "Wall";

    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private NCActiveWorldTimeSystem _worldTime = default!;
    [Dependency] private NCBankSystem _bank = default!;
    [Dependency] private NCCityAtlasSystem _atlas = default!;
    [Dependency] private NCMapCoordinatesSystem _mapCoordinates = default!;
    [Dependency] private MetaDataSystem _metaData = default!;

    private readonly Dictionary<string, NCPropertyRecord> _properties = new();
    private readonly Dictionary<string, NCPropertyLease> _leases = new();
    private readonly Dictionary<EntityUid, TimeSpan> _deniedPopups = new();
    private bool _indexed;
    private bool _leasesLoaded;
    private TimeSpan _indexAt;
    private TimeSpan _nextCheck;

    /// <summary>Leases load from the database and the world clock runs.</summary>
    public bool Ready => _leasesLoaded && _worldTime.IsReady;

    public IReadOnlyDictionary<string, NCPropertyRecord> Properties
    {
        get
        {
            EnsureIndexed();
            return _properties;
        }
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<NCPropertyDoorComponent, BeforeDoorOpenedEvent>(OnBeforeDoorOpened);
        SubscribeLocalEvent<NCPropertyCardComponent, ExaminedEvent>(OnCardExamined);
        InitializeAgency();
        LoadLeases();
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _properties.Clear();
        _deniedPopups.Clear();
        _indexed = false;
        _indexAt = TimeSpan.Zero;
        foreach (var lease in _leases.Values)
            lease.Warned = false;
    }

    private async void LoadLeases()
    {
        try
        {
            foreach (var row in await _db.GetNCPropertiesAsync())
            {
                _leases[row.PropertyId] = new NCPropertyLease
                {
                    PropertyId = row.PropertyId,
                    Tenant = row.TenantProfileId,
                    PaidUntil = new NCActiveWorldTime(row.PaidUntil),
                    KeyVersion = row.KeyVersion,
                };
            }

            _leasesLoaded = true;
        }
        catch (Exception exception)
        {
            Log.Error($"Unable to load property leases: {exception}");
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        // Give the round a moment to restore the saved city before the doors are looked at.
        if (!_indexed && _ticker.RunLevel == GameRunLevel.InRound)
        {
            if (_indexAt == TimeSpan.Zero)
                _indexAt = now + IndexDelay;
            else if (now >= _indexAt)
                EnsureIndexed();
        }

        if (now < _nextCheck)
            return;

        _nextCheck = now + CheckInterval;
        if (Ready && _worldTime.TryGetCurrentTime(out _, out var worldNow))
            CheckLeases(worldNow);
    }

    #region Finding properties

    /// <summary>Finds the properties on the map once per round and marks their doors.</summary>
    public void EnsureIndexed()
    {
        if (_indexed)
            return;

        _indexed = true;
        _properties.Clear();

        var doorQuery = EntityQueryEnumerator<NCPropertyDoorComponent>();
        while (doorQuery.MoveNext(out var uid, out _))
            RemComp<NCPropertyDoorComponent>(uid);

        var markers = new Dictionary<string, List<Entity<NCPropertyMarkerComponent>>>();
        var markerQuery = EntityQueryEnumerator<NCPropertyMarkerComponent>();
        while (markerQuery.MoveNext(out var uid, out var marker))
        {
            if (string.IsNullOrWhiteSpace(marker.Id))
                continue;

            if (!markers.TryGetValue(marker.Id, out var list))
                markers[marker.Id] = list = new List<Entity<NCPropertyMarkerComponent>>();
            list.Add((uid, marker));
        }

        foreach (var (id, list) in markers)
        {
            if (TryBuild(id, list, out var property))
                _properties[id] = property;
        }
    }

    private bool TryBuild(string id, List<Entity<NCPropertyMarkerComponent>> markers, out NCPropertyRecord property)
    {
        property = new NCPropertyRecord { Id = id };
        if (Transform(markers[0]).GridUid is not { } grid || !TryComp<MapGridComponent>(grid, out var gridComp))
        {
            Log.Warning($"Property {id} has a marker off any grid.");
            return false;
        }

        var index = new NCPropertyIndex(tile => ClassifyTile(grid, gridComp, tile));
        var doors = new Dictionary<Vector2i, EntityUid>();
        foreach (var marker in markers)
        {
            var xform = Transform(marker);
            if (xform.GridUid != grid)
            {
                Log.Warning($"Property {id} has markers on different grids; only one grid is used.");
                continue;
            }

            var start = _map.TileIndicesFor(grid, gridComp, xform.Coordinates);
            if (!index.TryFill(start, NCPropertyIndex.MaximumTiles, out var tiles, out var found))
            {
                Log.Warning($"Property {id}: the marker at {start} is not enclosed by walls and doors.");
                continue;
            }

            property.Tiles.UnionWith(tiles);
            foreach (var (tile, door) in found)
                doors[tile] = door;
        }

        if (property.Tiles.Count == 0)
            return false;

        var outer = index.AddBackRooms(property.Tiles, doors);
        var floor = _mapCoordinates.TryGetCoordinates(markers[0], out var coordinates) ? coordinates.Z : 0;
        foreach (var (tile, door) in outer.OrderBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.X))
        {
            var key = $"{floor}:{tile.X},{tile.Y}";
            property.Doors.Add((door, key));
            var component = EnsureComp<NCPropertyDoorComponent>(door);
            component.PropertyId = id;
            component.DoorKey = key;
        }

        var first = markers[0].Comp;
        property.Grid = grid;
        property.Name = markers.Select(marker => marker.Comp.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? id;
        property.RentPerHour = markers.Select(marker => marker.Comp.RentPerHour).FirstOrDefault(rent => rent > 0, first.RentPerHour);
        property.Floor = floor;
        _atlas.GetLocation(markers[0], out var district, out var building);
        property.District = district ?? string.Empty;
        property.Building = building ?? string.Empty;
        return true;
    }

    private (NCPropertyTileKind Kind, EntityUid? Door) ClassifyTile(EntityUid grid, MapGridComponent gridComp, Vector2i tile)
    {
        if (!_map.TryGetTileRef(grid, gridComp, tile, out var tileRef) || tileRef.Tile.IsEmpty)
            return (NCPropertyTileKind.Empty, null);

        var wall = false;
        var anchored = _map.GetAnchoredEntitiesEnumerator(grid, gridComp, tile);
        while (anchored.MoveNext(out var anchoredUid))
        {
            if (anchoredUid is not { } uid)
                continue;

            // Firelocks sit in doorways and corridors alike; they are not the doors anyone locks.
            var firelock = HasComp<FirelockComponent>(uid);
            if (HasComp<DoorComponent>(uid) && !firelock)
                return (NCPropertyTileKind.Door, uid);

            if (HasComp<AirtightComponent>(uid) && !firelock || _tags.HasTag(uid, WallTag))
                wall = true;
        }

        return (wall ? NCPropertyTileKind.Wall : NCPropertyTileKind.Floor, null);
    }

    #endregion

    #region Leases

    public NCPropertyLease GetLease(string propertyId)
    {
        if (!_leases.TryGetValue(propertyId, out var lease))
            _leases[propertyId] = lease = new NCPropertyLease { PropertyId = propertyId };
        return lease;
    }

    /// <summary>Time left on a lease, zero if vacant or expired.</summary>
    public TimeSpan GetRemaining(NCPropertyLease lease)
    {
        if (lease.Tenant == null || !_worldTime.TryGetCurrentTime(out _, out var now))
            return TimeSpan.Zero;

        var left = lease.PaidUntil.Elapsed - now.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>Rents a vacant property: rent for the term is paid up front and a card per door is handed over.</summary>
    public bool TryRent(EntityUid user, string propertyId, int hours, out string error)
    {
        if (!CanPay(user, propertyId, hours, out var property, out var account, out var now, out error))
            return false;

        var lease = GetLease(propertyId);
        if (lease.Tenant != null)
        {
            error = "nc-property-error-taken";
            return false;
        }

        if (!_bank.TryPayRent(account, property.RentPerHour * (long) hours, propertyId, user))
        {
            error = "nc-property-error-funds";
            return false;
        }

        lease.Tenant = account.ProfileId;
        lease.PaidUntil = now.Advance(TimeSpan.FromHours(hours));
        lease.KeyVersion++;
        lease.Warned = false;
        Save(lease);
        IssueCards(user, property, lease);
        return true;
    }

    /// <summary>Prepays more hours on the user's own lease.</summary>
    public bool TryExtend(EntityUid user, string propertyId, int hours, out string error)
    {
        if (!CanPay(user, propertyId, hours, out var property, out var account, out var now, out error))
            return false;

        var lease = GetLease(propertyId);
        if (lease.Tenant == null || lease.Tenant != account.ProfileId)
        {
            error = "nc-property-error-not-yours";
            return false;
        }

        if (!_bank.TryPayRent(account, property.RentPerHour * (long) hours, propertyId, user))
        {
            error = "nc-property-error-funds";
            return false;
        }

        var from = lease.PaidUntil > now ? lease.PaidUntil : now;
        lease.PaidUntil = from.Advance(TimeSpan.FromHours(hours));
        lease.Warned = false;
        Save(lease);
        return true;
    }

    /// <summary>Gives the property up. Prepaid rent is not returned; the locks change.</summary>
    public bool TryCancel(EntityUid user, string propertyId, out string error)
    {
        error = string.Empty;
        if (!Ready || !_bank.TryGetCharacterAccount(user, out var account) || account.ProfileId == null)
        {
            error = "nc-property-error-no-account";
            return false;
        }

        var lease = GetLease(propertyId);
        if (lease.Tenant == null || lease.Tenant != account.ProfileId)
        {
            error = "nc-property-error-not-yours";
            return false;
        }

        Vacate(lease);
        return true;
    }

    private bool CanPay(
        EntityUid user,
        string propertyId,
        int hours,
        out NCPropertyRecord property,
        out NCBankAccountRecord account,
        out NCActiveWorldTime now,
        out string error)
    {
        property = default!;
        account = default!;
        now = default;
        error = string.Empty;
        if (!Ready || !_worldTime.TryGetCurrentTime(out _, out now))
        {
            error = "nc-property-error-closed";
            return false;
        }

        if (!Properties.TryGetValue(propertyId, out property!) || property.RentPerHour <= 0)
        {
            error = "nc-property-error-unknown";
            return false;
        }

        if (!Terms.Contains(hours))
        {
            error = "nc-property-error-term";
            return false;
        }

        if (!_bank.TryGetCharacterAccount(user, out account) || account.ProfileId == null)
        {
            error = "nc-property-error-no-account";
            return false;
        }

        return true;
    }

    private void Vacate(NCPropertyLease lease)
    {
        lease.Tenant = null;
        lease.PaidUntil = NCActiveWorldTime.Zero;
        lease.KeyVersion++;
        lease.Warned = false;
        Save(lease);
    }

    /// <summary>Evicts tenants whose prepaid time is over and warns those about to run out.</summary>
    public void CheckLeases(NCActiveWorldTime now)
    {
        foreach (var lease in _leases.Values)
        {
            if (lease.Tenant is not { } tenant)
                continue;

            var name = _properties.TryGetValue(lease.PropertyId, out var property) ? property.Name : lease.PropertyId;
            if (lease.PaidUntil <= now)
            {
                Vacate(lease);
                Notify(tenant, Loc.GetString("nc-property-evicted", ("property", name)));
            }
            else if (!lease.Warned && lease.PaidUntil.Elapsed - now.Elapsed <= WarningTime)
            {
                lease.Warned = true;
                Notify(tenant, Loc.GetString("nc-property-expiring", ("property", name)));
            }
        }
    }

    private async void Save(NCPropertyLease lease)
    {
        try
        {
            await _db.SetNCPropertyAsync(new NCPropertyData(lease.PropertyId, lease.Tenant, lease.PaidUntil.Ticks, lease.KeyVersion));
        }
        catch (Exception exception)
        {
            Log.Error($"Unable to save the lease of property {lease.PropertyId}: {exception}");
        }
    }

    private void Notify(int profileId, string message)
    {
        foreach (var session in _players.Sessions)
        {
            if (_preferences.TryGetSelectedNCCharacterId(session.UserId, out var character) && character.IsValid &&
                character.Value == profileId)
            {
                _chat.DispatchServerMessage(session, message);
            }
        }
    }

    #endregion

    #region Cards and doors

    private void IssueCards(EntityUid user, NCPropertyRecord property, NCPropertyLease lease)
    {
        var coordinates = Transform(user).Coordinates;
        for (var i = 0; i < property.Doors.Count; i++)
        {
            var card = Spawn(CardPrototype, coordinates);
            var component = EnsureComp<NCPropertyCardComponent>(card);
            component.PropertyId = property.Id;
            component.DoorKey = property.Doors[i].Key;
            component.KeyVersion = lease.KeyVersion;
            _metaData.SetEntityName(card, Loc.GetString("nc-property-card-name",
                ("property", property.Name), ("door", i + 1), ("doors", property.Doors.Count)));
            _hands.TryPickupAnyHand(user, card);
        }
    }

    private void OnBeforeDoorOpened(Entity<NCPropertyDoorComponent> ent, ref BeforeDoorOpenedEvent args)
    {
        if (args.Cancelled || string.IsNullOrEmpty(ent.Comp.PropertyId) ||
            !_properties.TryGetValue(ent.Comp.PropertyId, out var property))
        {
            return;
        }

        if (args.User is { } user && CanOpen(user, property, ent.Comp.DoorKey))
            return;

        args.Cancel();
        if (args.User is { } denied && _timing.CurTime >= _deniedPopups.GetValueOrDefault(denied))
        {
            _deniedPopups[denied] = _timing.CurTime + DeniedPopupCooldown;
            _popup.PopupEntity(Loc.GetString("nc-property-door-locked"), ent, denied);
        }
    }

    /// <summary>Whether the user may open a property door: from inside always, from outside with a current card.</summary>
    public bool CanOpen(EntityUid user, NCPropertyRecord property, string doorKey)
    {
        var xform = Transform(user);
        if (xform.GridUid == property.Grid && TryComp<MapGridComponent>(property.Grid, out var grid) &&
            property.Tiles.Contains(_map.TileIndicesFor(property.Grid, grid, xform.Coordinates)))
        {
            return true;
        }

        var lease = GetLease(property.Id);
        if (lease.Tenant == null)
            return false;

        foreach (var item in _inventory.GetHandOrInventoryEntities(user))
        {
            if (IsKey(item, property.Id, doorKey, lease.KeyVersion))
                return true;

            // Cards tucked into a PDA or wallet still count.
            if (!TryComp<ContainerManagerComponent>(item, out var containers))
                continue;

            foreach (var container in _containers.GetAllContainers(item, containers))
            {
                foreach (var inner in container.ContainedEntities)
                {
                    if (IsKey(inner, property.Id, doorKey, lease.KeyVersion))
                        return true;
                }
            }
        }

        return false;
    }

    private bool IsKey(EntityUid item, string propertyId, string doorKey, int keyVersion)
    {
        return TryComp<NCPropertyCardComponent>(item, out var card) &&
               card.PropertyId == propertyId &&
               card.DoorKey == doorKey &&
               card.KeyVersion == keyVersion;
    }

    private void OnCardExamined(Entity<NCPropertyCardComponent> ent, ref ExaminedEvent args)
    {
        if (string.IsNullOrEmpty(ent.Comp.PropertyId))
            return;

        var name = Properties.TryGetValue(ent.Comp.PropertyId, out var property) ? property.Name : ent.Comp.PropertyId;
        args.PushMarkup(Loc.GetString("nc-property-card-examine", ("property", name)));
    }

    #endregion
}
