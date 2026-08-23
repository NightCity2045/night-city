// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server._NC.City.ZoneTemplates;
using Content.Server._NC.City.Zones;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.ZoneTemplates.Components;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.City.Connectivity;

/// <summary>
/// Versioned semantic connectivity graph for NPC and A-Life routing. Zone containment comes from city_zone;
/// traversable edges are registered by map analysis and explicit vertical/deployment links.
/// </summary>
public sealed partial class NCCityConnectivitySystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCZoneSystem _zones = default!;

    private readonly Dictionary<NCZoneId, SemanticNode> _nodes = [];
    private readonly Dictionary<NCCityPortalId, RuntimePortal> _portals = [];
    private readonly Dictionary<NCZoneId, List<NCCityPortalId>> _outgoing = [];
    private readonly Dictionary<NCStableCityLinkId, NCCityPortalId> _stableLinks = [];
    private readonly Dictionary<Guid, HashSet<NCStableCityLinkId>> _deploymentLinks = [];
    private readonly Dictionary<RouteCacheKey, NCCityRouteStep[]> _routeCache = [];
    private readonly Dictionary<DepthRouteCacheKey, NCCityRouteStep[]> _depthRouteCache = [];

    private long _nextPortalId = 1;
    private int _mutationBatchDepth;
    private bool _mutationBatchDirty;
    private long _publishedVersionChanges;
    private long _deferredInvalidations;
    private long _completedMutationBatches;

    public long GraphVersion { get; private set; }

    /// <summary>
    /// Returns graph-mutation counters without walking nodes or portals. F7 uses them to distinguish a real topology
    /// change from a portal-by-portal invalidation storm.
    /// </summary>
    public NCCityConnectivityMutationCounters GetMutationCounters() => new(
        _publishedVersionChanges,
        _deferredInvalidations,
        _completedMutationBatches,
        _mutationBatchDepth);

    /// <summary>
    /// Defers version publication while one logical topology replacement removes and recreates multiple portals.
    /// The graph is still mutated synchronously on the main thread, but consumers observe one atomic revision.
    /// </summary>
    public NCCityConnectivityMutationBatch BeginMutationBatch()
    {
        _mutationBatchDepth++;
        return new NCCityConnectivityMutationBatch(this);
    }

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCZoneIndexRebuiltEvent>(OnZoneIndexRebuilt);
        SubscribeLocalEvent<NCZoneDeploymentStateChangedEvent>(OnDeploymentStateChanged);
        SubscribeLocalEvent<NCResolvedVerticalLinkComponent, ComponentShutdown>(OnVerticalLinkShutdown);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        RebuildSemanticIndex();
        RebuildVerticalLinks();
    }

    public bool ContainsNode(NCZoneId zoneId)
    {
        return _nodes.ContainsKey(zoneId);
    }

    public bool TryGetParent(NCZoneId zoneId, out NCZoneId parent)
    {
        if (_nodes.TryGetValue(zoneId, out var node))
        {
            parent = node.Zone.Parent;
            return parent.IsValid;
        }

        parent = default;
        return false;
    }

    public void GetContainment(List<NCCityContainmentInfo> result)
    {
        result.Clear();
        foreach (var node in _nodes.Values)
        {
            if (node.Zone.Parent.IsValid)
                result.Add(new NCCityContainmentInfo(node.Zone.Id, node.Zone.Parent));
        }

        result.Sort(static (left, right) => left.ZoneId.Value.CompareTo(right.ZoneId.Value));
    }

    public void GetOutgoingPortals(NCZoneId zoneId, List<NCCityPortalInfo> result)
    {
        result.Clear();
        if (!_outgoing.TryGetValue(zoneId, out var portalIds))
            return;

        foreach (var portalId in portalIds)
        {
            if (_portals.TryGetValue(portalId, out var portal))
                result.Add(ToInfo(portal));
        }
    }

    public bool TryGetPortal(NCCityPortalId portalId, out NCCityPortalInfo portal)
    {
        if (_portals.TryGetValue(portalId, out var runtime))
        {
            portal = ToInfo(runtime);
            return true;
        }

        portal = default;
        return false;
    }

    /// <summary>
    /// Registers one directed edge. Door and nav analysis pass an empty StableId; explicit links pass a stable one.
    /// </summary>
    public bool TryRegisterPortal(NCCityPortalRegistration registration, out NCCityPortalId portalId)
    {
        portalId = default;
        if (!TryValidateRegistration(registration, out var profile, out var error))
        {
            Log.Error($"Rejected city portal: {error}");
            return false;
        }

        if (registration.StableId.IsValid &&
            _stableLinks.TryGetValue(registration.StableId, out var existingId) &&
            _portals.TryGetValue(existingId, out var existing))
        {
            RemoveOutgoingIndex(existing);
            existing.Registration = registration;
            existing.Profile = profile;
            existing.Enabled = true;
            AddOutgoingIndex(existing);
            portalId = existing.Id;
            InvalidateGraph();
            return true;
        }

        if (_nextPortalId == long.MaxValue)
            throw new InvalidOperationException("The runtime city portal ID space has been exhausted.");

        portalId = new NCCityPortalId(_nextPortalId++);
        var runtime = new RuntimePortal(portalId, registration, profile);
        _portals.Add(portalId, runtime);
        AddOutgoingIndex(runtime);
        if (registration.StableId.IsValid)
            _stableLinks.Add(registration.StableId, portalId);
        InvalidateGraph();
        return true;
    }

    public bool RemovePortal(NCCityPortalId portalId)
    {
        if (!_portals.Remove(portalId, out var portal))
            return false;

        RemoveOutgoingIndex(portal);
        if (portal.Registration.StableId.IsValid)
        {
            _stableLinks.Remove(portal.Registration.StableId);
            if (_deploymentLinks.TryGetValue(portal.Registration.StableId.ScopeId, out var links))
            {
                links.Remove(portal.Registration.StableId);
                if (links.Count == 0)
                    _deploymentLinks.Remove(portal.Registration.StableId.ScopeId);
            }
        }

        InvalidateGraph();
        return true;
    }

    public bool SetPortalEnabled(NCCityPortalId portalId, bool enabled)
    {
        if (!_portals.TryGetValue(portalId, out var portal) || portal.Enabled == enabled)
            return false;

        portal.Enabled = enabled;
        InvalidateGraph();
        return true;
    }

    /// <summary>
    /// Finds only the semantic portal sequence. Each step's EntryTarget is passed to existing Robust local pathfinding.
    /// </summary>
    public bool TryFindRoute(
        NCZoneId fromZone,
        NCZoneId toZone,
        NCCityTravelMode travelMode,
        List<NCCityRouteStep> result)
    {
        return TryFindRoute(fromZone, toZone, travelMode, result, null);
    }

    /// <summary>
    /// Finds a semantic route while allowing the caller to apply actor-specific access and controller rules.
    /// Filtered routes are deliberately not shared through the global route cache.
    /// </summary>
    public bool TryFindRoute(
        NCZoneId fromZone,
        NCZoneId toZone,
        NCCityTravelMode travelMode,
        List<NCCityRouteStep> result,
        Func<NCCityPortalInfo, bool>? canTraverse)
    {
        result.Clear();
        if (!_nodes.ContainsKey(fromZone) || !_nodes.ContainsKey(toZone) || travelMode == NCCityTravelMode.None)
            return false;
        if (fromZone == toZone)
            return true;

        var key = new RouteCacheKey(fromZone, toZone, travelMode);
        if (canTraverse == null && _routeCache.TryGetValue(key, out var cached))
        {
            result.AddRange(cached);
            return true;
        }

        var frontier = new PriorityQueue<NCZoneId, float>();
        var distances = new Dictionary<NCZoneId, float> { [fromZone] = 0f };
        var previous = new Dictionary<NCZoneId, RoutePredecessor>();
        frontier.Enqueue(fromZone, 0f);

        while (frontier.TryDequeue(out var current, out var currentCost))
        {
            if (current == toZone)
                break;
            if (!distances.TryGetValue(current, out var best) || currentCost > best)
                continue;
            if (!_outgoing.TryGetValue(current, out var portalIds))
                continue;

            foreach (var portalId in portalIds)
            {
                if (!_portals.TryGetValue(portalId, out var portal) ||
                    !portal.Enabled ||
                    (portal.Profile.TravelModes & travelMode) != travelMode ||
                    canTraverse != null && !canTraverse(ToInfo(portal)))
                {
                    continue;
                }

                var edgeCost = portal.Profile.BaseTravelSeconds + portal.Profile.ThreatCost;
                var candidate = currentCost + edgeCost;
                var next = portal.Registration.ToZone;
                if (distances.TryGetValue(next, out var known) && candidate >= known)
                    continue;

                distances[next] = candidate;
                previous[next] = new RoutePredecessor(current, portalId, edgeCost);
                frontier.Enqueue(next, candidate);
            }
        }

        if (!previous.ContainsKey(toZone))
            return false;

        var reversed = new List<NCCityRouteStep>();
        var cursor = toZone;
        while (cursor != fromZone)
        {
            if (!previous.TryGetValue(cursor, out var predecessor) ||
                !_portals.TryGetValue(predecessor.PortalId, out var portal))
            {
                result.Clear();
                return false;
            }

            reversed.Add(new NCCityRouteStep(
                portal.Id,
                portal.Registration.FromZone,
                portal.Registration.ToZone,
                portal.Registration.EntryTarget,
                portal.Registration.ExitTarget,
                portal.Registration.TraversalProfile,
                portal.Registration.StableId,
                predecessor.EdgeCost,
                portal.Registration.Controller));
            cursor = predecessor.PreviousZone;
        }

        reversed.Reverse();
        var route = reversed.ToArray();
        if (canTraverse == null)
            _routeCache[key] = route;
        result.AddRange(route);
        return true;
    }

    /// <summary>
    /// Finds a materialized route using both semantic zone and logical floor. Unlike the abstract zone-only overload,
    /// this requires vertical self-links when origin and destination share one multi-floor NCZoneId.
    /// </summary>
    public bool TryFindRoute(
        NCMapCoordinates origin,
        NCMapCoordinates destination,
        NCCityTravelMode travelMode,
        List<NCCityRouteStep> result,
        Func<NCCityPortalInfo, bool>? canTraverse = null)
    {
        result.Clear();
        var fromZone = _zones.GetLocationContext(origin).MostSpecific;
        var toZone = _zones.GetLocationContext(destination).MostSpecific;
        if (!fromZone.IsValid ||
            !toZone.IsValid ||
            origin.NetworkId != destination.NetworkId ||
            travelMode == NCCityTravelMode.None)
        {
            return false;
        }

        var from = new DepthRouteNode(fromZone, origin.Z);
        var to = new DepthRouteNode(toZone, destination.Z);
        if (from == to)
            return true;

        var key = new DepthRouteCacheKey(from, to, travelMode);
        if (canTraverse == null && _depthRouteCache.TryGetValue(key, out var cached))
        {
            result.AddRange(cached);
            return true;
        }

        var frontier = new PriorityQueue<DepthRouteNode, float>();
        var distances = new Dictionary<DepthRouteNode, float> { [from] = 0f };
        var previous = new Dictionary<DepthRouteNode, DepthRoutePredecessor>();
        frontier.Enqueue(from, 0f);

        while (frontier.TryDequeue(out var current, out var currentCost))
        {
            if (current == to)
                break;
            if (!distances.TryGetValue(current, out var best) || currentCost > best ||
                !_outgoing.TryGetValue(current.ZoneId, out var portalIds))
            {
                continue;
            }

            foreach (var portalId in portalIds)
            {
                if (!_portals.TryGetValue(portalId, out var portal) ||
                    !portal.Enabled ||
                    portal.Registration.EntryTarget.Z != current.Z ||
                    (portal.Profile.TravelModes & travelMode) != travelMode ||
                    canTraverse != null && !canTraverse(ToInfo(portal)))
                {
                    continue;
                }

                var edgeCost = portal.Profile.BaseTravelSeconds + portal.Profile.ThreatCost;
                var candidate = currentCost + edgeCost;
                var next = new DepthRouteNode(portal.Registration.ToZone, portal.Registration.ExitTarget.Z);
                if (distances.TryGetValue(next, out var known) && candidate >= known)
                    continue;

                distances[next] = candidate;
                previous[next] = new DepthRoutePredecessor(current, portalId, edgeCost);
                frontier.Enqueue(next, candidate);
            }
        }

        if (!previous.ContainsKey(to))
            return false;

        var reversed = new List<NCCityRouteStep>();
        var cursor = to;
        while (cursor != from)
        {
            if (!previous.TryGetValue(cursor, out var predecessor) ||
                !_portals.TryGetValue(predecessor.PortalId, out var portal))
            {
                result.Clear();
                return false;
            }

            reversed.Add(new NCCityRouteStep(
                portal.Id,
                portal.Registration.FromZone,
                portal.Registration.ToZone,
                portal.Registration.EntryTarget,
                portal.Registration.ExitTarget,
                portal.Registration.TraversalProfile,
                portal.Registration.StableId,
                predecessor.EdgeCost,
                portal.Registration.Controller));
            cursor = predecessor.Previous;
        }

        reversed.Reverse();
        var route = reversed.ToArray();
        if (canTraverse == null)
            _depthRouteCache[key] = route;
        result.AddRange(route);
        return true;
    }

    private void OnZoneIndexRebuilt(ref NCZoneIndexRebuiltEvent args)
    {
        // A zone deployment can invalidate semantic and vertical links together. Publish one coherent revision
        // after both indexes agree instead of waking every A-Life consumer between the two rebuilds.
        using var mutationBatch = BeginMutationBatch();
        RebuildSemanticIndex();
        RebuildVerticalLinks();
    }

    private void OnDeploymentStateChanged(ref NCZoneDeploymentStateChangedEvent args)
    {
        if (args.NewState == NCZoneDeploymentState.Active)
        {
            RegisterVerticalLinks(args.DeploymentId.Value);
            return;
        }

        if (args.OldState == NCZoneDeploymentState.Active)
            RemoveDeploymentLinks(args.DeploymentId.Value);
    }

    private void OnVerticalLinkShutdown(
        Entity<NCResolvedVerticalLinkComponent> entity,
        ref ComponentShutdown args)
    {
        var stableId = new NCStableCityLinkId(entity.Comp.ScopeId, entity.Comp.LinkId);
        if (stableId.IsValid && _stableLinks.TryGetValue(stableId, out var portalId))
            RemovePortal(portalId);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<NCCityTraversalProfilePrototype>() &&
            !args.WasModified<NCCityConnectivityRulesPrototype>())
        {
            return;
        }

        using var mutationBatch = BeginMutationBatch();
        foreach (var portalId in _portals.Keys.ToArray())
        {
            var portal = _portals[portalId];
            if (_prototypes.TryIndex(portal.Registration.TraversalProfile, out var profile) && profile != null)
                portal.Profile = profile;
            else
                RemovePortal(portalId);
        }

        RebuildVerticalLinks();
    }

    private void RebuildSemanticIndex()
    {
        using var mutationBatch = BeginMutationBatch();
        _nodes.Clear();
        var zones = new List<NCZoneInfo>();
        _zones.GetZones(zones);
        foreach (var zone in zones)
        {
            if (!_zones.TryGetZoneNetwork(zone.Id, out var networkId))
                continue;

            _nodes.Add(zone.Id, new SemanticNode(networkId, zone));
        }

        foreach (var portalId in _portals.Keys.ToArray())
        {
            var portal = _portals[portalId];
            if (!_nodes.ContainsKey(portal.Registration.FromZone) ||
                !_nodes.ContainsKey(portal.Registration.ToZone))
            {
                RemovePortal(portalId);
            }
        }

        InvalidateGraph();
    }

    private void RebuildVerticalLinks()
    {
        using var mutationBatch = BeginMutationBatch();
        foreach (var scopeId in _deploymentLinks.Keys.ToArray())
            RemoveDeploymentLinks(scopeId);

        var query = EntityQueryEnumerator<NCResolvedVerticalLinkComponent>();
        var scopes = new HashSet<Guid>();
        while (query.MoveNext(out _, out var link))
        {
            if (link.ScopeId != Guid.Empty)
                scopes.Add(link.ScopeId);
        }

        foreach (var scopeId in scopes)
            RegisterVerticalLinks(scopeId);
    }

    private void RegisterVerticalLinks(Guid scopeId)
    {
        using var mutationBatch = BeginMutationBatch();
        RemoveDeploymentLinks(scopeId);
        if (!TryGetConnectivityRules(out var rules))
            return;

        var query = EntityQueryEnumerator<NCResolvedVerticalLinkComponent>();
        while (query.MoveNext(out var uid, out var link))
        {
            if (link.ScopeId != scopeId ||
                string.IsNullOrWhiteSpace(link.LinkId) ||
                !rules.VerticalProfiles.TryGetValue(link.LinkType, out var profileId) ||
                !_coordinates.TryGetCoordinates(uid, out var sourceCoordinates))
            {
                continue;
            }

            var from = _zones.GetLocationContext(sourceCoordinates).MostSpecific;
            var to = _zones.GetLocationContext(link.Target).MostSpecific;
            var stableId = new NCStableCityLinkId(scopeId, link.LinkId);
            if (!from.IsValid || !to.IsValid || !stableId.IsValid)
            {
                Log.Error($"Vertical city link {stableId} does not resolve to two semantic zones.");
                continue;
            }

            if (!TryRegisterPortal(new NCCityPortalRegistration(
                    from,
                    to,
                    sourceCoordinates,
                    link.Target,
                    NCCityPortalKind.Vertical,
                    profileId,
                    stableId,
                    uid),
                out _))
            {
                continue;
            }

            if (!_deploymentLinks.TryGetValue(scopeId, out var links))
                _deploymentLinks[scopeId] = links = [];
            links.Add(stableId);
        }
    }

    private void RemoveDeploymentLinks(Guid scopeId)
    {
        if (!_deploymentLinks.Remove(scopeId, out var links))
            return;

        using var mutationBatch = BeginMutationBatch();
        foreach (var stableId in links)
        {
            if (_stableLinks.TryGetValue(stableId, out var portalId))
                RemovePortal(portalId);
        }
    }

    private bool TryGetConnectivityRules(out NCCityConnectivityRulesPrototype rules)
    {
        var loaded = _prototypes.EnumeratePrototypes<NCCityConnectivityRulesPrototype>().ToArray();
        if (loaded.Length == 1)
        {
            rules = loaded[0];
            return true;
        }

        Log.Error($"Expected exactly one city connectivity rules prototype, found {loaded.Length}.");
        rules = default!;
        return false;
    }

    private bool TryValidateRegistration(
        NCCityPortalRegistration registration,
        out NCCityTraversalProfilePrototype profile,
        out string error)
    {
        profile = default!;
        error = string.Empty;
        if (!_nodes.TryGetValue(registration.FromZone, out var from) ||
            !_nodes.TryGetValue(registration.ToZone, out var to))
        {
            error = "one or both endpoint zones are unknown";
            return false;
        }
        if (from.NetworkId != to.NetworkId ||
            registration.EntryTarget.NetworkId != from.NetworkId ||
            registration.ExitTarget.NetworkId != to.NetworkId)
        {
            error = "links between different Z-networks are not supported";
            return false;
        }
        if (!_prototypes.TryIndex(registration.TraversalProfile, out var resolvedProfile) || resolvedProfile == null)
        {
            error = $"unknown traversal profile {registration.TraversalProfile}";
            return false;
        }
        profile = resolvedProfile;
        if (profile.TravelModes == NCCityTravelMode.None ||
            !float.IsFinite(profile.BaseTravelSeconds) || profile.BaseTravelSeconds <= 0f ||
            !float.IsFinite(profile.CapacityPerMinute) || profile.CapacityPerMinute <= 0f ||
            !float.IsFinite(profile.ThreatCost) || profile.ThreatCost < 0f)
        {
            error = $"traversal profile {registration.TraversalProfile} has invalid costs or capacity";
            return false;
        }

        return true;
    }

    private void AddOutgoingIndex(RuntimePortal portal)
    {
        if (!_outgoing.TryGetValue(portal.Registration.FromZone, out var outgoing))
            _outgoing[portal.Registration.FromZone] = outgoing = [];
        if (!outgoing.Contains(portal.Id))
            outgoing.Add(portal.Id);
    }

    private void RemoveOutgoingIndex(RuntimePortal portal)
    {
        if (!_outgoing.TryGetValue(portal.Registration.FromZone, out var outgoing))
            return;

        outgoing.Remove(portal.Id);
        if (outgoing.Count == 0)
            _outgoing.Remove(portal.Registration.FromZone);
    }

    private void InvalidateGraph()
    {
        // Cached routes are invalid immediately, even while event publication is deferred until the outer batch ends.
        _routeCache.Clear();
        _depthRouteCache.Clear();
        if (_mutationBatchDepth > 0)
        {
            _mutationBatchDirty = true;
            _deferredInvalidations++;
            return;
        }

        PublishGraphChange();
    }

    private void EndMutationBatch()
    {
        if (_mutationBatchDepth <= 0)
            throw new InvalidOperationException("City connectivity mutation batch underflow.");

        _mutationBatchDepth--;
        if (_mutationBatchDepth != 0)
            return;

        _completedMutationBatches++;
        if (!_mutationBatchDirty)
            return;

        _mutationBatchDirty = false;
        PublishGraphChange();
    }

    private void PublishGraphChange()
    {
        var oldVersion = GraphVersion;
        GraphVersion = GraphVersion == long.MaxValue ? 1 : GraphVersion + 1;
        _publishedVersionChanges++;
        var ev = new NCCityConnectivityChangedEvent(oldVersion, GraphVersion);
        RaiseLocalEvent(ref ev);
    }

    public readonly struct NCCityConnectivityMutationBatch : IDisposable
    {
        private readonly NCCityConnectivitySystem _owner;

        internal NCCityConnectivityMutationBatch(NCCityConnectivitySystem owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            _owner.EndMutationBatch();
        }
    }

    private static NCCityPortalInfo ToInfo(RuntimePortal portal)
    {
        return new NCCityPortalInfo(
            portal.Id,
            portal.Registration.FromZone,
            portal.Registration.ToZone,
            portal.Registration.EntryTarget,
            portal.Registration.ExitTarget,
            portal.Registration.Kind,
            portal.Registration.TraversalProfile,
            portal.Registration.StableId,
            portal.Profile.TravelModes,
            portal.Profile.BaseTravelSeconds,
            portal.Profile.CapacityPerMinute,
            portal.Profile.ThreatCost,
            portal.Enabled,
            portal.Registration.Controller);
    }

    private sealed record SemanticNode(NCZNetworkId NetworkId, NCZoneInfo Zone);

    private sealed class RuntimePortal(
        NCCityPortalId id,
        NCCityPortalRegistration registration,
        NCCityTraversalProfilePrototype profile)
    {
        public NCCityPortalId Id { get; } = id;
        public NCCityPortalRegistration Registration = registration;
        public NCCityTraversalProfilePrototype Profile = profile;
        public bool Enabled = true;
    }

    private readonly record struct RouteCacheKey(
        NCZoneId FromZone,
        NCZoneId ToZone,
        NCCityTravelMode TravelMode);

    private readonly record struct RoutePredecessor(
        NCZoneId PreviousZone,
        NCCityPortalId PortalId,
        float EdgeCost);

    private readonly record struct DepthRouteNode(NCZoneId ZoneId, int Z);

    private readonly record struct DepthRouteCacheKey(
        DepthRouteNode From,
        DepthRouteNode To,
        NCCityTravelMode TravelMode);

    private readonly record struct DepthRoutePredecessor(
        DepthRouteNode Previous,
        NCCityPortalId PortalId,
        float EdgeCost);
}

public readonly record struct NCCityConnectivityMutationCounters(
    long PublishedVersionChanges,
    long DeferredInvalidations,
    long CompletedMutationBatches,
    int ActiveMutationBatchDepth);

/// <summary>
/// Raised after semantic nodes, portals, costs or availability change and cached routes are invalidated.
/// </summary>
[ByRefEvent]
public readonly record struct NCCityConnectivityChangedEvent(long OldVersion, long NewVersion);
