// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Connectivity;
using Content.Server._NC.City.Zones;
using Content.Server.Medical.Components;
using Content.Server.Construction.Components;
using Content.Server.Botany.Components;
using Content.Server.Chemistry.Components;
using Content.Server.Kitchen.Components;
using Content.Shared.Bed.Components;
using Content.Shared.Buckle.Components;
using Content.Shared.Doors.Components;
using Content.Shared.Nutrition.Components;
using Content.Shared.Fluids.Components;
using Content.Shared.Storage;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Prototypes;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Affordances;
using Content.Shared.VendingMachines;
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
    [Dependency] private NCRoomPortalGraphSystem _roomGraph = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;

    private readonly Dictionary<NCCityAffordanceId, NCCityAffordanceInfo> _entries = [];
    private readonly Dictionary<NCCityAffordanceKind, List<NCCityAffordanceId>> _byKind = [];
    private readonly Dictionary<EntityUid, List<NCCityAffordanceId>> _byProvider = [];
    private readonly HashSet<EntityUid> _explicitProviders = [];
    private readonly Dictionary<EntityUid, HashSet<NCCityObjectAffordanceSource>> _inferredProviders = [];
    private readonly Dictionary<NCCityObjectAffordanceSource, HashSet<ProtoId<NCCityAffordancePrototype>>>
        _objectRules = [];
    private readonly List<NCCitySpawnCandidateInfo> _candidateBuffer = [];
    private readonly List<NCRoomPortalInfo> _portalBuffer = [];
    private readonly HashSet<ProtoId<NCCityAffordancePrototype>> _providerAffordanceBuffer = [];
    private long _nextId = 1;
    private int _physicalEntries;

    public long Version { get; private set; }
    public int EntryCount => _entries.Count;
    public int PhysicalEntryCount => _physicalEntries;
    public int GeneratedEntryCount => _entries.Count - _physicalEntries;

    public int Count(NCCityAffordanceKind kind) =>
        _byKind.TryGetValue(kind, out var entries) ? entries.Count : 0;

    public string? GetConfigurationError()
    {
        if (Count(NCCityAffordanceKind.PublicEntrance) == 0 || Count(NCCityAffordanceKind.BuildingExit) == 0)
            return "affordance-building-boundaries-missing";
        if (Count(NCCityAffordanceKind.Workplace) == 0 || Count(NCCityAffordanceKind.RestArea) == 0)
            return "affordance-required-routine-targets-missing";
        return null;
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityAffordanceProviderComponent, ComponentStartup>(OnProviderStartup);
        SubscribeLocalEvent<NCCityAffordanceProviderComponent, ComponentShutdown>(OnProviderShutdown);
        SubscribeLocalEvent<NCCityAffordanceProviderComponent, NCZoneChangedEvent>(OnProviderZoneChanged);
        SubscribeLocalEvent<StrapComponent, NCZoneChangedEvent>(OnInferredProviderZoneChanged);
        SubscribeLocalEvent<VendingMachineComponent, NCZoneChangedEvent>(OnInferredProviderZoneChanged);
        SubscribeLocalEvent<StasisBedComponent, NCZoneChangedEvent>(OnInferredProviderZoneChanged);
        SubscribeLocalEvent<MedicalScannerComponent, NCZoneChangedEvent>(OnInferredProviderZoneChanged);
        SubscribeLocalEvent<DoorComponent, NCZoneChangedEvent>(OnInferredProviderZoneChanged);
        SubscribeLocalEvent<StorageComponent, NCZoneChangedEvent>(OnInferredProviderZoneChanged);
        SubscribeLocalEvent<EdibleComponent, NCZoneChangedEvent>(OnInferredProviderZoneChanged);
        SubscribeLocalEvent<GunComponent, NCZoneChangedEvent>(OnInferredProviderZoneChanged);
        SubscribeLocalEvent<MeleeWeaponComponent, NCZoneChangedEvent>(OnInferredProviderZoneChanged);
        SubscribeLocalEvent<ComputerComponent, NCZoneChangedEvent>(OnInferredProviderZoneChanged);
        SubscribeLocalEvent<ReagentDispenserComponent, NCZoneChangedEvent>(OnInferredProviderZoneChanged);
        SubscribeLocalEvent<MicrowaveComponent, NCZoneChangedEvent>(OnInferredProviderZoneChanged);
        SubscribeLocalEvent<PlantHolderComponent, NCZoneChangedEvent>(OnInferredProviderZoneChanged);
        SubscribeLocalEvent<PuddleComponent, NCZoneChangedEvent>(OnInferredProviderZoneChanged);
        SubscribeLocalEvent<NCCitySpawnCandidateIndexChangedEvent>(OnSpawnCandidatesChanged);
        SubscribeLocalEvent<NCZoneIndexRebuiltEvent>(OnZoneIndexRebuilt);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        EntityManager.ComponentAdded += OnComponentAdded;
        EntityManager.ComponentRemoved += OnComponentRemoved;
        RebuildObjectRules();
    }

    public override void Shutdown()
    {
        EntityManager.ComponentAdded -= OnComponentAdded;
        EntityManager.ComponentRemoved -= OnComponentRemoved;
        base.Shutdown();
    }

    private void OnZoneIndexRebuilt(ref NCZoneIndexRebuiltEvent args) => RebuildGenerated();

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
        if (!_byKind.TryGetValue(kind, out var ids))
        {
            entry = default;
            return false;
        }

        var count = 0;
        foreach (var id in ids)
        {
            if (_entries.TryGetValue(id, out var candidate) &&
                (!withinZone.IsValid || IsWithin(candidate.ZoneId, withinZone)))
                count++;
        }
        if (count == 0)
        {
            entry = default;
            return false;
        }

        var selected = (int) (seed % (uint) count);
        foreach (var id in ids)
        {
            if (!_entries.TryGetValue(id, out var candidate) ||
                withinZone.IsValid && !IsWithin(candidate.ZoneId, withinZone))
                continue;
            if (selected-- != 0)
                continue;
            entry = candidate;
            return true;
        }

        entry = default;
        return false;
    }

    /// <summary>Copies the semantic actions provided by one already-perceived physical entity.</summary>
    public void GetForProvider(EntityUid provider, List<NCCityAffordanceInfo> result)
    {
        result.Clear();
        if (!_byProvider.TryGetValue(provider, out var ids))
            return;
        foreach (var id in ids)
        {
            if (_entries.TryGetValue(id, out var entry))
                result.Add(entry);
        }
        result.Sort(static (left, right) => left.Id.Value.CompareTo(right.Id.Value));
    }

    private void OnProviderStartup(Entity<NCCityAffordanceProviderComponent> entity, ref ComponentStartup args)
    {
        _explicitProviders.Add(entity.Owner);
        _locations.EnsureTracked(entity.Owner);
        ReindexProvider(entity.Owner);
    }

    private void OnProviderShutdown(Entity<NCCityAffordanceProviderComponent> entity, ref ComponentShutdown args)
    {
        _explicitProviders.Remove(entity.Owner);
        ReindexOrRemoveProvider(entity.Owner);
    }

    private void OnProviderZoneChanged(Entity<NCCityAffordanceProviderComponent> entity, ref NCZoneChangedEvent args) =>
        ReindexProvider(entity.Owner);

    private void OnInferredProviderZoneChanged<T>(Entity<T> entity, ref NCZoneChangedEvent args) where T : Component =>
        ReindexProvider(entity.Owner);

    private void OnComponentAdded(AddedComponentEventArgs args)
    {
        // Observe vanilla object components without taking ownership of their exclusive lifecycle events.
        switch (args.BaseArgs.Component)
        {
            case StrapComponent:
                AddInferredProvider(args.BaseArgs.Owner, NCCityObjectAffordanceSource.Strap);
                break;
            case VendingMachineComponent:
                AddInferredProvider(args.BaseArgs.Owner, NCCityObjectAffordanceSource.VendingMachine);
                break;
            case StasisBedComponent:
            case MedicalScannerComponent:
                AddInferredProvider(args.BaseArgs.Owner, NCCityObjectAffordanceSource.MedicalTreatment);
                break;
            case DoorComponent:
                AddInferredProvider(args.BaseArgs.Owner, NCCityObjectAffordanceSource.Door);
                break;
            case StorageComponent:
                AddInferredProvider(args.BaseArgs.Owner, NCCityObjectAffordanceSource.Storage);
                break;
            case EdibleComponent:
                AddInferredProvider(args.BaseArgs.Owner, NCCityObjectAffordanceSource.Edible);
                break;
            case GunComponent:
            case MeleeWeaponComponent:
                AddInferredProvider(args.BaseArgs.Owner, NCCityObjectAffordanceSource.Weapon);
                break;
            case ComputerComponent:
                AddInferredProvider(args.BaseArgs.Owner, NCCityObjectAffordanceSource.Computer);
                break;
            case ReagentDispenserComponent:
                AddInferredProvider(args.BaseArgs.Owner, NCCityObjectAffordanceSource.ReagentDispenser);
                break;
            case MicrowaveComponent:
                AddInferredProvider(args.BaseArgs.Owner, NCCityObjectAffordanceSource.Microwave);
                break;
            case PlantHolderComponent:
                AddInferredProvider(args.BaseArgs.Owner, NCCityObjectAffordanceSource.PlantHolder);
                break;
            case PuddleComponent:
                AddInferredProvider(args.BaseArgs.Owner, NCCityObjectAffordanceSource.Puddle);
                break;
        }
    }

    private void OnComponentRemoved(RemovedComponentEventArgs args)
    {
        var provider = args.BaseArgs.Owner;
        if (!_inferredProviders.ContainsKey(provider))
            return;
        if (args.Terminating)
        {
            _inferredProviders.Remove(provider);
            RemoveProvider(provider);
            Invalidate();
            return;
        }

        switch (args.BaseArgs.Component)
        {
            case StrapComponent:
                RemoveInferredProvider(provider, NCCityObjectAffordanceSource.Strap);
                break;
            case VendingMachineComponent:
                RemoveInferredProvider(provider, NCCityObjectAffordanceSource.VendingMachine);
                break;
            case StasisBedComponent:
            case MedicalScannerComponent:
                RemoveInferredProvider(provider, NCCityObjectAffordanceSource.MedicalTreatment);
                break;
            case DoorComponent:
                RemoveInferredProvider(provider, NCCityObjectAffordanceSource.Door);
                break;
            case StorageComponent:
                RemoveInferredProvider(provider, NCCityObjectAffordanceSource.Storage);
                break;
            case EdibleComponent:
                RemoveInferredProvider(provider, NCCityObjectAffordanceSource.Edible);
                break;
            case GunComponent:
            case MeleeWeaponComponent:
                RemoveInferredProvider(provider, NCCityObjectAffordanceSource.Weapon);
                break;
            case ComputerComponent:
                RemoveInferredProvider(provider, NCCityObjectAffordanceSource.Computer);
                break;
            case ReagentDispenserComponent:
                RemoveInferredProvider(provider, NCCityObjectAffordanceSource.ReagentDispenser);
                break;
            case MicrowaveComponent:
                RemoveInferredProvider(provider, NCCityObjectAffordanceSource.Microwave);
                break;
            case PlantHolderComponent:
                RemoveInferredProvider(provider, NCCityObjectAffordanceSource.PlantHolder);
                break;
            case PuddleComponent:
                RemoveInferredProvider(provider, NCCityObjectAffordanceSource.Puddle);
                break;
        }
    }

    private void OnSpawnCandidatesChanged(ref NCCitySpawnCandidateIndexChangedEvent args) => RebuildGenerated();

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<NCCityAffordancePrototype>() &&
            !args.WasModified<NCCityObjectAffordanceRulePrototype>() &&
            !args.WasModified<NCCityZoneAffordanceSourcePrototype>() &&
            !args.WasModified<NCZoneKindPrototype>())
            return;
        RebuildObjectRules();
        foreach (var provider in _explicitProviders.Concat(_inferredProviders.Keys).Distinct().ToArray())
            ReindexProvider(provider);
        RebuildGenerated();
    }

    private void AddInferredProvider(EntityUid provider, NCCityObjectAffordanceSource source)
    {
        _locations.EnsureTracked(provider);
        if (!_inferredProviders.TryGetValue(provider, out var sources))
            _inferredProviders[provider] = sources = [];
        if (sources.Add(source))
            ReindexProvider(provider);
    }

    private void RemoveInferredProvider(EntityUid provider, NCCityObjectAffordanceSource source)
    {
        if (!_inferredProviders.TryGetValue(provider, out var sources) || !sources.Remove(source))
            return;
        if (sources.Count == 0)
            _inferredProviders.Remove(provider);
        ReindexOrRemoveProvider(provider);
    }

    private void ReindexOrRemoveProvider(EntityUid provider)
    {
        if (_explicitProviders.Contains(provider) || _inferredProviders.ContainsKey(provider))
            ReindexProvider(provider);
        else
        {
            RemoveProvider(provider);
            Invalidate();
        }
    }

    private void RebuildObjectRules()
    {
        _objectRules.Clear();
        foreach (var rule in _prototypes.EnumeratePrototypes<NCCityObjectAffordanceRulePrototype>())
        {
            if (!_objectRules.TryGetValue(rule.Source, out var affordances))
                _objectRules[rule.Source] = affordances = [];
            affordances.UnionWith(rule.Affordances);
        }
    }

    private void ReindexProvider(EntityUid provider)
    {
        RemoveProvider(provider);
        _coordinates.TryGetCoordinates(provider, out var target);

        // Ordinary SS14 maps intentionally have no CityGraph. A physical provider is still a real nearby
        // opportunity there; it simply has no stable NC coordinate or semantic city-zone parent. Callers that need
        // long-range routing reject that empty coordinate, while local neural actions target the provider entity.
        var zone = _locations.TryGetContext(provider, out var location)
            ? location.MostSpecific
            : default;

        _providerAffordanceBuffer.Clear();
        if (TryComp<NCCityAffordanceProviderComponent>(provider, out var component))
            _providerAffordanceBuffer.UnionWith(component.Affordances);
        if (_inferredProviders.TryGetValue(provider, out var sources))
        {
            foreach (var source in sources)
            {
                if (_objectRules.TryGetValue(source, out var affordances))
                    _providerAffordanceBuffer.UnionWith(affordances);
            }
        }

        foreach (var affordanceId in _providerAffordanceBuffer)
        {
            if (!_prototypes.TryIndex(affordanceId, out var affordance) || affordance == null)
                continue;
            Add(affordanceId, affordance, provider, zone, target, true);
        }
        Invalidate();
    }

    private void RebuildGenerated()
    {
        foreach (var id in _entries.Where(static pair => !pair.Value.PhysicalProvider)
                     .Select(static pair => pair.Key).ToArray())
            Remove(id);

        _roomGraph.GetPortals(_portalBuffer);

        foreach (var source in _prototypes.EnumeratePrototypes<NCCityZoneAffordanceSourcePrototype>()
                     .OrderBy(static value => value.ID, StringComparer.Ordinal))
        {
            if (source.MaximumCandidates <= 0 || source.Zone == Guid.Empty)
                continue;
            NCZoneId? restrictedZone = source.Zone is { } zoneGuid ? new NCZoneId(zoneGuid) : null;
            if (restrictedZone is { IsValid: true } concreteZone)
                _spawnCandidates.GetCandidatesInHierarchy(source.CandidateProfile, concreteZone, _candidateBuffer);
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

            AddBoundaryAffordances(source, restrictedZone);
        }
        Invalidate();
    }

    private void AddBoundaryAffordances(
        NCCityZoneAffordanceSourcePrototype source,
        NCZoneId? restrictedZone)
    {
        if (source.BoundaryAffordances.Count == 0 || source.RequiredContext == NCZoneContextSlot.None)
            return;

        foreach (var portal in _portalBuffer)
        {
            if (!_roomGraph.TryGetRoomInfo(portal.RoomA, out var roomA) ||
                !_roomGraph.TryGetRoomInfo(portal.RoomB, out var roomB))
                continue;

            AddBoundarySide(source, restrictedZone, roomA.ZoneId, roomB.ZoneId, portal.TargetA);
            AddBoundarySide(source, restrictedZone, roomB.ZoneId, roomA.ZoneId, portal.TargetB);
        }
    }

    private void AddBoundarySide(NCCityZoneAffordanceSourcePrototype source, NCZoneId? restrictedZone,
        NCZoneId inside,
        NCZoneId outside, Content.Shared._NC.Coordinates.NCMapCoordinates target)
    {
        if (!TryGetAncestor(inside, source.RequiredContext, out var insideScope) ||
            TryGetAncestor(outside, source.RequiredContext, out var outsideScope) && outsideScope == insideScope ||
            restrictedZone is { IsValid: true } concreteZone && insideScope != concreteZone)
            return;

        foreach (var affordanceId in source.BoundaryAffordances.Distinct())
        {
            if (_prototypes.TryIndex(affordanceId, out var affordance) && affordance != null &&
                !affordance.RequiresPhysicalProvider)
                Add(affordanceId, affordance, EntityUid.Invalid, inside, target, false);
        }
    }

    private bool TryGetAncestor(NCZoneId zoneId, NCZoneContextSlot slot, out NCZoneId ancestor)
    {
        var current = zoneId;
        for (var depth = 0; current.IsValid && depth < 16 && _zones.TryGetZone(current, out var zone); depth++)
        {
            if (zone.ContextSlot == slot)
            {
                ancestor = current;
                return true;
            }
            current = zone.Parent;
        }
        ancestor = default;
        return false;
    }

    private void Add(ProtoId<NCCityAffordancePrototype> prototypeId, NCCityAffordancePrototype prototype,
        EntityUid provider, NCZoneId zone, Content.Shared._NC.Coordinates.NCMapCoordinates target, bool physical)
    {
        var id = new NCCityAffordanceId(_nextId++);
        var entry = new NCCityAffordanceInfo(id, prototypeId, prototype.Kind, prototype.KnowledgeKey,
            prototype.OrganizationKey, provider, zone, target, physical);
        _entries.Add(id, entry);
        if (physical)
            _physicalEntries++;
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
        if (entry.PhysicalProvider)
            _physicalEntries--;
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
