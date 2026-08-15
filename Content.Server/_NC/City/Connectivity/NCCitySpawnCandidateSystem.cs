// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._NC.City.Zones;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.City.Connectivity;

/// <summary>
/// Maintains deterministic, round-local materialization targets derived from Room/Portal samples. Rebuilds are
/// bounded to the grid reported by structural graph invalidation and never run from Update().
/// </summary>
public sealed class NCCitySpawnCandidateSystem : EntitySystem
{
    private static readonly Vector2[] ClearanceDirections =
    [
        Vector2.UnitX,
        -Vector2.UnitX,
        Vector2.UnitY,
        -Vector2.UnitY,
        Vector2.Normalize(Vector2.One),
        Vector2.Normalize(new Vector2(-1f, 1f)),
        Vector2.Normalize(new Vector2(1f, -1f)),
        Vector2.Normalize(-Vector2.One),
    ];

    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private readonly NCRoomPortalGraphSystem _rooms = default!;
    [Dependency] private readonly NCZoneSystem _zones = default!;
    [Dependency] private readonly SharedMapSystem _maps = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private readonly Dictionary<NCCitySpawnCandidateId, NCCitySpawnCandidateInfo> _candidates = [];
    private readonly Dictionary<EntityUid, HashSet<NCCitySpawnCandidateId>> _byGrid = [];
    private readonly Dictionary<ProtoId<NCCitySpawnCandidateProfilePrototype>, List<NCCitySpawnCandidateId>> _byProfile = [];
    private readonly HashSet<EntityUid> _analyzedGrids = [];
    private readonly List<NCRoomNavigationSample> _samples = [];
    private readonly List<NCRoomPortalInfo> _portals = [];
    private long _nextCandidateId = 1;

    public long IndexVersion { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCRoomPortalGraphChangedEvent>(OnRoomGraphChanged);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
    }

    public void GetCandidates(
        ProtoId<NCCitySpawnCandidateProfilePrototype> profile,
        NCZoneId zoneId,
        List<NCCitySpawnCandidateInfo> result)
    {
        result.Clear();
        if (!_byProfile.TryGetValue(profile, out var candidateIds))
            return;

        foreach (var candidateId in candidateIds)
        {
            if (_candidates.TryGetValue(candidateId, out var candidate) &&
                (!zoneId.IsValid || candidate.ZoneId == zoneId))
            {
                result.Add(candidate);
            }
        }

        result.Sort(CompareCandidates);
    }

    /// <summary>
    /// Deterministically selects from an already stable sorted index. A-Life can persist its own seed, never an ID.
    /// </summary>
    public bool TrySelectCandidate(
        ProtoId<NCCitySpawnCandidateProfilePrototype> profile,
        NCZoneId zoneId,
        uint selectionSeed,
        out NCCitySpawnCandidateInfo candidate,
        Func<NCCitySpawnCandidateInfo, bool>? predicate = null)
    {
        var candidates = new List<NCCitySpawnCandidateInfo>();
        GetCandidates(profile, zoneId, candidates);
        if (predicate != null)
            candidates.RemoveAll(item => !predicate(item));
        if (candidates.Count == 0)
        {
            candidate = default;
            return false;
        }

        candidate = candidates[(int) (selectionSeed % (uint) candidates.Count)];
        return true;
    }

    private void OnRoomGraphChanged(ref NCRoomPortalGraphChangedEvent args)
    {
        if (args.AffectedGrid.IsValid())
        {
            RebuildGrid(args.AffectedGrid);
            return;
        }

        foreach (var gridUid in _analyzedGrids.ToArray())
            RebuildGrid(gridUid);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<NCCitySpawnCandidateProfilePrototype>())
            return;

