// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Buffers.Binary;
using System.Numerics;
using Content.Server._NC.City.Connectivity;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Ambient.Components;
using Content.Server._NC.LivingCity.Simulation;
using Content.Server._NC.LivingCity.Time;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Ambient;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Damage.Systems;
using Robust.Server.Player;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Ambient;

/// <summary>
/// Maintains both non-physical decorative projections and simplified physical ambient pedestrians. Work is bounded by
/// player clusters and YAML limits; no persistent actor or global ECS scan is ever created.
/// </summary>
public sealed partial class NCAmbientCrowdSystem : EntitySystem
{
    private const string DefaultPolicyId = "NCDefaultAmbientCrowdPolicy";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCCitySpawnCandidateSystem _spawnCandidates = default!;
    [Dependency] private NCCityRelevanceSystem _relevance = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCAILoadGovernorSystem _governor = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;

    private readonly Dictionary<EntityUid, RuntimeMember> _members = [];
    private readonly List<NCAmbientCrowdProfilePrototype> _decorativeProfiles = [];
    private readonly List<NCAmbientCrowdProfilePrototype> _interactiveProfiles = [];
    private readonly List<NCCitySpawnCandidateInfo> _candidateBuffer = [];
    private readonly List<EntityUid> _removalBuffer = [];
    private readonly Dictionary<Guid, int> _clusterCounts = [];

    private NCAmbientCrowdPolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private NCActiveWorldTime _nextEvaluation;
    private bool _active;
    private long _spawned;
    private long _despawned;
    private long _spawnRejected;
    private long _threatReactions;
    private int _decorativeVisuals;
    private uint _sequence;
    private string? _configurationError;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
        SubscribeLocalEvent<NCAmbientCrowdMemberComponent, ComponentShutdown>(OnMemberShutdown);
        SubscribeLocalEvent<NCAmbientCrowdMemberComponent, DamageChangedEvent>(OnDamageChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch || now < _nextEvaluation)
        {
            return;
        }

