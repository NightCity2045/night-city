// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.CityServices;

/// <summary>Event-driven contested territory projection consumed by taxation, patrol and supply systems.</summary>
public sealed partial class NCTerritoryProjectionSystem : EntitySystem
{
    private static readonly ProtoId<NCFactionPolicyPrototype> DefaultPolicy = "Default";

    [Dependency] private IPrototypeManager _prototypes = default!;
    private readonly Dictionary<NCZoneId, Dictionary<NCOrganizationId, (NCFactionTerritoryStake Stake,
        NCFactionPolicy Policy, long Version)>> _stakes = [];
    private readonly Dictionary<NCZoneId, NCTerritoryControlProjection> _control = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCFactionPersistedEvent>(OnFactionPersisted);
    }

    public bool TryGet(NCZoneId zoneId, out NCTerritoryControlProjection projection) =>
        _control.TryGetValue(zoneId, out projection!);

    private void OnFactionPersisted(NCFactionPersistedEvent args)
    {
        var faction = args.Faction;
        var affected = new HashSet<NCZoneId>();
        foreach (var (zoneId, zone) in _stakes)
        {
            if (zone.Remove(faction.Snapshot.FactionId))
                affected.Add(zoneId);
        }
        foreach (var stake in faction.Snapshot.Territories)
        {
            if (!_stakes.TryGetValue(stake.ZoneId, out var contenders))
                _stakes[stake.ZoneId] = contenders = [];
            contenders[faction.Snapshot.FactionId] = (stake, faction.Snapshot.Policy, faction.AggregateVersion);
            affected.Add(stake.ZoneId);
        }
        foreach (var zoneId in affected)
        {
            if (_stakes.TryGetValue(zoneId, out var contenders) && contenders.Count > 0)
                Rebuild(zoneId, contenders);
            else
            {
                _control.Remove(zoneId);
                RaiseLocalEvent(new NCTerritoryControlChangedEvent(zoneId, null));
            }
        }
    }

    private void Rebuild(NCZoneId zoneId, Dictionary<NCOrganizationId,
        (NCFactionTerritoryStake Stake, NCFactionPolicy Policy, long Version)> contenders)
    {
        if (!_prototypes.TryIndex(DefaultPolicy, out var policy))
            return;
        var ranked = contenders.OrderByDescending(value => value.Value.Stake.Influence)
            .ThenBy(value => value.Key.Value).ToArray();
        NCOrganizationId? controller = null;
        if (ranked.Length > 0 && ranked[0].Value.Stake.ClaimsControl &&
            ranked[0].Value.Stake.Influence >= policy.ControlThreshold &&
            (ranked.Length == 1 || ranked[0].Value.Stake.Influence - ranked[1].Value.Stake.Influence >=
                policy.ControlMargin))
            controller = ranked[0].Key;
        var controllerPolicy = controller is { } id ? contenders[id].Policy : null;
        _control[zoneId] = new NCTerritoryControlProjection(zoneId, controller,
            ranked.Select(value => value.Value.Stake).ToArray(), controllerPolicy?.TaxBasisPoints ?? 0,
            controllerPolicy?.ProtectionBasisPoints ?? 0, controllerPolicy?.PatrolPriority ?? 0,
            controllerPolicy?.SupplyPriority ?? 0, ranked.Select(value => value.Value.Version).DefaultIfEmpty(0).Max());
        RaiseLocalEvent(new NCTerritoryControlChangedEvent(zoneId, _control[zoneId]));
    }
}

public sealed class NCTerritoryControlChangedEvent(NCZoneId zoneId, NCTerritoryControlProjection? projection)
    : EntityEventArgs
{
    public NCZoneId ZoneId { get; } = zoneId;
    public NCTerritoryControlProjection? Projection { get; } = projection;
}
