// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.CityServices;

/// <summary>
/// Event-fed district safety read model. It never scans ECS entities; only bounded persistent incident and dispatch
/// projections are recomputed for the affected district.
/// </summary>
public sealed partial class NCDistrictSafetyProjectionSystem : EntitySystem
{
    private static readonly ProtoId<NCDistrictSafetyPolicyPrototype> DefaultPolicy = "Default";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCTerritoryProjectionSystem _territory = default!;

    private readonly Dictionary<NCCityIncidentId, NCCityIncidentSnapshot> _incidents = [];
    private readonly Dictionary<NCServiceDispatchId, NCServiceDispatchSnapshot> _dispatches = [];
    private readonly Dictionary<NCZoneId, NCDistrictSafetyProjection> _districts = [];
    private long _revision;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityIncidentPersistedEvent>(OnIncident);
        SubscribeLocalEvent<NCServiceDispatchPersistedEvent>(OnDispatch);
        SubscribeLocalEvent<NCTerritoryControlChangedEvent>(OnTerritory);
        SubscribeLocalEvent<NCZoneIndexRebuiltEvent>(OnZoneIndexRebuilt);
    }

    public bool TryGet(NCZoneId districtId, out NCDistrictSafetyProjection projection) =>
        _districts.TryGetValue(districtId, out projection!);

    public IReadOnlyList<NCDistrictSafetyProjection> GetAll() =>
        _districts.Values.OrderBy(value => value.DistrictId.Value).ToArray();

    private void OnZoneIndexRebuilt(ref NCZoneIndexRebuiltEvent args)
    {
        RebuildAll();
    }

    private void OnIncident(NCCityIncidentPersistedEvent args)
    {
        var snapshot = args.Incident.Snapshot;
        _incidents[snapshot.IncidentId] = snapshot;
        if (TryDistrict(snapshot.LocationZone, out var district))
            Rebuild(district);
    }

    private void OnDispatch(NCServiceDispatchPersistedEvent args)
    {
        var snapshot = args.Dispatch.Snapshot;
        _dispatches[snapshot.DispatchId] = snapshot;
        if (TryDistrict(snapshot.DestinationZone, out var district))
            Rebuild(district);
    }

    private void OnTerritory(NCTerritoryControlChangedEvent args)
    {
        if (TryDistrict(args.ZoneId, out var district))
            Rebuild(district);
    }

    private void RebuildAll()
    {
        var affected = _incidents.Values.Select(value => value.LocationZone)
            .Concat(_dispatches.Values.Select(value => value.DestinationZone))
            .Select(value => TryDistrict(value, out var district) ? district : default)
            .Where(value => value.IsValid).Distinct().ToArray();
        foreach (var district in affected)
            Rebuild(district);
    }

    private void Rebuild(NCZoneId district)
    {
        if (!_prototypes.TryIndex(DefaultPolicy, out NCDistrictSafetyPolicyPrototype? policy))
            return;
        var activeIncidents = _incidents.Values.Where(value =>
            value.Stage is not (NCCityIncidentStage.Resolved or NCCityIncidentStage.Closed or
                NCCityIncidentStage.Cancelled) && TryDistrict(value.LocationZone, out var valueDistrict) &&
            valueDistrict == district).ToArray();
        var activeDispatches = _dispatches.Values.Where(value =>
            (value.Status is NCServiceDispatchStatus.Queued or NCServiceDispatchStatus.Assigned or
                NCServiceDispatchStatus.EnRoute or NCServiceDispatchStatus.OnScene) &&
            TryDistrict(value.DestinationZone, out var valueDistrict) && valueDistrict == district).ToArray();

        var threat = activeIncidents.Sum(value => value.Severity * policy.SeverityThreatPermille / 1000 +
            policy.IncidentThreat);
        NCPartyRef controlling = default;
        var lawPressure = 0;
        if (_territory.TryGet(district, out var territory) && territory.Controller is { } controller)
        {
            controlling = NCPartyRef.ForOrganization(controller);
            lawPressure = territory.PatrolPressure * policy.PatrolPressurePermille / 1000;
        }
        var ncpd = new NCCityServiceId(NCPersistentIdentity.CityService("ncpd").PersistentId);
        lawPressure += activeDispatches.Count(value => value.ServiceId == ncpd) * policy.PoliceDispatchPressure;
        _districts[district] = new NCDistrictSafetyProjection(district,
            Math.Clamp(threat, 0, policy.MaximumProjectionValue),
            Math.Clamp(lawPressure, 0, policy.MaximumProjectionValue), activeDispatches.Length,
            activeIncidents.Length, controlling, ++_revision);
        RaiseLocalEvent(new NCDistrictSafetyChangedEvent(_districts[district]));
    }

    private bool TryDistrict(NCZoneId zoneId, out NCZoneId district)
    {
        var current = zoneId;
        for (var depth = 0; depth < 16 && current.IsValid; depth++)
        {
            if (_zones.TryGetDistrictId(current, out var typed))
            {
                district = typed.ZoneId;
                return true;
            }
            if (!_zones.TryGetZone(current, out var info) || !info.Parent.IsValid)
                break;
            current = info.Parent;
        }
        district = default;
        return false;
    }
}

public sealed class NCDistrictSafetyChangedEvent(NCDistrictSafetyProjection projection) : EntityEventArgs
{
    public NCDistrictSafetyProjection Projection { get; } = projection;
}
