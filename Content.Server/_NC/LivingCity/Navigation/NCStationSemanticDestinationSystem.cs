// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Time;
using Content.Server.Spawners.Components;
using Content.Server.Station.Systems;
using Content.Shared.Pinpointer;
using Content.Shared.Roles;
using Content.Shared._NC.LivingCity.Navigation;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.LivingCity.Navigation;

/// <summary>
/// Builds a bounded station-only navigation vocabulary from real job spawn points, department definitions and
/// navigation beacons. Handles are opaque, actor-scoped and round-local; only the adapter may resolve them back to
/// live coordinates. This is a fallback for ordinary station maps and has no CityGraph dependency.
/// </summary>
public sealed partial class NCStationSemanticDestinationSystem : EntitySystem
{
    public static readonly ProtoId<NCStationSemanticDestinationPolicyPrototype> DefaultPolicy =
        new("NCDefaultStationSemanticDestinations");

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private StationSystem _stations = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private readonly Dictionary<EntityUid, StationCatalog> _catalogs = [];
    private readonly Dictionary<EntityUid, List<HandleScope>> _handleScopes = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<StationInitializedEvent>(OnStationInitialized);
        SubscribeLocalEvent<StationGridAddedEvent>(OnStationGridAdded);
        SubscribeLocalEvent<StationGridRemovedEvent>(OnStationGridRemoved);
        SubscribeLocalEvent<SpawnPointComponent, ComponentStartup>(OnSpawnPointStartup);
        SubscribeLocalEvent<SpawnPointComponent, ComponentShutdown>(OnSpawnPointShutdown);
        SubscribeLocalEvent<NavMapBeaconComponent, ComponentStartup>(OnBeaconStartup);
        SubscribeLocalEvent<NavMapBeaconComponent, ComponentShutdown>(OnBeaconShutdown);
        SubscribeLocalEvent<NCCityActorBindingChangedEvent>(OnBindingChanged);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Clear());
    }

    /// <summary>
    /// Creates a short-lived request scope and appends only serializable semantic data to <paramref name="output"/>.
    /// A small bounded number of prior scopes remains resolvable while queued neural requests finish.
    /// Returns zero when the actor is not on a station or the station has no usable authored anchors.
    /// </summary>
    public int BuildFor(EntityUid actor, List<NCStationSemanticDestination> output) =>
        BuildFor(actor, DefaultPolicy, output);

    public int BuildFor(
        EntityUid actor,
        ProtoId<NCStationSemanticDestinationPolicyPrototype> policyId,
        List<NCStationSemanticDestination> output)
    {
        output.Clear();
        if (!Exists(actor) ||
            !_prototypes.TryIndex(policyId, out NCStationSemanticDestinationPolicyPrototype? policy) ||
            !Valid(policy) ||
            _stations.GetOwningStation(actor) is not { } station)
        {
            return 0;
        }

        var actorCoordinates = _transform.GetMapCoordinates(actor);
        if (actorCoordinates.MapId == MapId.Nullspace)
            return 0;

        var catalog = GetOrBuildCatalog(station);
        var assignedJob = TryComp<NCNpcJobComponent>(actor, out var jobMarker)
            ? jobMarker.Job
            : (ProtoId<JobPrototype>?) null;
        var workplaces = BuildWorkplaces(station, actorCoordinates, catalog, assignedJob);
        var departments = BuildDepartments(workplaces, assignedJob);
        var landmarks = BuildLandmarks(station, actorCoordinates, catalog);
        if (workplaces.Count == 0 && departments.Count == 0 && landmarks.Count == 0)
            return 0;

        var selected = new List<RankedDestination>(policy.MaximumDestinations);
        var semanticKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Always reserve the first workplace slot for the NPC's authored job, if the map supplies one.
        AddSelected(selected, semanticKeys,
            workplaces.Where(value => value.AssignedJob).Take(1),
            policy.MaximumDestinations);
        AddSelected(selected, semanticKeys,
            departments.Take(policy.MaximumDepartments),
            policy.MaximumDestinations);
        AddSelected(selected, semanticKeys,
            landmarks.Take(policy.MaximumLandmarks),
            policy.MaximumDestinations);
        AddSelected(selected, semanticKeys,
            workplaces.Where(value => !value.AssignedJob)
                .Take(Math.Max(0, policy.MaximumWorkplaces - selected.Count(value => value.Kind == NCStationSemanticDestinationKind.Workplace))),
            policy.MaximumDestinations);

        var handles = new Dictionary<string, EntityUid>(selected.Count, StringComparer.Ordinal);
        foreach (var destination in selected)
        {
            var handle = $"station-destination:{Guid.NewGuid():N}";
            handles.Add(handle, destination.Anchor);
            output.Add(new NCStationSemanticDestination(
                handle,
                destination.Label,
                destination.Kind,
                destination.AssignedJob,
                destination.DistanceMeters));
        }

        if (handles.Count != 0)
        {
            if (!_handleScopes.TryGetValue(actor, out var scopes))
            {
                scopes = [];
                _handleScopes.Add(actor, scopes);
            }
            PruneExpired(scopes);
            scopes.Add(new HandleScope(
                station,
                _timing.CurTime + policy.HandleLifetime,
                handles));
            while (scopes.Count > policy.MaximumRetainedScopes)
                scopes.RemoveAt(0);
        }

        return output.Count;
    }

    /// <summary>
    /// Resolves one opaque handle only for the actor and request scope that received it. The returned coordinate is
    /// sampled from the live anchor so station grid movement does not leave a durable or stale location behind.
    /// </summary>
    public bool TryResolve(EntityUid actor, string handle, out MapCoordinates coordinates)
    {
        coordinates = default;
        if (!Exists(actor) || string.IsNullOrWhiteSpace(handle) ||
            !_handleScopes.TryGetValue(actor, out var scopes))
        {
            return false;
        }

        PruneExpired(scopes);
        if (scopes.Count == 0)
        {
            _handleScopes.Remove(actor);
            return false;
        }

        var station = _stations.GetOwningStation(actor);
        for (var index = scopes.Count - 1; index >= 0; index--)
        {
            var scope = scopes[index];
            if (station != scope.Station || !scope.Anchors.TryGetValue(handle, out var anchor) ||
                !Exists(anchor) || !TryComp(anchor, out TransformComponent? transform) ||
                _stations.GetOwningStation(anchor, transform) != scope.Station)
            {
                continue;
            }

            coordinates = _transform.GetMapCoordinates(anchor, transform);
            return coordinates.MapId != MapId.Nullspace;
        }

        return false;
    }

    private void PruneExpired(List<HandleScope> scopes)
    {
        for (var index = scopes.Count - 1; index >= 0; index--)
        {
            if (_timing.CurTime > scopes[index].ExpiresAt)
                scopes.RemoveAt(index);
        }
    }

    private List<RankedDestination> BuildWorkplaces(
        EntityUid station,
        MapCoordinates actorCoordinates,
        StationCatalog catalog,
        ProtoId<JobPrototype>? assignedJob)
    {
        var result = new List<RankedDestination>(catalog.JobSpawns.Count);
        foreach (var (jobId, anchors) in catalog.JobSpawns)
        {
            if (!_prototypes.TryIndex(jobId, out JobPrototype? job) || job == null ||
                !TryFindNearest(station, actorCoordinates, anchors, out var anchor, out var distance))
            {
                continue;
            }

            var isAssigned = assignedJob == jobId;
            var label = NormalizeLabel(
                Loc.GetString("nc-station-semantic-destination-workplace", ("job", job.LocalizedName)));
            if (label.Length == 0)
                continue;
            result.Add(new RankedDestination(
                anchor,
                label,
                NCStationSemanticDestinationKind.Workplace,
                isAssigned,
                distance,
                jobId));
        }

        result.Sort(static (left, right) =>
        {
            var assigned = right.AssignedJob.CompareTo(left.AssignedJob);
            if (assigned != 0)
                return assigned;
            var distance = left.DistanceMeters.CompareTo(right.DistanceMeters);
            return distance != 0 ? distance : string.CompareOrdinal(left.Label, right.Label);
        });
        return result;
    }

    private List<RankedDestination> BuildDepartments(
        IReadOnlyList<RankedDestination> workplaces,
        ProtoId<JobPrototype>? assignedJob)
    {
        var byJob = new Dictionary<ProtoId<JobPrototype>, RankedDestination>();
        foreach (var workplace in workplaces)
        {
            if (workplace.Job is { } job)
                byJob[job] = workplace;
        }

        var result = new List<RankedDestination>();
        foreach (var department in _prototypes.EnumeratePrototypes<DepartmentPrototype>())
        {
            RankedDestination? nearest = null;
            foreach (var role in department.Roles)
            {
                if (!byJob.TryGetValue(role, out var workplace) ||
                    nearest is { } current && current.DistanceMeters <= workplace.DistanceMeters)
                {
                    continue;
                }

                nearest = workplace;
            }

            if (nearest is not { } destination)
                continue;
            var label = NormalizeLabel(Loc.GetString(department.Name));
            if (label.Length == 0)
                continue;
            result.Add(new RankedDestination(
                destination.Anchor,
                label,
                NCStationSemanticDestinationKind.Department,
                assignedJob is { } job && department.Roles.Contains(job),
                destination.DistanceMeters));
        }

        result.Sort(static (left, right) =>
        {
            var assigned = right.AssignedJob.CompareTo(left.AssignedJob);
            if (assigned != 0)
                return assigned;
            var distance = left.DistanceMeters.CompareTo(right.DistanceMeters);
            return distance != 0 ? distance : string.CompareOrdinal(left.Label, right.Label);
        });
        return result;
    }

    private List<RankedDestination> BuildLandmarks(
        EntityUid station,
        MapCoordinates actorCoordinates,
        StationCatalog catalog)
    {
        var byLabel = new Dictionary<string, RankedDestination>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in catalog.Beacons)
        {
            if (!TryComp<NavMapBeaconComponent>(anchor, out var beacon) || !beacon.Enabled ||
                !TryGetLocation(station, actorCoordinates, anchor, out var distance))
            {
                continue;
            }

            var label = beacon.Text;
            if (string.IsNullOrWhiteSpace(label))
                label = Name(anchor);
            label = NormalizeLabel(label);
            if (label.Length == 0)
                continue;

            var candidate = new RankedDestination(
                anchor,
                label,
                NCStationSemanticDestinationKind.Landmark,
                false,
                distance);
            if (!byLabel.TryGetValue(label, out var current) || candidate.DistanceMeters < current.DistanceMeters)
                byLabel[label] = candidate;
        }

        var result = byLabel.Values.ToList();
        result.Sort(static (left, right) =>
        {
            var distance = left.DistanceMeters.CompareTo(right.DistanceMeters);
            return distance != 0 ? distance : string.CompareOrdinal(left.Label, right.Label);
        });
        return result;
    }

    private bool TryFindNearest(
        EntityUid station,
        MapCoordinates actorCoordinates,
        IReadOnlyList<EntityUid> anchors,
        out EntityUid nearest,
        out float distance)
    {
        nearest = default;
        distance = float.PositiveInfinity;
        foreach (var anchor in anchors)
        {
            if (!TryGetLocation(station, actorCoordinates, anchor, out var candidateDistance) ||
                candidateDistance >= distance)
            {
                continue;
            }

            nearest = anchor;
            distance = candidateDistance;
        }

        return nearest.IsValid();
    }

    private bool TryGetLocation(
        EntityUid station,
        MapCoordinates actorCoordinates,
        EntityUid anchor,
        out float distance)
    {
        distance = 0f;
        if (!Exists(anchor) || !TryComp(anchor, out TransformComponent? transform) ||
            _stations.GetOwningStation(anchor, transform) != station)
        {
            return false;
        }

        var coordinates = _transform.GetMapCoordinates(anchor, transform);
        if (coordinates.MapId == MapId.Nullspace || coordinates.MapId != actorCoordinates.MapId)
            return false;
        distance = (coordinates.Position - actorCoordinates.Position).Length();
        return float.IsFinite(distance);
    }

    private StationCatalog GetOrBuildCatalog(EntityUid station)
    {
        if (_catalogs.TryGetValue(station, out var existing))
            return existing;

        var catalog = new StationCatalog();
        var spawns = EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();
        while (spawns.MoveNext(out var uid, out var spawn, out var transform))
        {
            if (spawn.SpawnType != SpawnPointType.Job || spawn.Job is not { } job ||
                _stations.GetOwningStation(uid, transform) != station)
            {
                continue;
            }

            if (!catalog.JobSpawns.TryGetValue(job, out var anchors))
            {
                anchors = [];
                catalog.JobSpawns.Add(job, anchors);
            }
            anchors.Add(uid);
        }

        var beacons = EntityQueryEnumerator<NavMapBeaconComponent, TransformComponent>();
        while (beacons.MoveNext(out var uid, out _, out var transform))
        {
            if (_stations.GetOwningStation(uid, transform) == station)
                catalog.Beacons.Add(uid);
        }

        _catalogs[station] = catalog;
        return catalog;
    }

    private static void AddSelected(
        List<RankedDestination> selected,
        HashSet<string> semanticKeys,
        IEnumerable<RankedDestination> candidates,
        int maximum)
    {
        foreach (var candidate in candidates)
        {
            if (selected.Count >= maximum)
                return;
            var key = $"{candidate.Kind}:{candidate.Label}";
            if (semanticKeys.Add(key))
                selected.Add(candidate);
        }
    }

    private static bool Valid(NCStationSemanticDestinationPolicyPrototype policy) =>
        policy.MaximumDestinations is > 0 and <= 128 &&
        policy.MaximumWorkplaces is >= 0 and <= 128 &&
        policy.MaximumDepartments is >= 0 and <= 128 &&
        policy.MaximumLandmarks is >= 0 and <= 128 &&
        policy.HandleLifetime > TimeSpan.Zero && policy.HandleLifetime <= TimeSpan.FromHours(1) &&
        policy.MaximumRetainedScopes is >= 2 and <= 8;

    private static string NormalizeLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return new string(value.Where(character => !char.IsControl(character)).Take(128).ToArray()).Trim();
    }

    private void OnStationInitialized(StationInitializedEvent args) => InvalidateStation(args.Station);

    private void OnStationGridAdded(StationGridAddedEvent args) => InvalidateStation(args.Station);

    private void OnStationGridRemoved(StationGridRemovedEvent args) => InvalidateStation(args.Station);

    private void OnSpawnPointStartup(Entity<SpawnPointComponent> entity, ref ComponentStartup args) =>
        _catalogs.Clear();

    private void OnSpawnPointShutdown(Entity<SpawnPointComponent> entity, ref ComponentShutdown args) =>
        _catalogs.Clear();

    private void OnBeaconStartup(Entity<NavMapBeaconComponent> entity, ref ComponentStartup args) =>
        _catalogs.Clear();

    private void OnBeaconShutdown(Entity<NavMapBeaconComponent> entity, ref ComponentShutdown args) =>
        _catalogs.Clear();

    private void OnBindingChanged(NCCityActorBindingChangedEvent args)
    {
        if (!args.Bound)
            _handleScopes.Remove(args.EntityUid);
    }

    private void InvalidateStation(EntityUid station)
    {
        _catalogs.Remove(station);
        foreach (var (actor, scopes) in _handleScopes.ToArray())
        {
            scopes.RemoveAll(scope => scope.Station == station);
            if (scopes.Count == 0)
                _handleScopes.Remove(actor);
        }
    }

    private void Clear()
    {
        _catalogs.Clear();
        _handleScopes.Clear();
    }

    private sealed class StationCatalog
    {
        public readonly Dictionary<ProtoId<JobPrototype>, List<EntityUid>> JobSpawns = [];
        public readonly List<EntityUid> Beacons = [];
    }

    private sealed record HandleScope(
        EntityUid Station,
        TimeSpan ExpiresAt,
        Dictionary<string, EntityUid> Anchors);

    private readonly record struct RankedDestination(
        EntityUid Anchor,
        string Label,
        NCStationSemanticDestinationKind Kind,
        bool AssignedJob,
        float DistanceMeters,
        ProtoId<JobPrototype>? Job = null);
}
