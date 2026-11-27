// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Server._NC.Bank;
using Content.Shared.Buckle.Components;
using Content.Shared.GameTicking;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._NC.Housing;

/// <summary>
/// Home: a tenant marks one lease as the place they enter the round. Every role spawns as usual and is then
/// moved home, by the bed if there is one inside, otherwise just inside the main door. At round start that
/// waits until the properties have been found.
/// </summary>
public sealed partial class NCPropertySystem
{
    private readonly List<(EntityUid Mob, int Profile)> _homeQueue = new();

    private void InitializeHome()
    {
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
    }

    public bool TrySetHome(EntityUid user, string propertyId, bool home, out string error)
    {
        return TryGetPayer(user, out var account, out error) && TrySetHome(account, propertyId, home, out error);
    }

    /// <summary>Marks the tenant's lease as home (clearing any other home of theirs) or clears the mark.</summary>
    public bool TrySetHome(NCBankAccountRecord account, string propertyId, bool home, out string error)
    {
        if (!TryGetOwnLease(account, propertyId, out _, out var lease, out error))
            return false;

        if (home)
        {
            foreach (var other in _leases.Values)
            {
                if (other != lease && other.Home && other.Tenant == lease.Tenant)
                {
                    other.Home = false;
                    Save(other);
                }
            }
        }

        lease.Home = home;
        Save(lease);
        return true;
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        if (!_preferences.TryGetSelectedNCCharacterId(args.Player.UserId, out var character) || !character.IsValid)
            return;

        if (_indexed)
            TrySendHome(args.Mob, character.Value);
        else
            _homeQueue.Add((args.Mob, character.Value));
    }

    private void SendQueuedHome()
    {
        foreach (var (mob, profile) in _homeQueue)
        {
            if (!TerminatingOrDeleted(mob))
                TrySendHome(mob, profile);
        }

        _homeQueue.Clear();
    }

    /// <summary>Moves a character into their home, if they have one.</summary>
    public bool TrySendHome(EntityUid mob, int profileId)
    {
        foreach (var lease in _leases.Values)
        {
            if (!lease.Home || lease.Tenant != profileId ||
                !_properties.TryGetValue(lease.PropertyId, out var property) ||
                GetHomeSpot(property) is not { } spot)
            {
                continue;
            }

            _transform.SetCoordinates(mob, spot);
            _transform.AttachToGridOrMap(mob);
            return true;
        }

        return false;
    }

    /// <summary>Where a resident appears: on a bed inside, else on the floor just inside the first door.</summary>
    public EntityCoordinates? GetHomeSpot(NCPropertyRecord property)
    {
        if (!TryComp<MapGridComponent>(property.Grid, out var grid))
            return null;

        foreach (var tile in property.Tiles)
        {
            var anchored = _map.GetAnchoredEntitiesEnumerator(property.Grid, grid, tile);
            while (anchored.MoveNext(out var uid))
            {
                if (TryComp<StrapComponent>(uid, out var strap) && strap.Position == StrapPosition.Down)
                    return _map.GridTileToLocal(property.Grid, grid, tile);
            }
        }

        foreach (var (door, _) in property.Doors)
        {
            if (TerminatingOrDeleted(door))
                continue;

            var doorTile = _map.TileIndicesFor(property.Grid, grid, Transform(door).Coordinates);
            foreach (var step in new Vector2i[] { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) })
            {
                if (property.Tiles.Contains(doorTile + step))
                    return _map.GridTileToLocal(property.Grid, grid, doorTile + step);
            }
        }

        foreach (var tile in property.Tiles)
            return _map.GridTileToLocal(property.Grid, grid, tile);

        return null;
    }
}
