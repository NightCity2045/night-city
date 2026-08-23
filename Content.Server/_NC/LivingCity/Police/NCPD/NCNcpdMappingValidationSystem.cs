// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Affordances;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Prototypes;
using Content.Shared._NC.LivingCity.Affordances;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>Validates semantic geography and automatically generated capabilities; mapper markers are not accepted.</summary>
public sealed partial class NCNcpdMappingValidationSystem : EntitySystem
{
    private static readonly ProtoId<NCNcpdDutyPolicyPrototype> DutyPolicy = "NCDefaultNcpdDutyPolicy";
    private static readonly NCCityAffordanceKind[] RequiredStationAffordances =
    [
        NCCityAffordanceKind.Workplace,
        NCCityAffordanceKind.PublicEntrance,
        NCCityAffordanceKind.BuildingExit,
    ];

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCCityAffordanceCatalogSystem _affordances = default!;

    private readonly List<NCCityAffordanceInfo> _buffer = [];
    private NCNcpdMappingDiagnostics _diagnostics = new(false, 0, 0, ["not-validated"]);

    public bool StationValid => _diagnostics.StationValid;
    public int StationAffordanceCount => _diagnostics.StationAffordances;
    public int RequiredStationAffordanceCount => RequiredStationAffordances.Length;
    public int PatrolAreaCount => _diagnostics.PatrolAreas;
    public int MappingErrorCount => _diagnostics.Errors.Length;
    public string? FirstError => _diagnostics.Errors.FirstOrDefault();
    public NCNcpdMappingDiagnostics GetDiagnostics() => _diagnostics;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityAffordanceCatalogChangedEvent>(OnCatalogChanged);
        SubscribeLocalEvent<NCZoneIndexRebuiltEvent>(OnZoneIndexRebuilt);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        Validate();
    }

    private void OnCatalogChanged(ref NCCityAffordanceCatalogChangedEvent args) => Validate();
    private void OnZoneIndexRebuilt(ref NCZoneIndexRebuiltEvent args) => Validate();
    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args) => Validate();

    private void Validate()
    {
        var errors = new List<string>();
        if (!_prototypes.TryIndex(DutyPolicy, out var policy) || policy == null ||
            !_zones.TryGetZone(policy.StationZone, out var station) || station.ContextSlot != NCZoneContextSlot.Building)
        {
            _diagnostics = new NCNcpdMappingDiagnostics(false, 0, 0, ["NCPD station zone is not a Building zone."]);
            return;
        }

        var stationCount = 0;
        foreach (var kind in RequiredStationAffordances)
        {
            _affordances.Get(kind, policy.StationZone, _buffer);
            stationCount += _buffer.Count;
            if (_buffer.Count == 0)
                errors.Add($"Station affordance {kind} was not generated from its zone/navigation catalog.");
        }

        _affordances.Get(NCCityAffordanceKind.PatrolArea, default, _buffer);
        if (_buffer.Count == 0)
            errors.Add("No Street patrol areas were generated from city geography.");

        _diagnostics = new NCNcpdMappingDiagnostics(true, stationCount, _buffer.Count,
            errors.Order(StringComparer.Ordinal).ToArray());
    }
}
