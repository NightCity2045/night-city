// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Connectivity;
using Content.Server._NC.City.Zones;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Prototypes;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Affordances;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Affordances;

/// <summary>
/// Event-driven catalog of real object capabilities and geography-derived navigation capabilities. NPC hot paths
/// query prebuilt indexes; they never scan every entity or every city tile.
/// </summary>
public sealed partial class NCCityAffordanceCatalogSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCCityLocationSystem _locations = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCCitySpawnCandidateSystem _spawnCandidates = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;

    private readonly Dictionary<NCCityAffordanceId, NCCityAffordanceInfo> _entries = [];
    private readonly Dictionary<NCCityAffordanceKind, List<NCCityAffordanceId>> _byKind = [];
    private readonly Dictionary<EntityUid, List<NCCityAffordanceId>> _byProvider = [];
    private readonly HashSet<EntityUid> _providers = [];
    private readonly List<NCCitySpawnCandidateInfo> _candidateBuffer = [];
    private long _nextId = 1;

    public long Version { get; private set; }
    public int EntryCount => _entries.Count;

    public int Count(NCCityAffordanceKind kind) =>
        _byKind.TryGetValue(kind, out var entries) ? entries.Count : 0;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityAffordanceProviderComponent, ComponentStartup>(OnProviderStartup);
        SubscribeLocalEvent<NCCityAffordanceProviderComponent, ComponentShutdown>(OnProviderShutdown);
        SubscribeLocalEvent<NCCityAffordanceProviderComponent, NCZoneChangedEvent>(OnProviderZoneChanged);
        SubscribeLocalEvent<NCCitySpawnCandidateIndexChangedEvent>(OnSpawnCandidatesChanged);
        SubscribeLocalEvent<NCZoneIndexRebuiltEvent>(_ => RebuildGenerated());
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
    }

    public void Get(NCCityAffordanceKind kind, NCZoneId withinZone, List<NCCityAffordanceInfo> result)
    {
        result.Clear();
        if (!_byKind.TryGetValue(kind, out var ids))
            return;
        foreach (var id in ids)
        {
            if (_entries.TryGetValue(id, out var entry) &&
                (!withinZone.IsValid || IsWithin(entry.ZoneId, withinZone)))
                result.Add(entry);
        }
        result.Sort(static (left, right) => left.Id.Value.CompareTo(right.Id.Value));
    }

    public bool TrySelect(NCCityAffordanceKind kind, NCZoneId withinZone, uint seed, out NCCityAffordanceInfo entry)
    {
        var entries = new List<NCCityAffordanceInfo>();
        Get(kind, withinZone, entries);
        if (entries.Count == 0)
        {
            entry = default;
            return false;
        }
        entry = entries[(int) (seed % (uint) entries.Count)];
        return true;
    }

    private void OnProviderStartup(Entity<NCCityAffordanceProviderComponent> entity, ref ComponentStartup args)
    {
        _providers.Add(entity.Owner);
        _locations.EnsureTracked(entity.Owner);
        ReindexProvider(entity.Owner, entity.Comp);
    }

    private void OnProviderShutdown(Entity<NCCityAffordanceProviderComponent> entity, ref ComponentShutdown args)
    {
        _providers.Remove(entity.Owner);
        RemoveProvider(entity.Owner);
        Invalidate();
    }

    private void OnProviderZoneChanged(Entity<NCCityAffordanceProviderComponent> entity, ref NCZoneChangedEvent args) =>
        ReindexProvider(entity.Owner, entity.Comp);

    private void OnSpawnCandidatesChanged(ref NCCitySpawnCandidateIndexChangedEvent args) => RebuildGenerated();

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<NCCityAffordancePrototype>() &&
            !args.WasModified<NCCityZoneAffordanceSourcePrototype>() &&
            !args.WasModified<NCZoneKindPrototype>())
            return;
        foreach (var provider in _providers.ToArray())
        {
            if (TryComp<NCCityAffordanceProviderComponent>(provider, out var component))
                ReindexProvider(provider, component);
        }
        RebuildGenerated();
    }

    private void ReindexProvider(EntityUid provider, NCCityAffordanceProviderComponent component)
    {
        RemoveProvider(provider);
        if (!_locations.TryGetContext(provider, out var location) ||
            !_coordinates.TryGetCoordinates(provider, out var target))
        {
            Invalidate();
            return;
        }
        foreach (var affordanceId in component.Affordances.Distinct())
        {
            if (!_prototypes.TryIndex(affordanceId, out var affordance) || affordance == null)
                continue;
            Add(affordanceId, affordance, provider, location.MostSpecific, target, true);
        }
        Invalidate();
    }

    private void RebuildGenerated()
    {
        foreach (var id in _entries.Where(static pair => !pair.Value.PhysicalProvider)
                     .Select(static pair => pair.Key).ToArray())
            Remove(id);

        foreach (var source in _prototypes.EnumeratePrototypes<NCCityZoneAffordanceSourcePrototype>()
                     .OrderBy(static value => value.ID, StringComparer.Ordinal))
        {
            if (source.MaximumCandidates <= 0)
                continue;
            if (source.Zone.IsValid)
                _spawnCandidates.GetCandidatesInHierarchy(source.CandidateProfile, source.Zone, _candidateBuffer);
            else
                _spawnCandidates.GetCandidates(source.CandidateProfile, default, _candidateBuffer);

            var accepted = 0;
            foreach (var candidate in _candidateBuffer)
            {
                if (accepted >= source.MaximumCandidates || !_zones.TryGetZone(candidate.ZoneId, out var zone) ||
                    source.RequiredContext != NCZoneContextSlot.None && zone.ContextSlot != source.RequiredContext)
                    continue;
                foreach (var affordanceId in source.Affordances.Distinct())
                {
                    if (_prototypes.TryIndex(affordanceId, out var affordance) && affordance != null &&
                        !affordance.RequiresPhysicalProvider)
                        Add(affordanceId, affordance, EntityUid.Invalid, candidate.ZoneId, candidate.Target, false);
                }
                accepted++;
            }
        }
        Invalidate();
    }

    private void Add(ProtoId<NCCityAffordancePrototype> prototypeId, NCCityAffordancePrototype prototype,
        EntityUid provider, NCZoneId zone, Content.Shared._NC.Coordinates.NCMapCoordinates target, bool physical)
    {
        var id = new NCCityAffordanceId(_nextId++);
        var entry = new NCCityAffordanceInfo(id, prototypeId, prototype.Kind, prototype.KnowledgeKey,
            prototype.OrganizationKey, provider, zone, target, physical);
        _entries.Add(id, entry);
        if (!_byKind.TryGetValue(entry.Kind, out var byKind))
            _byKind[entry.Kind] = byKind = [];
        byKind.Add(id);
        if (provider.IsValid())
        {
            if (!_byProvider.TryGetValue(provider, out var byProvider))
                _byProvider[provider] = byProvider = [];
            byProvider.Add(id);
        }
    }

    private void RemoveProvider(EntityUid provider)
    {
        if (!_byProvider.Remove(provider, out var ids))
            return;
        foreach (var id in ids)
            Remove(id);
    }

    private void Remove(NCCityAffordanceId id)
    {
        if (!_entries.Remove(id, out var entry) || !_byKind.TryGetValue(entry.Kind, out var ids))
            return;
        ids.Remove(id);
        if (ids.Count == 0)
            _byKind.Remove(entry.Kind);
    }

    private bool IsWithin(NCZoneId candidate, NCZoneId ancestor)
    {
        var current = candidate;
        for (var depth = 0; current.IsValid && depth < 16 && _zones.TryGetZone(current, out var zone); depth++)
        {
            if (current == ancestor)
                return true;
            current = zone.Parent;
        }
        return false;
    }

    private void Invalidate()
    {
        var old = Version;
        Version = Version == long.MaxValue ? 1 : Version + 1;
        var ev = new NCCityAffordanceCatalogChangedEvent(old, Version);
        RaiseLocalEvent(ref ev);
    }
}