        foreach (var gridUid in _analyzedGrids.ToArray())
            RebuildGrid(gridUid);
    }

    private void RebuildGrid(EntityUid gridUid)
    {
        RemoveGrid(gridUid);
        _rooms.GetNavigationSamples(gridUid, _samples);
        if (_samples.Count == 0)
        {
            _analyzedGrids.Remove(gridUid);
            InvalidateIndex(gridUid);
            return;
        }

        _analyzedGrids.Add(gridUid);

        var profiles = _prototypes.EnumeratePrototypes<NCCitySpawnCandidateProfilePrototype>()
            .OrderBy(static profile => profile.ID, StringComparer.Ordinal)
            .ToArray();
        foreach (var profile in profiles)
        {
            if (!ValidateProfile(profile))
                continue;

            foreach (var roomSamples in _samples.GroupBy(static sample => sample.RoomId))
                BuildRoomProfile(gridUid, roomSamples.Key, roomSamples, profile);
        }

        InvalidateIndex(gridUid);
    }

    private void BuildRoomProfile(
        EntityUid gridUid,
        NCRoomNodeId roomId,
        IEnumerable<NCRoomNavigationSample> samples,
        NCCitySpawnCandidateProfilePrototype profile)
    {
        var ordered = samples
            .OrderBy(static sample => sample.Target.Z)
            .ThenBy(static sample => sample.Target.Position.X)
            .ThenBy(static sample => sample.Target.Position.Y)
            .ToArray();
        if (ordered.Length == 0 ||
            !_zones.TryGetZone(ordered[0].ZoneId, out var zone) ||
            !profile.AllowedZoneContexts.Contains(zone.ContextSlot))
        {
            return;
        }

        _rooms.GetPortals(roomId, _portals);
        var portalTargets = new List<NCMapCoordinates>(_portals.Count);
        foreach (var portal in _portals)
            portalTargets.Add(portal.RoomA == roomId ? portal.TargetA : portal.TargetB);

        var accepted = new List<NCMapCoordinates>();
        var spacingSquared = profile.MinimumSpacing * profile.MinimumSpacing;
        var portalExclusionSquared = profile.PortalExclusionRadius * profile.PortalExclusionRadius;
        foreach (var sample in ordered)
        {
            if (accepted.Count >= profile.MaximumPerRoom ||
                !HasClearance(sample.Target, roomId, profile.MinimumClearance) ||
                accepted.Any(existing => Vector2.DistanceSquared(existing.Position, sample.Target.Position) < spacingSquared) ||
                portalTargets.Any(portal =>
                    portal.NetworkId == sample.Target.NetworkId &&
                    portal.Z == sample.Target.Z &&
                    Vector2.DistanceSquared(portal.Position, sample.Target.Position) < portalExclusionSquared))
            {
                continue;
            }

            var candidate = new NCCitySpawnCandidateInfo(
                NextCandidateId(),
                new ProtoId<NCCitySpawnCandidateProfilePrototype>(profile.ID),
                profile.Kind,
                profile.TravelMode,
                roomId,
                gridUid,
                sample.ZoneId,
                sample.Target);
            _candidates.Add(candidate.Id, candidate);
            if (!_byGrid.TryGetValue(gridUid, out var gridCandidates))
                _byGrid[gridUid] = gridCandidates = [];
            gridCandidates.Add(candidate.Id);
            if (!_byProfile.TryGetValue(candidate.Profile, out var profileCandidates))
                _byProfile[candidate.Profile] = profileCandidates = [];
            profileCandidates.Add(candidate.Id);
            accepted.Add(sample.Target);
        }
    }

    private bool HasClearance(NCMapCoordinates center, NCRoomNodeId expectedRoom, float radius)
    {
        if (!TryResolveRoom(center, out var centerRoom) || centerRoom != expectedRoom)
            return false;
        if (radius <= 0f)
            return true;

        foreach (var direction in ClearanceDirections)
        {
            var target = new NCMapCoordinates(center.NetworkId, center.Position + direction * radius, center.Z);
            if (!TryResolveRoom(target, out var roomId) || roomId != expectedRoom)
                return false;
        }

        return true;
    }

    private bool TryResolveRoom(NCMapCoordinates target, out NCRoomNodeId roomId)
    {
        roomId = default;
        if (!_coordinates.TryResolve(target, out var mapCoordinates) ||
            !_maps.TryGetMap(mapCoordinates.MapId, out var mapUid) ||
            mapUid is not { } map ||
            !_maps.TryFindGridAt(map, mapCoordinates.Position, out var gridUid, out _))
        {
            return false;
        }

        var local = _transform.ToCoordinates((gridUid, null), mapCoordinates);
        return _rooms.TryGetRoom(local, out roomId);
    }

    private bool ValidateProfile(NCCitySpawnCandidateProfilePrototype profile)
    {
        if (profile.TravelMode == NCCityTravelMode.None ||
            profile.AllowedZoneContexts.Count == 0 ||
            !float.IsFinite(profile.MinimumSpacing) || profile.MinimumSpacing <= 0f ||
            !float.IsFinite(profile.MinimumClearance) || profile.MinimumClearance < 0f ||
            !float.IsFinite(profile.PortalExclusionRadius) || profile.PortalExclusionRadius < 0f ||
            profile.MaximumPerRoom <= 0)
        {
            Log.Error($"City spawn candidate profile {profile.ID} contains invalid constraints.");
            return false;
        }

        return true;
    }

    private void RemoveGrid(EntityUid gridUid)
    {
        if (!_byGrid.Remove(gridUid, out var candidateIds))
            return;

        foreach (var candidateId in candidateIds)
        {
            if (!_candidates.Remove(candidateId, out var candidate) ||
                !_byProfile.TryGetValue(candidate.Profile, out var profileCandidates))
            {
                continue;
            }

            profileCandidates.Remove(candidateId);
            if (profileCandidates.Count == 0)
                _byProfile.Remove(candidate.Profile);
        }
    }

    private NCCitySpawnCandidateId NextCandidateId()
    {
        if (_nextCandidateId == long.MaxValue)
            throw new InvalidOperationException("The round-local city spawn candidate ID space has been exhausted.");
        return new NCCitySpawnCandidateId(_nextCandidateId++);
    }

    private void InvalidateIndex(EntityUid gridUid)
    {
        var oldVersion = IndexVersion;
        IndexVersion = IndexVersion == long.MaxValue ? 1 : IndexVersion + 1;
        var ev = new NCCitySpawnCandidateIndexChangedEvent(oldVersion, IndexVersion, gridUid);
        RaiseLocalEvent(ref ev);
    }

    private static int CompareCandidates(NCCitySpawnCandidateInfo left, NCCitySpawnCandidateInfo right)
    {
        var z = left.Target.Z.CompareTo(right.Target.Z);
        if (z != 0)
            return z;
        var x = left.Target.Position.X.CompareTo(right.Target.Position.X);
        return x != 0 ? x : left.Target.Position.Y.CompareTo(right.Target.Position.Y);
    }
}
