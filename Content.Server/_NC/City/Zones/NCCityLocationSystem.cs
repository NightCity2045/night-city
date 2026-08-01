// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Components;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.ZLevels.Core.EntitySystems;

namespace Content.Server._NC.City.Zones;

/// <summary>
/// Maintains opt-in entity location caches from movement events.
/// There is no Update loop and no scan over all city entities.
/// </summary>
public sealed partial class NCCityLocationSystem : EntitySystem
{
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    private readonly HashSet<EntityUid> _tracked = [];

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCCityLocationComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<NCCityLocationComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCCityLocationComponent, MoveEvent>(OnMove);
        SubscribeLocalEvent<NCCityLocationComponent, NCZLevelMapMoveEvent>(OnZLevelMove);
        SubscribeLocalEvent<NCZoneIndexRebuiltEvent>(OnZoneIndexRebuilt);
    }

    public void EnsureTracked(EntityUid uid)
    {
        var component = EnsureComp<NCCityLocationComponent>(uid);
        Refresh((uid, component));
    }

    public bool TryGetContext(EntityUid uid, out NCCityLocationContext context)
    {
        if (TryComp<NCCityLocationComponent>(uid, out var component))
        {
            context = component.Context;
            return context.IsValid;
        }

        context = default;
        return false;
    }

    /// <summary>
    /// Returns typed city identities without exposing the entity's cache component.
    /// Empty levels are reported as unsuccessful lookups.
    /// </summary>
    public bool TryGetDistrict(EntityUid uid, out NCDistrictId districtId)
    {
        districtId = TryGetContext(uid, out var context) ? context.DistrictId : default;
        return districtId.IsValid;
    }

    public bool TryGetSector(EntityUid uid, out NCSectorId sectorId)
    {
        sectorId = TryGetContext(uid, out var context) ? context.SectorId : default;
        return sectorId.IsValid;
    }

    public bool TryGetStreet(EntityUid uid, out NCStreetId streetId)
    {
        streetId = TryGetContext(uid, out var context) ? context.StreetId : default;
        return streetId.IsValid;
    }

    public bool TryGetBuilding(EntityUid uid, out NCBuildingId buildingId)
    {
        buildingId = TryGetContext(uid, out var context) ? context.BuildingId : default;
        return buildingId.IsValid;
    }

    public bool TryGetApartment(EntityUid uid, out NCApartmentId apartmentId)
    {
        apartmentId = TryGetContext(uid, out var context) ? context.ApartmentId : default;
        return apartmentId.IsValid;
    }

    public bool Refresh(EntityUid uid)
    {
        if (!TryComp<NCCityLocationComponent>(uid, out var component))
            return false;

        return Refresh((uid, component));
    }

    private bool Refresh(Entity<NCCityLocationComponent> entity)
    {
        var next = _coordinates.TryGetCoordinates(entity.Owner, out var coordinates)
            ? _zones.GetLocationContext(coordinates)
            : default;

        if (entity.Comp.Context == next)
            return false;

        var previous = entity.Comp.Context;
        entity.Comp.Context = next;
        Dirty(entity);

        var ev = new NCZoneChangedEvent(previous, next);
        RaiseLocalEvent(entity.Owner, ref ev);
        return true;
    }

    private void OnStartup(Entity<NCCityLocationComponent> entity, ref ComponentStartup args)
    {
        _tracked.Add(entity.Owner);
        Refresh(entity);
    }

    private void OnShutdown(Entity<NCCityLocationComponent> entity, ref ComponentShutdown args)
    {
        _tracked.Remove(entity.Owner);
    }

    private void OnMove(Entity<NCCityLocationComponent> entity, ref MoveEvent args)
    {
        Refresh(entity);
    }

    private void OnZLevelMove(Entity<NCCityLocationComponent> entity, ref NCZLevelMapMoveEvent args)
    {
        // The explicit Z event guarantees refresh even if an engine transform move is coalesced.
        Refresh(entity);
    }

    private void OnZoneIndexRebuilt(ref NCZoneIndexRebuiltEvent args)
    {
        // Only explicitly tracked entities are revisited; this is not a global entity scan.
        foreach (var uid in _tracked.ToArray())
            Refresh(uid);
    }
}