        using var work = _governor.MeasureWork(NCAILoadWorkKind.Ambient);
        _nextEvaluation = now.Advance(_policy.EvaluationInterval);
        ReapInvalidMembers();
        ReassignOrphanedMembers();
        EvaluateDespawns(now);
        EvaluateSpawns(now);
        UpdateMovement(now);
        PublishDecorativeSnapshots();
    }

    public NCAmbientCrowdCounters GetCounters() => new(
        _active,
        _members.Count,
        _decorativeVisuals,
        _spawned,
        _despawned,
        _spawnRejected,
        _threatReactions,
        _configurationError);

    /// <summary>
    /// Bounded spatial witness query for crime, service and scripted domain adapters.
    /// </summary>
    public void GetWitnessCandidates(MapCoordinates origin, float radius, int limit, List<EntityUid> result)
    {
        result.Clear();
        if (!_active || _policy == null || radius <= 0f || limit <= 0)
            return;

        var maximum = Math.Min(limit, _policy.MaximumWitnessQueryResults);
        foreach (var entity in _lookup.GetEntitiesInRange(origin, radius))
        {
            if (!_members.ContainsKey(entity) || !Exists(entity))
                continue;
            result.Add(entity);
            if (result.Count >= maximum)
                break;
        }
    }

    /// <summary>
    /// Makes nearby ambient witnesses flee using local Robust steering. Callers remain authoritative for the threat.
    /// </summary>
    public int ReportThreat(MapCoordinates origin, float radius)
    {
        if (!_active || radius <= 0f || !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return 0;

        var reacted = 0;
        foreach (var entity in _lookup.GetEntitiesInRange(origin, radius))
        {
            if (!_members.TryGetValue(entity, out var member) || !Exists(entity))
                continue;
            member.ThreatOrigin = origin;
            member.Fleeing = true;
            member.NextDecisionAt = now;
            reacted++;
        }

        _threatReactions += reacted;
        return reacted;
    }

    private void OnDamageChanged(Entity<NCAmbientCrowdMemberComponent> entity, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || !_members.TryGetValue(entity.Owner, out var member) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            return;
        }

        var location = _transform.GetMapCoordinates(entity.Owner);
        member.ThreatOrigin = args.Origin is { } source && Exists(source)
            ? _transform.GetMapCoordinates(source)
            : location;
        member.Fleeing = true;
        member.NextDecisionAt = now;
        _threatReactions++;

        var witnessed = new NCAmbientWitnessedViolenceEvent(entity.Owner, args.Origin, location);
        RaiseLocalEvent(ref witnessed);
    }

    private void OnMemberShutdown(Entity<NCAmbientCrowdMemberComponent> entity, ref ComponentShutdown args)
    {
        _members.Remove(entity.Owner);
    }

    private void EvaluateSpawns(NCActiveWorldTime now)
    {
        if (_policy == null || _interactiveProfiles.Count == 0 || _governor.State == NCAILoadState.Critical)
            return;

        BuildClusterCounts();
        var density = DensityMultiplier();
        var globalTarget = Math.Min(
            _policy.MaximumInteractiveGlobal,
            _relevance.Clusters.Sum(cluster => ClusterInteractiveTarget(cluster, density)));
        var globalMissing = Math.Max(0, globalTarget - _members.Count);
        var spawnBudget = Math.Min(_policy.MaximumSpawnsPerEvaluation, globalMissing);
        if (spawnBudget == 0)
            return;

        foreach (var cluster in _relevance.Clusters)
        {
            var target = ClusterInteractiveTarget(cluster, density);
            var missing = Math.Max(0, target - _clusterCounts.GetValueOrDefault(cluster.ClusterId));
            while (missing-- > 0 && spawnBudget-- > 0)
            {
                var seed = NextSeed(cluster.ClusterId);
                var profile = PickProfile(_interactiveProfiles, seed);
                if (profile == null || !TrySpawn(cluster, profile, seed, now))
                    _spawnRejected++;
            }

            if (spawnBudget <= 0)
                break;
        }
    }

    private bool TrySpawn(
        NCCityPlayerCluster cluster,
        NCAmbientCrowdProfilePrototype profile,
        uint seed,
        NCActiveWorldTime now)
    {
        if (_policy == null || profile.EntityPrototype is not { } entityPrototype ||
            !TrySelectCandidate(cluster, profile, seed, true, out var candidate, out var spawnCoordinates))
        {
            return false;
        }

        EntityUid entity;
        try
        {
            entity = Spawn(entityPrototype, spawnCoordinates);
        }
        catch (Exception exception)
        {
            Log.Warning($"Ambient profile {profile.ID} could not spawn {entityPrototype}: {exception.Message}");
            return false;
        }

        // Ambient must never inherit persistent identity or the long-horizon HTN planner from a content parent.
        if (HasComp<NCCityActorBindingComponent>(entity))
        {
            QueueDel(entity);
            Log.Error($"Ambient profile {profile.ID} spawned a persistent actor binding; entity was rejected.");
            return false;
        }
        RemComp<HTNComponent>(entity);

        var marker = EnsureComp<NCAmbientCrowdMemberComponent>(entity);
        marker.Profile = new ProtoId<NCAmbientCrowdProfilePrototype>(profile.ID);
        marker.Kind = NCAmbientCrowdKind.Interactive;
        marker.ClusterId = cluster.ClusterId;

        var lifetime = SampleRange(profile.MinimumLifetime, profile.MaximumLifetime, Mix(seed ^ 0xA17E5u));
        var decision = SampleRange(
            profile.MinimumDecisionInterval,
            profile.MaximumDecisionInterval,
            Mix(seed ^ 0xD3C1510u));
        _members[entity] = new RuntimeMember(
            marker.Profile,
            cluster.ClusterId,
            candidate.ZoneId,
            candidate.RoomId,
            now.Advance(lifetime),
            now.Advance(decision));
        _clusterCounts[cluster.ClusterId] = _clusterCounts.GetValueOrDefault(cluster.ClusterId) + 1;
        _spawned++;
        return true;
    }

    private void EvaluateDespawns(NCActiveWorldTime now)
    {
        if (_policy == null || _members.Count == 0)
            return;

        BuildClusterCounts();
        var density = DensityMultiplier();
        var remaining = _policy.MaximumDespawnsPerEvaluation;
        _removalBuffer.Clear();
        foreach (var (entity, member) in _members)
        {
            if (remaining <= 0)
                break;
            var cluster = FindCluster(member.ClusterId);
            var excess = cluster == null ||
                _clusterCounts.GetValueOrDefault(member.ClusterId) > ClusterInteractiveTarget(cluster, density) ||
                _members.Count > _policy.MaximumInteractiveGlobal;
            if (member.ExpiresAt > now && !excess)
                continue;
            if (Exists(entity) && IsNearAttachedPlayer(_transform.GetMapCoordinates(entity),
                    _policy.DespawnVisibilityExclusionRadius))
            {
                continue;
            }

            _removalBuffer.Add(entity);
            _clusterCounts[member.ClusterId] = Math.Max(0,
                _clusterCounts.GetValueOrDefault(member.ClusterId) - 1);
            remaining--;
        }

        foreach (var entity in _removalBuffer)
            Despawn(entity);
    }

    private void UpdateMovement(NCActiveWorldTime now)
    {
        if (_policy == null)
            return;

        foreach (var (entity, member) in _members)
        {
            if (!Exists(entity) || now < member.NextDecisionAt ||
                !_prototypes.TryIndex(member.Profile, out NCAmbientCrowdProfilePrototype? profile) || profile == null)
            {
                continue;
            }

            var seed = NextSeed(member.ClusterId) ^ (uint) entity.Id;
            _spawnCandidates.GetCandidates(profile.SpawnCandidateProfile, member.ZoneId, _candidateBuffer);
            NCCitySpawnCandidateInfo? selected = null;
            var selectedDistance = member.Fleeing ? float.MinValue : float.MaxValue;
            var count = Math.Min(_candidateBuffer.Count, _policy.MaximumCandidateChecks);
            var start = _candidateBuffer.Count == 0 ? 0 : (int) (seed % (uint) _candidateBuffer.Count);
            for (var i = 0; i < count; i++)
            {
                var candidate = _candidateBuffer[(start + i) % _candidateBuffer.Count];
                if (candidate.RoomId != member.RoomId ||
                    !_coordinates.TryResolve(candidate.Target, out var resolved) ||
                    !IsRetainedByCluster(member.ClusterId, resolved))
                {
                    continue;
                }

                var reference = member.Fleeing ? member.ThreatOrigin : _transform.GetMapCoordinates(entity);
                if (reference.MapId != resolved.MapId)
                    continue;
                var distance = Vector2.DistanceSquared(reference.Position, resolved.Position);
                if (member.Fleeing ? distance <= selectedDistance : distance >= selectedDistance)
                    continue;
                selected = candidate;
                selectedDistance = distance;
            }

            if (selected is { } target && _coordinates.TryResolve(target.Target, out var mapTarget))
            {
                var coordinates = _transform.ToCoordinates((target.GridUid, null), mapTarget);
                _steering.Unregister(entity);
                var steering = _steering.Register(entity, coordinates);
                steering.Range = _policy.MovementReachRange;
                steering.ArriveOnLineOfSight = false;
                steering.Flags = profile.InteractWithDoors ? PathFlags.Interact : PathFlags.None;
            }

            member.Fleeing = false;
            member.NextDecisionAt = now.Advance(SampleRange(
                profile.MinimumDecisionInterval,
                profile.MaximumDecisionInterval,
                Mix(seed ^ 0x51A7E7u)));
        }
    }

    private void PublishDecorativeSnapshots()
    {
        if (_policy == null)
            return;

        _decorativeVisuals = 0;
        var density = DensityMultiplier();
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { } attached || !Exists(attached))
                continue;
            var cluster = _relevance.Clusters.FirstOrDefault(value => value.AnchorEntities.Contains(attached));
            if (cluster == null)
            {
                RaiseNetworkEvent(new NCAmbientDecorativeSnapshot(true, []), session);
                continue;
            }

            var requested = (int) MathF.Floor(
                (_policy.DecorativePerCluster + cluster.AttachedPlayerCount * _policy.DecorativePerPlayer) * density);
            var target = Math.Clamp(requested, 0, _policy.MaximumDecorativePerClient);
            var visuals = new List<NCAmbientDecorativeVisual>(target);
            for (var index = 0; index < target; index++)
            {
                var seed = Mix(StableSeed(cluster.ClusterId) ^ (uint) index * 0x9E3779B9u);
                var profile = PickProfile(_decorativeProfiles, seed);
                if (profile == null || !TrySelectCandidate(cluster, profile, seed, false, out var candidate, out _))
                    continue;
                visuals.Add(new NCAmbientDecorativeVisual(
                    candidate.Target,
                    seed,
                    profile.SilhouetteColor,
                    profile.SilhouetteScale,
                    profile.SilhouetteMotionRadius,
                    profile.SilhouetteMotionSpeed));
            }

            _decorativeVisuals += visuals.Count;
            RaiseNetworkEvent(new NCAmbientDecorativeSnapshot(true, visuals.ToArray()), session);
        }
    }

    private bool TrySelectCandidate(
        NCCityPlayerCluster cluster,
        NCAmbientCrowdProfilePrototype profile,
        uint seed,
        bool hiddenAndUnoccupied,
        out NCCitySpawnCandidateInfo selected,
        out EntityCoordinates coordinates)
    {
        selected = default;
        coordinates = default;
        if (_policy == null || cluster.ActiveZoneIds.Count == 0)
            return false;

        var checks = 0;
        var zoneStart = (int) (seed % (uint) cluster.ActiveZoneIds.Count);
        for (var zoneOffset = 0; zoneOffset < cluster.ActiveZoneIds.Count; zoneOffset++)
        {
            var zone = cluster.ActiveZoneIds[(zoneStart + zoneOffset) % cluster.ActiveZoneIds.Count];
            _spawnCandidates.GetCandidates(profile.SpawnCandidateProfile, zone, _candidateBuffer);
            if (_candidateBuffer.Count == 0)
                continue;
            var start = (int) (Mix(seed ^ (uint) zoneOffset) % (uint) _candidateBuffer.Count);
            for (var index = 0; index < _candidateBuffer.Count && checks++ < _policy.MaximumCandidateChecks; index++)
            {
                var candidate = _candidateBuffer[(start + index) % _candidateBuffer.Count];
                if (!_coordinates.TryResolve(candidate.Target, out var mapCoordinates) ||
                    mapCoordinates.MapId != cluster.MapId || !IsRetainedByCluster(cluster.ClusterId, mapCoordinates))
                {
                    continue;
                }
                if (hiddenAndUnoccupied &&
                    (IsNearAttachedPlayer(mapCoordinates, _policy.SpawnVisibilityExclusionRadius) ||
                     IsOccupied(mapCoordinates, _policy.OccupancyExclusionRadius)))
                {
                    continue;
                }

                selected = candidate;
                coordinates = _transform.ToCoordinates((candidate.GridUid, null), mapCoordinates);
                return true;
            }
            if (checks >= _policy.MaximumCandidateChecks)
                break;
        }

        return false;
    }

    private bool IsOccupied(MapCoordinates coordinates, float radius)
    {
        foreach (var entity in _lookup.GetEntitiesInRange(coordinates, radius))
        {
            if (HasComp<NCCityActorBindingComponent>(entity) || HasComp<NCAmbientCrowdMemberComponent>(entity))
                return true;
        }
        return false;
    }

    private bool IsNearAttachedPlayer(MapCoordinates coordinates, float radius)
    {
        var radiusSquared = radius * radius;
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { } attached || !Exists(attached))
                continue;
            var player = _transform.GetMapCoordinates(attached);
            if (player.MapId == coordinates.MapId &&
                Vector2.DistanceSquared(player.Position, coordinates.Position) <= radiusSquared)
            {
                return true;
            }
        }
        return false;
    }

    private bool IsRetainedByCluster(Guid clusterId, MapCoordinates coordinates)
    {
        if (_policy == null || FindCluster(clusterId) is not { } cluster)
            return false;
        var radiusSquared = _policy.ClusterRetentionRadius * _policy.ClusterRetentionRadius;
        foreach (var anchor in cluster.AnchorEntities)
        {
            if (!Exists(anchor))
                continue;
            var anchorCoordinates = _transform.GetMapCoordinates(anchor);
            if (anchorCoordinates.MapId == coordinates.MapId &&
                Vector2.DistanceSquared(anchorCoordinates.Position, coordinates.Position) <= radiusSquared)
            {
                return true;
            }
        }
        return false;
    }

    private void ReassignOrphanedMembers()
    {
        foreach (var (entity, member) in _members)
        {
            if (!Exists(entity) || FindCluster(member.ClusterId) != null)
                continue;
            var coordinates = _transform.GetMapCoordinates(entity);
            foreach (var cluster in _relevance.Clusters)
            {
                if (!IsWithinCluster(cluster, coordinates))
                    continue;
                member.ClusterId = cluster.ClusterId;
                if (TryComp<NCAmbientCrowdMemberComponent>(entity, out var marker))
                    marker.ClusterId = cluster.ClusterId;
                break;
            }
        }
    }

    private bool IsWithinCluster(NCCityPlayerCluster cluster, MapCoordinates coordinates)
    {
        if (_policy == null || cluster.MapId != coordinates.MapId)
            return false;
        var radiusSquared = _policy.ClusterRetentionRadius * _policy.ClusterRetentionRadius;
        return cluster.AnchorEntities.Any(anchor => Exists(anchor) &&
            Vector2.DistanceSquared(_transform.GetMapCoordinates(anchor).Position, coordinates.Position) <= radiusSquared);
    }

    private NCCityPlayerCluster? FindCluster(Guid id) =>
        _relevance.Clusters.FirstOrDefault(cluster => cluster.ClusterId == id);

    private void BuildClusterCounts()
    {
        _clusterCounts.Clear();
        foreach (var member in _members.Values)
            _clusterCounts[member.ClusterId] = _clusterCounts.GetValueOrDefault(member.ClusterId) + 1;
    }

    private int ClusterInteractiveTarget(NCCityPlayerCluster cluster, float density)
    {
        if (_policy == null)
            return 0;
        var requested = _policy.InteractivePerCluster + cluster.AttachedPlayerCount * _policy.InteractivePerPlayer;
        return Math.Clamp((int) MathF.Floor(requested * density), 0, _policy.MaximumInteractivePerCluster);
    }

    private float DensityMultiplier() => _governor.State switch
    {
        NCAILoadState.Elevated => _policy?.ElevatedDensityMultiplier ?? 0f,
        NCAILoadState.Critical => _policy?.CriticalDensityMultiplier ?? 0f,
        _ => 1f,
    };

    private void Despawn(EntityUid entity)
    {
        if (!_members.Remove(entity))
            return;
        if (Exists(entity))
        {
            _steering.Unregister(entity);
            QueueDel(entity);
        }
        _despawned++;
    }

    private void ReapInvalidMembers()
    {
        _removalBuffer.Clear();
        foreach (var entity in _members.Keys)
        {
            if (!Exists(entity))
                _removalBuffer.Add(entity);
        }
        foreach (var entity in _removalBuffer)
            _members.Remove(entity);
    }

    private static NCAmbientCrowdProfilePrototype? PickProfile(
        IReadOnlyList<NCAmbientCrowdProfilePrototype> profiles,
        uint seed)
    {
        var total = profiles.Sum(profile => profile.Weight);
        if (total <= 0)
            return null;
        var value = (int) (seed % (uint) total);
        foreach (var profile in profiles)
        {
            if (value < profile.Weight)
                return profile;
            value -= profile.Weight;
        }
        return profiles[^1];
    }

    private uint NextSeed(Guid clusterId) => Mix(StableSeed(clusterId) ^ ++_sequence);

    private static uint StableSeed(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes) ^ BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
    }

    private static uint Mix(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        return value ^ (value >> 16);
    }

    private static TimeSpan SampleRange(TimeSpan minimum, TimeSpan maximum, uint seed)
    {
        if (maximum <= minimum)
            return minimum;
        var ratio = seed / (float) uint.MaxValue;
        return TimeSpan.FromTicks(minimum.Ticks + (long) ((maximum.Ticks - minimum.Ticks) * ratio));
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset(false);
        _epoch = args.Epoch;
        _nextEvaluation = args.Time;
        if (!_prototypes.TryIndex(
                new ProtoId<NCAmbientCrowdPolicyPrototype>(DefaultPolicyId),
                out NCAmbientCrowdPolicyPrototype? policy) || policy == null)
        {
            _configurationError = $"Ambient crowd policy {DefaultPolicyId} is missing.";
            Log.Error(_configurationError);
            return;
        }

        // A disabled policy owns no ECS entities and publishes no decorative overlay. This lets authored persistent
        // A-Life populations replace both ambient layers without deleting the reusable bounded crowd subsystem.
        if (!policy.Enabled)
            return;

        if (!ValidatePolicy(policy))
        {
            _configurationError = $"Ambient crowd policy {DefaultPolicyId} is invalid.";
            Log.Error(_configurationError);
            return;
        }

        foreach (var profileId in policy.Profiles)
        {
            if (!_prototypes.TryIndex(profileId, out NCAmbientCrowdProfilePrototype? profile) || profile == null ||
                !ValidateProfile(profile))
            {
                _configurationError = $"Ambient crowd profile {profileId} is missing or invalid.";
                Log.Error(_configurationError);
                _decorativeProfiles.Clear();
                _interactiveProfiles.Clear();
                return;
            }
            (profile.Kind == NCAmbientCrowdKind.Decorative ? _decorativeProfiles : _interactiveProfiles).Add(profile);
        }

        if (_decorativeProfiles.Count == 0 || _interactiveProfiles.Count == 0)
        {
            _configurationError = "Ambient crowd requires at least one decorative and one interactive profile.";
            Log.Error(_configurationError);
            return;
        }

        _policy = policy;
        _active = true;
    }

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args)
    {
        if (_epoch == args.Epoch)
            Reset(true);
    }

    private void OnClockFailed(NCActiveWorldClockFailedEvent args)
    {
        if (_epoch == args.Epoch)
            Reset(true);
    }

    private bool ValidateProfile(NCAmbientCrowdProfilePrototype profile)
    {
        return profile.Weight > 0 &&
               profile.MinimumLifetime > TimeSpan.Zero && profile.MaximumLifetime >= profile.MinimumLifetime &&
               profile.MinimumDecisionInterval > TimeSpan.Zero &&
               profile.MaximumDecisionInterval >= profile.MinimumDecisionInterval &&
               float.IsFinite(profile.SilhouetteScale) && profile.SilhouetteScale > 0f &&
               float.IsFinite(profile.SilhouetteMotionRadius) && profile.SilhouetteMotionRadius >= 0f &&
               float.IsFinite(profile.SilhouetteMotionSpeed) && profile.SilhouetteMotionSpeed >= 0f &&
               (profile.Kind == NCAmbientCrowdKind.Decorative && profile.EntityPrototype == null ||
                profile.Kind == NCAmbientCrowdKind.Interactive && profile.EntityPrototype is { } entity &&
                _prototypes.HasIndex<EntityPrototype>(entity));
    }

    private static bool ValidatePolicy(NCAmbientCrowdPolicyPrototype policy) =>
        policy.Profiles.Count is > 0 and <= 64 && policy.EvaluationInterval > TimeSpan.Zero &&
        policy.DecorativePerCluster >= 0 && policy.DecorativePerPlayer >= 0 &&
        policy.MaximumDecorativePerClient is > 0 and <= 256 &&
        policy.InteractivePerCluster >= 0 && policy.InteractivePerPlayer >= 0 &&
        policy.MaximumInteractivePerCluster is > 0 and <= 64 &&
        policy.MaximumInteractiveGlobal is > 0 and <= 256 &&
        float.IsFinite(policy.ElevatedDensityMultiplier) && policy.ElevatedDensityMultiplier is >= 0f and <= 1f &&
        float.IsFinite(policy.CriticalDensityMultiplier) && policy.CriticalDensityMultiplier is >= 0f and <= 1f &&
        policy.CriticalDensityMultiplier <= policy.ElevatedDensityMultiplier &&
        policy.MaximumSpawnsPerEvaluation is > 0 and <= 32 &&
        policy.MaximumDespawnsPerEvaluation is > 0 and <= 64 &&
        policy.MaximumCandidateChecks is > 0 and <= 256 &&
        FinitePositive(policy.SpawnVisibilityExclusionRadius) &&
        FinitePositive(policy.DespawnVisibilityExclusionRadius) &&
        FinitePositive(policy.OccupancyExclusionRadius) && FinitePositive(policy.ClusterRetentionRadius) &&
        FinitePositive(policy.MovementReachRange) && policy.MaximumWitnessQueryResults is > 0 and <= 128;

    private static bool FinitePositive(float value) => float.IsFinite(value) && value > 0f;

    private void Reset(bool notifyClients)
    {
        if (notifyClients)
        {
            foreach (var session in _players.Sessions)
                RaiseNetworkEvent(new NCAmbientDecorativeSnapshot(false, []), session);
        }
        foreach (var entity in _members.Keys.ToArray())
            Despawn(entity);
        _members.Clear();
        _decorativeProfiles.Clear();
        _interactiveProfiles.Clear();
        _candidateBuffer.Clear();
        _removalBuffer.Clear();
        _clusterCounts.Clear();
        _policy = null;
        _epoch = default;
        _nextEvaluation = default;
        _active = false;
        _sequence = 0;
        _decorativeVisuals = 0;
        _spawned = 0;
        _despawned = 0;
        _spawnRejected = 0;
        _threatReactions = 0;
        _configurationError = null;
    }

    private sealed class RuntimeMember(
        ProtoId<NCAmbientCrowdProfilePrototype> profile,
        Guid clusterId,
        NCZoneId zoneId,
        NCRoomNodeId roomId,
        NCActiveWorldTime expiresAt,
        NCActiveWorldTime nextDecisionAt)
    {
        public ProtoId<NCAmbientCrowdProfilePrototype> Profile { get; } = profile;
        public Guid ClusterId { get; set; } = clusterId;
        public NCZoneId ZoneId { get; } = zoneId;
        public NCRoomNodeId RoomId { get; } = roomId;
        public NCActiveWorldTime ExpiresAt { get; } = expiresAt;
        public NCActiveWorldTime NextDecisionAt { get; set; } = nextDecisionAt;
        public MapCoordinates ThreatOrigin { get; set; }
        public bool Fleeing { get; set; }
    }
}
