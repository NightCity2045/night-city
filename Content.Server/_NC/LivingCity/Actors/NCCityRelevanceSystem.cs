// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Groups;
using Content.Server._NC.LivingCity.Simulation;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Server.Player;
using Robust.Shared.Asynchronous;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Builds bounded round-local player/camera clusters and moves persistent actors between Entity and strategic LOD.
/// The persistent catalog is recovered in keyset pages; hot evaluations inspect only active zone buckets and the
/// materialized binding index, never the complete actor or ECS population.
/// </summary>
public sealed partial class NCCityRelevanceSystem : EntitySystem
{
    private const string DefaultPolicyId = "NCDefaultCityRelevancePolicy";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityActorMaterializationSystem _materialization = default!;
    [Dependency] private NCCityActorDematerializationSystem _dematerialization = default!;
    [Dependency] private NCCityGroupPhysicalizationSystem _groupPhysicalization = default!;
    [Dependency] private NCCityRelevanceAnchorIndexSystem _anchorIndex = default!;
    [Dependency] private NCAILoadGovernorSystem _loadGovernor = default!;

    private readonly Dictionary<NCActorId, ActorCatalogEntry> _catalog = [];
    private readonly Dictionary<NCZoneId, ZoneBucket> _zoneBuckets = [];
    private readonly Dictionary<NCActorId, Guid> _materializationRequests = [];
    private readonly Dictionary<NCActorId, Guid> _dematerializationRequests = [];
    private readonly Dictionary<NCGroupId, Guid> _groupMaterializationRequests = [];
    private readonly Dictionary<NCGroupId, Guid> _groupDematerializationRequests = [];
    private readonly List<EntityUid> _componentAnchorBuffer = [];
    private readonly HashSet<AnchorPair> _linkedAnchorPairs = [];
    private readonly List<(NCActorId ActorId, EntityUid EntityUid)> _bindingBuffer = [];
    private readonly List<NCCityPlayerCluster> _clusters = [];
    private readonly HashSet<NCZoneId> _activeLocalZones = [];
    private readonly HashSet<NCZoneId> _activeStrategicZones = [];

    private NCCityRelevancePolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private NCActiveWorldTime _nextEvaluation;
    private int _generation;
    private int _materializationClusterCursor;
    private int _dematerializationCursor;
    private int _requiredPhysicalActors;
    private int _waitingPhysicalActors;
    private int _materializationRequestsSubmitted;
    private int _travelingCandidatesSkipped;
    private int _observerRetainedActors;
    private int _graceRetainedActors;
    private int _adminRetainedActors;
    private int _eligibilityBlockedActors;
    private int _offscreenRequests;
    private bool _catalogRecovered;
    private bool _active;

    public bool IsActive => _active;
    public bool CatalogRecovered => _catalogRecovered;
    public int CatalogActorCount => _catalog.Count;
    public int ClusterCount => _clusters.Count;
    public IReadOnlyList<NCCityPlayerCluster> Clusters => _clusters;
    public int RequiredPhysicalActors => _requiredPhysicalActors;
    public int WaitingPhysicalActors => _waitingPhysicalActors;
    public int MaterializationRequestsSubmitted => _materializationRequestsSubmitted;
    public int TravelingCandidatesSkipped => _travelingCandidatesSkipped;
    public int ObserverRetainedActors => _observerRetainedActors;
    public int GraceRetainedActors => _graceRetainedActors;
    public int AdminRetainedActors => _adminRetainedActors;
    public int EligibilityBlockedActors => _eligibilityBlockedActors;
    public int OffscreenRequests => _offscreenRequests;
    public float LocalMaterializationRadius => _policy?.DematerializationRadius ?? 0f;

    /// <summary>
    /// Returns a stable player/camera origin for an active local zone without scanning the actor population.
    /// Incident systems use it to stage bounded physical encounters near an observed street.
    /// </summary>
    public bool TryGetLocalMaterializationOrigin(NCZoneId zoneId, out NCMapCoordinates origin)
    {
        origin = default;
        if (!_active || !zoneId.IsValid || !_activeLocalZones.Contains(zoneId))
            return false;

        foreach (var cluster in _clusters)
        {
            if (!cluster.ActiveZoneIds.Contains(zoneId))
                continue;
            foreach (var anchor in cluster.AnchorEntities)
            {
                if (Exists(anchor) && _coordinates.TryGetCoordinates(anchor, out origin))
                    return true;
            }
        }

        return false;
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
        SubscribeLocalEvent<NCCityActorPersistedEvent>(OnActorPersisted);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch || now < _nextEvaluation)
        {
            return;
        }

