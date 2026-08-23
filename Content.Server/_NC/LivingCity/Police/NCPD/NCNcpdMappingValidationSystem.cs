// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Zones;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>
/// Event-driven station mapping validation. Diagnostics are cached for F7 and never rebuild city graphs.
/// </summary>
public sealed partial class NCNcpdMappingValidationSystem : EntitySystem
{
    private static readonly ProtoId<NCNcpdDutyPolicyPrototype> DefaultPolicy = "NCDefaultNcpdDutyPolicy";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCCityLocationSystem _locations = default!;

    private readonly HashSet<EntityUid> _workAnchors = [];
    private readonly HashSet<EntityUid> _patrolAnchors = [];
    private NCNcpdMappingDiagnostics _diagnostics = new(false, 0, 0, []);

    public bool StationValid => _diagnostics.StationValid;
    public int WorkAnchorCount => _diagnostics.WorkAnchors;
    public int PatrolAnchorCount => _diagnostics.PatrolAnchors;
    public int RequiredWorkAnchorCount =>
        _prototypes.TryIndex(DefaultPolicy, out NCNcpdDutyPolicyPrototype? policy) && policy != null
            ? policy.RequiredAnchors.Distinct().Count()
            : 0;
    public int MappingErrorCount => _diagnostics.Errors.Length;
    public string? FirstError => _diagnostics.Errors.FirstOrDefault();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCNcpdWorkAnchorComponent, ComponentStartup>(OnWorkAnchorStartup);
        SubscribeLocalEvent<NCNcpdWorkAnchorComponent, ComponentShutdown>(OnWorkAnchorShutdown);
        SubscribeLocalEvent<NCNcpdWorkAnchorComponent, NCZoneChangedEvent>(OnWorkAnchorZoneChanged);
        SubscribeLocalEvent<NCNcpdPatrolAnchorComponent, ComponentStartup>(OnPatrolAnchorStartup);
        SubscribeLocalEvent<NCNcpdPatrolAnchorComponent, ComponentShutdown>(OnPatrolAnchorShutdown);
        SubscribeLocalEvent<NCNcpdPatrolAnchorComponent, NCZoneChangedEvent>(OnPatrolAnchorZoneChanged);
        SubscribeLocalEvent<NCZoneIndexRebuiltEvent>(OnZoneIndexRebuilt);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        Validate();
    }

    public NCNcpdMappingDiagnostics GetDiagnostics() => _diagnostics;

    private void OnWorkAnchorStartup(Entity<NCNcpdWorkAnchorComponent> entity, ref ComponentStartup args)
    {
        _workAnchors.Add(entity.Owner);
        _locations.EnsureTracked(entity.Owner);
        Validate();
    }

    private void OnWorkAnchorShutdown(Entity<NCNcpdWorkAnchorComponent> entity, ref ComponentShutdown args)
    {
        _workAnchors.Remove(entity.Owner);
        Validate();
    }

    private void OnWorkAnchorZoneChanged(Entity<NCNcpdWorkAnchorComponent> entity, ref NCZoneChangedEvent args) =>
        Validate();

    private void OnPatrolAnchorStartup(Entity<NCNcpdPatrolAnchorComponent> entity, ref ComponentStartup args)
    {
        _patrolAnchors.Add(entity.Owner);
        _locations.EnsureTracked(entity.Owner);
        Validate();
    }

    private void OnPatrolAnchorShutdown(Entity<NCNcpdPatrolAnchorComponent> entity, ref ComponentShutdown args)
    {
        _patrolAnchors.Remove(entity.Owner);
        Validate();
    }

    private void OnPatrolAnchorZoneChanged(Entity<NCNcpdPatrolAnchorComponent> entity, ref NCZoneChangedEvent args) =>
        Validate();

    private void OnZoneIndexRebuilt(ref NCZoneIndexRebuiltEvent args) => Validate();

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<NCNcpdDutyPolicyPrototype>())
            Validate();
    }

    private void Validate()
    {
        var errors = new List<string>();
        if (!_prototypes.TryIndex(DefaultPolicy, out NCNcpdDutyPolicyPrototype? policy) || policy == null)
        {
            _diagnostics = new NCNcpdMappingDiagnostics(false, _workAnchors.Count, _patrolAnchors.Count,
                [$"Missing policy {DefaultPolicy}."]);
            return;
        }

        var stationValid = _zones.TryGetBuildingId(policy.StationZone, out var stationBuilding);
        if (!stationValid)
            errors.Add($"Station zone {policy.StationZone} is not a Building zone.");

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kinds = new HashSet<NCNcpdWorkAnchorKind>();
        foreach (var entityUid in _workAnchors)
        {
            if (!TryComp<NCNcpdWorkAnchorComponent>(entityUid, out var anchor))
                continue;
            if (string.IsNullOrWhiteSpace(anchor.StableKey))
                errors.Add($"Work anchor {entityUid} has no stable key.");
            else if (!keys.Add(anchor.StableKey.Trim()))
                errors.Add($"Duplicate work anchor key {anchor.StableKey.Trim()}.");

            kinds.Add(anchor.Kind);
            if (!stationValid || !_locations.TryGetBuilding(entityUid, out var building) || building != stationBuilding)
                errors.Add($"Work anchor {anchor.StableKey} is outside G NCPD #1.");
        }

        foreach (var required in policy.RequiredAnchors.Distinct())
        {
            if (!kinds.Contains(required))
                errors.Add($"Missing required {required} work anchor.");
        }

        foreach (var entityUid in _patrolAnchors)
        {
            if (!TryComp<NCNcpdPatrolAnchorComponent>(entityUid, out var anchor))
                continue;
            if (string.IsNullOrWhiteSpace(anchor.StableKey))
                errors.Add($"Patrol anchor {entityUid} has no stable key.");
            else if (!keys.Add(anchor.StableKey.Trim()))
                errors.Add($"Duplicate NCPD anchor key {anchor.StableKey.Trim()}.");
            if (!_locations.TryGetStreet(entityUid, out _))
                errors.Add($"Patrol anchor {anchor.StableKey} is outside a Street zone.");
        }

        _diagnostics = new NCNcpdMappingDiagnostics(
            stationValid,
            _workAnchors.Count,
            _patrolAnchors.Count,
            errors.Order(StringComparer.Ordinal).ToArray());
    }
}