        using var work = _loadGovernor.MeasureWork(NCAILoadWorkKind.Relevance);
        _nextEvaluation = now.Advance(_loadGovernor.ScaleEvaluationInterval(_policy.EvaluationInterval));
        ReapCompletedRequests();
        RebuildClusters();
        EvaluateMaterialization();
        EvaluateDematerialization(now);
    }

    public bool TryGetLod(NCActorId actorId, out NCCityActorLod lod)
    {
        if (_runtime.TryGetEntity(actorId, out var entityUid) && Exists(entityUid))
        {
            lod = NCCityActorLod.Entity;
            return true;
        }
        if (!_catalog.TryGetValue(actorId, out var actor))
        {
            lod = default;
            return false;
        }

        if (IsLocalZoneActive(actor.CurrentZone))
        {
            lod = NCCityActorLod.Local;
            return true;
        }

        var zone = actor.CurrentZone;
        var visited = 0;
        while (zone.IsValid && visited++ < 16 && _zones.TryGetZone(zone, out var info))
        {
            if (_activeStrategicZones.Contains(zone))
            {
                lod = NCCityActorLod.Strategic;
                return true;
            }
            zone = info.Parent;
        }

        lod = NCCityActorLod.City;
        return true;
    }

    private bool IsLocalZoneActive(NCZoneId zoneId)
    {
        var current = zoneId;
        var depth = 0;
        while (current.IsValid && depth++ < 16 && _zones.TryGetZone(current, out var zone))
        {
            if (_activeLocalZones.Contains(current))
                return true;
            current = zone.Parent;
        }
        return false;
    }

    private void RebuildClusters()
    {
        if (_policy == null)
            return;

        var anchors = CollectAnchors(_policy.MaximumAnchors);
        var roots = new int[anchors.Count];
        for (var i = 0; i < roots.Length; i++)
            roots[i] = i;

        var nextLinks = new HashSet<AnchorPair>();
        for (var left = 0; left < anchors.Count; left++)
        {
            for (var right = left + 1; right < anchors.Count; right++)
            {
                if (anchors[left].Coordinates.MapId != anchors[right].Coordinates.MapId)
                    continue;

                var pair = new AnchorPair(anchors[left].EntityUid, anchors[right].EntityUid).Normalized();
                var radius = _linkedAnchorPairs.Contains(pair)
                    ? _policy.ClusterSplitRadius
                    : _policy.ClusterMergeRadius;
                if (Vector2.DistanceSquared(
                        anchors[left].Coordinates.Position,
                        anchors[right].Coordinates.Position) > radius * radius)
                {
                    continue;
                }

                Union(roots, left, right);
                nextLinks.Add(pair);
            }
        }

        var grouped = new Dictionary<int, List<AnchorInfo>>();
        for (var i = 0; i < anchors.Count; i++)
        {
            var root = Find(roots, i);
            if (!grouped.TryGetValue(root, out var group))
            {
                group = [];
                grouped.Add(root, group);
            }
            group.Add(anchors[i]);
        }

        _runtime.GetBindings(_bindingBuffer);
        var previous = _clusters.ToArray();
        var claimedPrevious = new HashSet<Guid>();
        _clusters.Clear();
        _activeLocalZones.Clear();
        _activeStrategicZones.Clear();

        foreach (var group in grouped.Values)
        {
            var anchorIds = group.Select(static anchor => anchor.EntityUid).Order().ToArray();
            var old = previous
                .Where(cluster => cluster.MapId == group[0].Coordinates.MapId &&
                                  !claimedPrevious.Contains(cluster.ClusterId))
                .Select(cluster => (Cluster: cluster,
                    Overlap: cluster.AnchorEntities.Count(anchorIds.Contains)))
                .OrderByDescending(static candidate => candidate.Overlap)
                .FirstOrDefault();
            var clusterId = old.Overlap > 0 ? old.Cluster.ClusterId : Guid.NewGuid();
            claimedPrevious.Add(clusterId);

            var localZones = new List<NCZoneId>();
            var strategicZones = new HashSet<NCZoneId>();
            foreach (var anchor in group)
            {
                AddLocationZones(anchor.Location, localZones, strategicZones);
            }

            foreach (var zone in localZones)
                _activeLocalZones.Add(zone);
            foreach (var zone in strategicZones)
                _activeStrategicZones.Add(zone);

            var materialized = 0;
            foreach (var (_, entityUid) in _bindingBuffer)
            {
                if (!Exists(entityUid))
                    continue;
                var coordinates = _transform.GetMapCoordinates(entityUid);
                if (coordinates.MapId != group[0].Coordinates.MapId)
                    continue;
                var overlapsLocalZone = _coordinates.TryConvert(coordinates, out var stable) &&
                                        HasLocalZoneOverlap(_zones.GetLocationContext(stable), localZones);
                if (overlapsLocalZone || group.Any(anchor => Vector2.DistanceSquared(
                        anchor.Coordinates.Position,
                        coordinates.Position) <= _policy.DematerializationRadius *
                        _policy.DematerializationRadius))
                {
                    materialized++;
                }
            }

            _clusters.Add(new NCCityPlayerCluster(
                clusterId,
                group[0].Coordinates.MapId,
                anchorIds,
                localZones,
                group.Count(static anchor => anchor.PlayerAttached),
                materialized));
        }

        _linkedAnchorPairs.Clear();
        _linkedAnchorPairs.UnionWith(nextLinks);
    }

    private List<AnchorInfo> CollectAnchors(int maximum)
    {
        var result = new List<AnchorInfo>(Math.Min(maximum, 32));
        var added = new HashSet<EntityUid>();
        foreach (var session in _players.Sessions)
        {
            if (result.Count >= maximum)
                break;
            if (session.AttachedEntity is not { } attached || !Exists(attached) || !added.Add(attached) ||
                !TryGetAnchor(attached, true, out var anchor))
            {
                continue;
            }
            result.Add(anchor);
        }

        _anchorIndex.GetAnchors(_componentAnchorBuffer);
        foreach (var entityUid in _componentAnchorBuffer)
        {
            if (result.Count >= maximum)
                break;
            if (!added.Add(entityUid) || !TryComp<NCCityRelevanceAnchorComponent>(entityUid, out var component) ||
                !component.Enabled || !TryGetAnchor(entityUid, false, out var anchor))
            {
                continue;
            }
            result.Add(anchor);
        }

        return result;
    }

    private bool TryGetAnchor(EntityUid entityUid, bool playerAttached, out AnchorInfo anchor)
    {
        anchor = default;
        if (!Exists(entityUid))
            return false;
        var mapCoordinates = _transform.GetMapCoordinates(entityUid);
        if (!_coordinates.TryConvert(mapCoordinates, out var stable))
            return false;
        var location = _zones.GetLocationContext(stable);
        if (!location.IsValid || !location.MostSpecific.IsValid)
            return false;
        anchor = new AnchorInfo(entityUid, mapCoordinates, location, playerAttached);
        return true;
    }

    private static void AddLocationZones(
        NCCityLocationContext context,
        List<NCZoneId> local,
        HashSet<NCZoneId> strategic)
    {
        // Local relevance uses the most-specific authored place. Keeping all parents local would make a player
        // inside one building materialize every resident currently assigned to the containing street.
        var localZone = context.MissionSlotId.IsValid ? context.MissionSlotId.ZoneId :
            context.ApartmentId.IsValid ? context.ApartmentId.ZoneId :
            context.BuildingId.IsValid ? context.BuildingId.ZoneId :
            context.StreetId.ZoneId;
        if (localZone.IsValid && !local.Contains(localZone))
            local.Add(localZone);
        if (context.SectorId.IsValid)
            strategic.Add(context.SectorId.ZoneId);
        if (context.DistrictId.IsValid)
            strategic.Add(context.DistrictId.ZoneId);
    }

    private static bool HasLocalZoneOverlap(NCCityLocationContext context, IReadOnlyList<NCZoneId> localZones)
    {
        return context.MissionSlotId.IsValid && localZones.Contains(context.MissionSlotId.ZoneId) ||
               context.ApartmentId.IsValid && localZones.Contains(context.ApartmentId.ZoneId) ||
               context.BuildingId.IsValid && localZones.Contains(context.BuildingId.ZoneId) ||
               context.StreetId.IsValid && localZones.Contains(context.StreetId.ZoneId);
    }

    private void EvaluateMaterialization()
    {
        _travelingCandidatesSkipped = 0;
        _requiredPhysicalActors = 0;
        _waitingPhysicalActors = 0;
        _materializationRequestsSubmitted = 0;
        if (_policy == null || !_catalogRecovered || !_groupPhysicalization.CatalogRecovered)
        {
            return;
        }

        var maximumRequests = _loadGovernor.TakeAutomaticMaterializationRequests(
            _policy.MaximumMaterializationRequestsPerEvaluation);
        var requestedOperations = 0;
        var inspected = new HashSet<NCActorId>();
        if (_clusters.Count == 0)
            return;

        // Rotate the first cluster every evaluation. One busy cluster cannot permanently consume the complete
        // dispatch allowance while another observed district waits for its physical residents.
        _materializationClusterCursor %= _clusters.Count;
        for (var clusterOffset = 0; clusterOffset < _clusters.Count; clusterOffset++)
        {
            var cluster = _clusters[(_materializationClusterCursor + clusterOffset) % _clusters.Count];
            var inspections = 0;
            for (var zoneIndex = 0; zoneIndex < cluster.ActiveZoneIds.Count; zoneIndex++)
            {
                if (inspections >= _policy.MaximumCandidateInspectionsPerCluster)
                    break;
                var zoneId = cluster.ActiveZoneIds[zoneIndex];
                if (!_zoneBuckets.TryGetValue(zoneId, out var bucket))
                    continue;

                var remainingZones = cluster.ActiveZoneIds.Count - zoneIndex;
                var maximum = Math.Max(
                    1,
                    (_policy.MaximumCandidateInspectionsPerCluster - inspections) / remainingZones);
                foreach (var actorId in bucket.TakeRoundRobin(maximum))
                {
                    inspections++;
                    if (!inspected.Add(actorId) || !_catalog.TryGetValue(actorId, out var actor) ||
                        actor.Frozen || actor.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived)
                    {
                        continue;
                    }
                    // Strategic travel remains abstract until arrival. Materializing it here hands a city-scale
                    // destination to local steering, suppresses HTN and creates expensive no-path replan loops.
                    if (actor.TravelPhase != NCCityActorTravelPhase.Stationary)
                    {
                        _travelingCandidatesSkipped++;
                        continue;
                    }
                    if (actor.PresenceMode is
                        (NCCityActorPresenceMode.AbstractHome or NCCityActorPresenceMode.AbstractCustody))
                        continue;

                    _requiredPhysicalActors++;
                    if (_runtime.TryGetEntity(actorId, out _))
                        continue;
                    if (_materializationRequests.ContainsKey(actorId))
                    {
                        _waitingPhysicalActors++;
                        continue;
                    }
                    if (requestedOperations >= maximumRequests)
                    {
                        _waitingPhysicalActors++;
                        continue;
                    }

                    if (_groupPhysicalization.TryGetMaterializeTogetherGroup(
                            actorId,
                            out var groupId,
                            out _))
                    {
                        if (_groupMaterializationRequests.ContainsKey(groupId) ||
                            _groupPhysicalization.HasActiveRequest(groupId))
                        {
                            _waitingPhysicalActors++;
                            continue;
                        }
                        if (!_groupPhysicalization.CanAcceptRequest)
                        {
                            _waitingPhysicalActors++;
                            continue;
                        }

                        var groupRequestId = Guid.NewGuid();
                        var groupState = _groupPhysicalization.Request(new NCCityGroupPhysicalizationRequest(
                            groupRequestId,
                            groupId,
                            NCCityGroupPhysicalizationDirection.Materialize,
                            NCCityGroupPhysicalizationReason.PlayerRelevance,
                            _policy.PlayerRelevancePriority,
                            unchecked((uint) groupId.Value.GetHashCode()),
                            _groupPhysicalization.AutomaticRequestMaximumWait,
                            "living-city.relevance"));
                        if (IsGroupRequestActive(groupState.Status))
                        {
                            _groupMaterializationRequests[groupId] = groupRequestId;
                            requestedOperations++;
                            _materializationRequestsSubmitted++;
                        }
                        else
                            _waitingPhysicalActors++;
                        continue;
                    }

                    if (!_materialization.CanAcceptRequest)
                    {
                        _waitingPhysicalActors++;
                        continue;
                    }

                    var requestId = Guid.NewGuid();
                    var state = _materialization.Request(new NCCityMaterializationRequest(
                        requestId,
                        actorId,
                        NCCityMaterializationReason.PlayerRelevance,
                        _policy.PlayerRelevancePriority,
                        unchecked((uint) actorId.Value.GetHashCode()),
                        _materialization.AutomaticRequestMaximumWait,
                        "living-city.relevance"));
                    if (state.Status is NCCityMaterializationRequestStatus.Queued or
                        NCCityMaterializationRequestStatus.Loading or
                        NCCityMaterializationRequestStatus.Retrying)
                    {
                        _materializationRequests[actorId] = requestId;
                        requestedOperations++;
                        _materializationRequestsSubmitted++;
                    }
                    else
                        _waitingPhysicalActors++;
                }
            }
        }
        _materializationClusterCursor = (_materializationClusterCursor + 1) % _clusters.Count;
    }

    private void EvaluateDematerialization(NCActiveWorldTime now)
    {
        _observerRetainedActors = 0;
        _graceRetainedActors = 0;
        _adminRetainedActors = 0;
        _eligibilityBlockedActors = 0;
        _offscreenRequests = 0;
        if (_policy == null)
            return;

        _runtime.GetBindings(_bindingBuffer);
        if (_bindingBuffer.Count == 0)
            return;
        _dematerializationCursor %= _bindingBuffer.Count;
        var inspections = Math.Min(
            _bindingBuffer.Count,
            _policy.MaximumDematerializationInspectionsPerEvaluation);
        var requested = 0;
        for (var offset = 0; offset < inspections; offset++)
        {
            var index = (_dematerializationCursor + offset) % _bindingBuffer.Count;
            var binding = _bindingBuffer[index];
            if (_dematerializationRequests.ContainsKey(binding.ActorId))
            {
                continue;
            }

            // A lease makes materialization ownership symmetric. Player/observer proximity refreshes it; once the
            // grace period expires the physical projection must return to strategic simulation. Admin projections
            // intentionally remain manual.
            var lease = EnsureComp<NCCityMaterializationLeaseComponent>(binding.EntityUid);
            if (!lease.MaterializedAt.IsValid)
            {
                lease.Reason = NCCityMaterializationReason.PlayerRelevance;
                lease.MaterializedAt = now;
                lease.LastRelevantAt = now;
            }
            if (lease.Reason == NCCityMaterializationReason.Admin)
            {
                _adminRetainedActors++;
                continue;
            }
            if (IsWithinObserverRadius(binding.EntityUid, _policy.DematerializationRadius))
            {
                lease.LastRelevantAt = now;
                _observerRetainedActors++;
                continue;
            }
            if (lease.LastRelevantAt.IsValid &&
                lease.LastRelevantAt.Advance(_policy.OffscreenGracePeriod) > now)
            {
                _graceRetainedActors++;
                continue;
            }

            if (_groupPhysicalization.TryGetMaterializeTogetherGroup(
                    binding.ActorId,
                    out var groupId,
                    out _))
            {
                if (_groupDematerializationRequests.ContainsKey(groupId) ||
                    _groupPhysicalization.HasActiveRequest(groupId) ||
                    !_groupPhysicalization.CanAcceptRequest ||
                    !_groupPhysicalization.IsEligibleForDematerialization(groupId, out _))
                {
                    _eligibilityBlockedActors++;
                    continue;
                }
                var groupRequestId = Guid.NewGuid();
                var groupState = _groupPhysicalization.Request(new NCCityGroupPhysicalizationRequest(
                    groupRequestId,
                    groupId,
                    NCCityGroupPhysicalizationDirection.Dematerialize,
                    NCCityGroupPhysicalizationReason.PlayerRelevance,
                    500,
                    unchecked((uint) groupId.Value.GetHashCode()),
                    _groupPhysicalization.AutomaticRequestMaximumWait,
                    "living-city.relevance"));
                if (IsGroupRequestActive(groupState.Status))
                {
                    _groupDematerializationRequests[groupId] = groupRequestId;
                    requested++;
                    _offscreenRequests++;
                }
                if (requested >= _policy.MaximumDematerializationRequestsPerEvaluation)
                    break;
                continue;
            }

            if (!_dematerialization.IsEligibleForOffscreenHandOff(binding.ActorId, binding.EntityUid, out _))
            {
                _eligibilityBlockedActors++;
                continue;
            }
            if (!_dematerialization.CanAcceptRequest)
            {
                _eligibilityBlockedActors++;
                continue;
            }
            var requestId = Guid.NewGuid();
            var state = _dematerialization.Request(new NCCityDematerializationRequest(
                requestId,
                binding.ActorId,
                binding.EntityUid,
                NCCityDematerializationReason.PlayerIrrelevant,
                500,
                "living-city.relevance"));
            if (state.Status is NCCityDematerializationStatus.Requested or NCCityDematerializationStatus.Loading or
                NCCityDematerializationStatus.Capturing or NCCityDematerializationStatus.Committing or
                NCCityDematerializationStatus.Retrying)
            {
                _dematerializationRequests[binding.ActorId] = requestId;
                requested++;
                _offscreenRequests++;
            }
            if (requested >= _policy.MaximumDematerializationRequestsPerEvaluation)
                break;
        }
        _dematerializationCursor = (_dematerializationCursor + inspections) % _bindingBuffer.Count;
    }

    private bool IsWithinObserverRadius(EntityUid entityUid, float radius)
    {
        if (!Exists(entityUid))
            return false;
        var target = _transform.GetMapCoordinates(entityUid);
        var radiusSquared = radius * radius;
        foreach (var cluster in _clusters)
        {
            if (cluster.MapId != target.MapId)
                continue;

            if (_coordinates.TryConvert(target, out var stable) &&
                HasLocalZoneOverlap(_zones.GetLocationContext(stable), cluster.ActiveZoneIds))
            {
                return true;
            }

            foreach (var anchor in cluster.AnchorEntities)
            {
                if (!Exists(anchor))
                    continue;
                var coordinates = _transform.GetMapCoordinates(anchor);
                if (Vector2.DistanceSquared(target.Position, coordinates.Position) <= radiusSquared)
                    return true;
            }
        }
        return false;
    }

    private void ReapCompletedRequests()
    {
        foreach (var (actorId, requestId) in _materializationRequests.ToArray())
        {
            if (_materialization.TryGetRequest(requestId, out var state) &&
                state.Status is NCCityMaterializationRequestStatus.Queued or
                    NCCityMaterializationRequestStatus.Loading or
                    NCCityMaterializationRequestStatus.Retrying)
            {
                continue;
            }
            _materializationRequests.Remove(actorId);
        }

        foreach (var (actorId, requestId) in _dematerializationRequests.ToArray())
        {
            if (_dematerialization.TryGetRequest(requestId, out var state) &&
                state.Status is NCCityDematerializationStatus.Requested or
                    NCCityDematerializationStatus.Loading or
                    NCCityDematerializationStatus.Capturing or
                    NCCityDematerializationStatus.Committing or
                    NCCityDematerializationStatus.Retrying)
            {
                continue;
            }
            _dematerializationRequests.Remove(actorId);
        }

        foreach (var (groupId, requestId) in _groupMaterializationRequests.ToArray())
        {
            if (_groupPhysicalization.TryGetRequest(requestId, out var state) && IsGroupRequestActive(state.Status))
                continue;
            _groupMaterializationRequests.Remove(groupId);
        }

        foreach (var (groupId, requestId) in _groupDematerializationRequests.ToArray())
        {
            if (_groupPhysicalization.TryGetRequest(requestId, out var state) && IsGroupRequestActive(state.Status))
                continue;
            _groupDematerializationRequests.Remove(groupId);
        }
    }

    private async Task RecoverCatalog(int generation, int pageSize)
    {
        NCActorId? after = null;
        try
        {
            while (true)
            {
                var page = await _actors.GetPageResultAsync(after, pageSize, false);
                var records = page.Records.ToArray();
                _taskManager.RunOnMainThread(() => ApplyRecoveredPage(generation, records));
                if (!page.HasMore || page.ContinuationActorId is not { IsValid: true } continuation)
                    break;
                after = continuation;
            }
            _taskManager.RunOnMainThread(() => CompleteRecovery(generation));
        }
        catch (Exception exception)
        {
            _taskManager.RunOnMainThread(() =>
            {
                if (generation == _generation)
                    Log.Error($"Living-city relevance catalog recovery failed: {exception}");
            });
        }
    }

    private void ApplyRecoveredPage(int generation, IReadOnlyList<NCCityActorRecord> records)
    {
        if (generation != _generation || !_active)
            return;
        foreach (var record in records)
            UpsertCatalog(record);
    }

    private void CompleteRecovery(int generation)
    {
        if (generation == _generation && _active)
            _catalogRecovered = true;
    }

    private void OnActorPersisted(NCCityActorPersistedEvent args)
    {
        if (!_active || args.SessionEpoch != _epoch)
            return;
        UpsertCatalog(new ActorCatalogEntry(
            args.ActorId,
            args.AggregateVersion,
            args.CurrentZone,
            args.Status,
            args.Frozen,
            args.TravelPhase,
            args.PresenceMode));
    }

    private void UpsertCatalog(NCCityActorRecord record)
    {
        var snapshot = record.Snapshot;
        UpsertCatalog(new ActorCatalogEntry(
            snapshot.Identity.ActorId,
            record.AggregateVersion,
            snapshot.Location.CurrentZone,
            snapshot.Lifecycle.Status,
            snapshot.Lifecycle.Frozen,
            snapshot.Location.TravelPhase,
            snapshot.Schedule.PresenceMode));
    }

    private void UpsertCatalog(ActorCatalogEntry candidate)
    {
        if (_catalog.TryGetValue(candidate.ActorId, out var current) && current.Version > candidate.Version)
            return;
        if (_catalog.TryGetValue(candidate.ActorId, out current) && current.CurrentZone != candidate.CurrentZone)
            IndexActorHierarchy(candidate.ActorId, current.CurrentZone, false);

        _catalog[candidate.ActorId] = candidate;
        IndexActorHierarchy(candidate.ActorId, candidate.CurrentZone, true);
    }

    /// <summary>
    /// Indexes an actor under the exact local zone and its local ancestors. A resident in an apartment or mission
    /// slot can therefore be found from the containing building without scanning all descendants at evaluation time.
    /// </summary>
    private void IndexActorHierarchy(NCActorId actorId, NCZoneId zoneId, bool add)
    {
        var current = zoneId;
        var depth = 0;
        while (current.IsValid && depth++ < 16 && _zones.TryGetZone(current, out var zone))
        {
            if (zone.ContextSlot is NCZoneContextSlot.MissionSlot or NCZoneContextSlot.Apartment or
                NCZoneContextSlot.Building or NCZoneContextSlot.Street)
            {
                if (add)
                {
                    if (!_zoneBuckets.TryGetValue(current, out var bucket))
                        _zoneBuckets.Add(current, bucket = new ZoneBucket());
                    bucket.Add(actorId);
                }
                else if (_zoneBuckets.TryGetValue(current, out var bucket))
                {
                    bucket.Remove(actorId);
                    if (bucket.Count == 0)
                        _zoneBuckets.Remove(current);
                }
            }

            current = zone.Parent;
        }
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (!_prototypes.TryIndex(
                new ProtoId<NCCityRelevancePolicyPrototype>(DefaultPolicyId),
                out NCCityRelevancePolicyPrototype? policy) || policy == null)
        {
            Log.Error($"Actor relevance cannot start: policy {DefaultPolicyId} is missing.");
            return;
        }

        _policy = policy;
        _epoch = args.Epoch;
        _nextEvaluation = args.Time;
        _active = true;
        _ = RecoverCatalog(_generation, policy.CatalogPageSize);
    }

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args)
    {
        if (_active && args.Epoch == _epoch)
            Reset();
    }

    private void OnClockFailed(NCActiveWorldClockFailedEvent args)
    {
        if (_active && args.Epoch == _epoch)
            Reset();
    }

    private void Reset()
    {
        _generation++;
        _active = false;
        _catalogRecovered = false;
        _policy = null;
        _epoch = default;
        _nextEvaluation = default;
        _materializationClusterCursor = 0;
        _dematerializationCursor = 0;
        _requiredPhysicalActors = 0;
        _waitingPhysicalActors = 0;
        _materializationRequestsSubmitted = 0;
        _travelingCandidatesSkipped = 0;
        _observerRetainedActors = 0;
        _graceRetainedActors = 0;
        _adminRetainedActors = 0;
        _eligibilityBlockedActors = 0;
        _offscreenRequests = 0;
        _catalog.Clear();
        _zoneBuckets.Clear();
        _materializationRequests.Clear();
        _dematerializationRequests.Clear();
        _groupMaterializationRequests.Clear();
        _groupDematerializationRequests.Clear();
        _linkedAnchorPairs.Clear();
        _bindingBuffer.Clear();
        _clusters.Clear();
        _activeLocalZones.Clear();
        _activeStrategicZones.Clear();
    }

    private static int Find(int[] roots, int value)
    {
        while (roots[value] != value)
        {
            roots[value] = roots[roots[value]];
            value = roots[value];
        }
        return value;
    }

    private static bool IsGroupRequestActive(NCCityGroupPhysicalizationStatus status) =>
        status is NCCityGroupPhysicalizationStatus.Queued or
            NCCityGroupPhysicalizationStatus.Loading or
            NCCityGroupPhysicalizationStatus.Retrying or
            NCCityGroupPhysicalizationStatus.MaterializingMembers or
            NCCityGroupPhysicalizationStatus.ApplyingFormation or
            NCCityGroupPhysicalizationStatus.DematerializingMembers or
            NCCityGroupPhysicalizationStatus.RollingBack;

    private static void Union(int[] roots, int left, int right)
    {
        var leftRoot = Find(roots, left);
        var rightRoot = Find(roots, right);
        if (leftRoot != rightRoot)
            roots[rightRoot] = leftRoot;
    }

    private readonly record struct AnchorInfo(
        EntityUid EntityUid,
        MapCoordinates Coordinates,
        NCCityLocationContext Location,
        bool PlayerAttached);

    private readonly record struct AnchorPair(EntityUid Left, EntityUid Right)
    {
        public AnchorPair Normalized() => Left.CompareTo(Right) <= 0 ? this : new AnchorPair(Right, Left);
    }

    private sealed record ActorCatalogEntry(
        NCActorId ActorId,
        long Version,
        NCZoneId CurrentZone,
        NCCityActorStatus Status,
        bool Frozen,
        NCCityActorTravelPhase TravelPhase,
        NCCityActorPresenceMode PresenceMode);

    private sealed class ZoneBucket
    {
        private readonly List<NCActorId> _actors = [];
        private readonly HashSet<NCActorId> _membership = [];
        private int _cursor;

        public int Count => _actors.Count;

        public void Add(NCActorId actorId)
        {
            if (_membership.Add(actorId))
                _actors.Add(actorId);
        }

        public void Remove(NCActorId actorId)
        {
            if (!_membership.Remove(actorId))
                return;
            var index = _actors.IndexOf(actorId);
            if (index < 0)
                return;
            _actors.RemoveAt(index);
            if (_actors.Count == 0)
                _cursor = 0;
            else
                _cursor %= _actors.Count;
        }

        public IReadOnlyList<NCActorId> TakeRoundRobin(int maximum)
        {
            if (_actors.Count == 0 || maximum <= 0)
                return [];
            var count = Math.Min(maximum, _actors.Count);
            var result = new NCActorId[count];
            for (var i = 0; i < count; i++)
                result[i] = _actors[(_cursor + i) % _actors.Count];
            _cursor = (_cursor + count) % _actors.Count;
            return result;
        }
    }
}
