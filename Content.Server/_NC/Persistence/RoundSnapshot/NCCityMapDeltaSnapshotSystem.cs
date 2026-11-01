// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.IO;
using System.Text;
using System.Text.Json;
using Content.Server.Database;
using Content.Server.Maps;
using Content.Server._NC.Housing.Components;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.ZoneTemplates.Components;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared.GameTicking;
using Content.Shared.Mobs.Components;
using Content.Shared.Tag;
using Robust.Server.GameObjects;
using Robust.Shared.Asynchronous;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Persistence.RoundSnapshot;

/// <summary>
/// Replaces the non-wall roots of the mapper-authored city with the last committed round state.
/// Mapper walls remain untouched, so destroyed walls return from the baseline map every round.
/// </summary>
public sealed partial class NCCityMapDeltaSnapshotSystem : EntitySystem
{
    [Dependency] private Content.Server._NC.Medical.Cyberware.NCCyberwareSystem _cyberwareWorld = default!;
    private const string SectionType = "city-map-delta";
    private const string OwnerType = "world";
    private const string OwnerId = "city";
    private const int FormatVersion = 1;
    private const int RootsPerChunk = 128;
    private const int MaximumRoots = 32_768;
    private const int MaximumEntities = 131_072;
    private const int MaximumChunkBytes = 12 * 1024 * 1024;
    private static readonly ProtoId<TagPrototype> WallTag = "Wall";

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IGameMapManager _gameMaps = default!;
    [Dependency] private ITaskManager _tasks = default!;
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private NCRoundRestoreContextSystem _restore = default!;

    private readonly List<EntityUid> _roots = [];
    private bool _serializing;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCRoundSnapshotCaptureEvent>(OnRoundSnapshotCapture);
        _loader.OnIsSerializable += OnIsSerializable;
    }

    public override void Shutdown()
    {
        _loader.OnIsSerializable -= OnIsSerializable;
        base.Shutdown();
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        // Round start is already a loading boundary. Blocking here prevents players or simulations from observing
        // the clean baseline between deletion of its dynamic roots and application of the committed delta.
        NCRoundSnapshotManifestData? manifest;
        IReadOnlyList<NCRoundSnapshotSectionData> sections;
        try
        {
            manifest = _restore.Manifest;
            if (manifest == null) return;
            var sectionsTask = _database.GetNCRoundSnapshotSectionsAsync(manifest.SnapshotId, SectionType, OwnerType, OwnerId);
            _tasks.BlockWaitOnTask(sectionsTask);
            sections = sectionsTask.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            // Keep the mapper baseline intact when persistence is unavailable at the load boundary.
            Log.Error($"Could not read the city map delta; the mapper baseline will be used: {exception}");
            return;
        }

        if (manifest == null || sections.Count == 0)
            return;

        var selectedMap = _gameMaps.GetSelectedMap()?.ID ?? string.Empty;
        if (!string.Equals(manifest.MapId, selectedMap, StringComparison.Ordinal))
        {
            Log.Warning($"Skipped city delta for map '{manifest.MapId}' while '{selectedMap}' is loaded.");
            return;
        }

        if (!TryReadSections(sections, out var entries, out var error) || !TryApply(entries, out error))
            Log.Error($"City map delta {manifest.SnapshotId} was not applied: {error}");
    }

    private void OnRoundSnapshotCapture(NCRoundSnapshotCaptureEvent args)
    {
        if (!TryCapture(out var entries, out var entityCount, out var error))
        {
            args.Fail($"City map delta capture failed: {error}");
            return;
        }

        var chunkIndex = 0;
        foreach (var chunk in entries.Chunk(RootsPerChunk))
        {
            var document = new CityMapDeltaDocument(FormatVersion, chunk.ToList());
            var payload = JsonSerializer.Serialize(document);
            if (Encoding.UTF8.GetByteCount(payload) > MaximumChunkBytes)
            {
                args.Fail($"City map delta chunk {chunkIndex} exceeds {MaximumChunkBytes} bytes.");
                return;
            }

            args.TryAddSection(SectionType, OwnerType, OwnerId, FormatVersion,
                chunk.Sum(value => value.EntityCount), payload, chunkIndex++);
        }

        // Preserve an explicit empty state so a round that removed every mutable root clears the next baseline.
        if (entries.Count == 0)
        {
            args.TryAddSection(SectionType, OwnerType, OwnerId, FormatVersion, entityCount,
                JsonSerializer.Serialize(new CityMapDeltaDocument(FormatVersion, [])));
        }
    }

    private bool TryCapture(out List<CityMapRootEntry> entries, out int entityCount, out string error)
    {
        entries = [];
        entityCount = 0;
        error = string.Empty;
        CollectEligibleRoots(_roots);
        if (_roots.Count > MaximumRoots)
        {
            error = $"root limit {MaximumRoots} was exceeded";
            return false;
        }

        _serializing = true;
        try
        {
            foreach (var root in _roots)
            {
                if (!_coordinates.TryGetCoordinates(root, out var coordinates))
                    continue;
                var treeCount = CountSerializableTree(root);
                if (treeCount > MaximumEntities - entityCount)
                {
                    error = $"entity limit {MaximumEntities} was exceeded";
                    return false;
                }
                entityCount += treeCount;
                if (entityCount > MaximumEntities)
                {
                    error = $"entity limit {MaximumEntities} was exceeded";
                    return false;
                }

                using var writer = new StringWriter();
                var options = SerializationOptions.Default with
                {
                    MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
                    ErrorOnOrphan = false,
                    LogAutoInclude = null,
                };
                if (!_loader.TrySaveGeneric([root], writer, out _, options))
                {
                    error = $"root {ToPrettyString(root)} could not be serialized";
                    return false;
                }

                entries.Add(new CityMapRootEntry(
                    coordinates.NetworkId.Value,
                    coordinates.Z,
                    coordinates.Position.X,
                    coordinates.Position.Y,
                    (float) _transform.GetWorldRotation(root).Theta,
                    treeCount,
                    writer.ToString()));
            }
        }
        finally
        {
            _serializing = false;
        }
        return true;
    }

    private bool TryApply(IReadOnlyList<CityMapRootEntry> entries, out string error)
    {
        error = string.Empty;
        var loaded = new List<(EntityUid Root, CityMapRootEntry Entry, MapCoordinates Coordinates)>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.NetworkId == Guid.Empty || entry.EntityCount <= 0 ||
                !float.IsFinite(entry.X) || !float.IsFinite(entry.Y) || !float.IsFinite(entry.Rotation) ||
                string.IsNullOrWhiteSpace(entry.EntityYaml))
            {
                DeleteLoaded(loaded.Select(value => value.Root));
                error = "a root record is invalid";
                return false;
            }

            using var reader = new StringReader(entry.EntityYaml);
            if (!_loader.TryLoadGeneric(reader, "city map delta", out var result, MapLoadOptions.Default) ||
                result.Maps.Count != 0 || result.Grids.Count != 0 || result.Orphans.Count != 1 ||
                result.RootNodes.Count != 1 || !_cyberwareWorld.CanUseRestoredWorldTree(result.Orphans.Single()))
            {
                DeleteLoaded(result?.RootNodes ?? []);
                DeleteLoaded(loaded.Select(value => value.Root));
                error = "a root entity graph is invalid";
                return false;
            }

            var stable = new NCMapCoordinates(new NCZNetworkId(entry.NetworkId), new(entry.X, entry.Y), entry.Z);
            if (!_coordinates.TryResolve(stable, out var mapCoordinates))
            {
                DeleteLoaded(result.RootNodes);
                DeleteLoaded(loaded.Select(value => value.Root));
                error = $"coordinate {entry.NetworkId}/{entry.Z}/{entry.X},{entry.Y} cannot be resolved";
                return false;
            }
            loaded.Add((result.Orphans.Single(), entry, mapCoordinates));
        }

        CollectEligibleRoots(_roots);
        foreach (var root in _roots)
        {
            if (Exists(root))
                Del(root);
        }

        foreach (var (root, entry, mapCoordinates) in loaded)
        {
            _transform.SetMapCoordinates(root, mapCoordinates);
            _transform.SetWorldRotation(root, new Angle(entry.Rotation));
        }
        return true;
    }

    private void CollectEligibleRoots(List<EntityUid> roots)
    {
        roots.Clear();
        var query = EntityQueryEnumerator<TransformComponent>();
        while (query.MoveNext(out var uid, out var transform))
        {
            if (TerminatingOrDeleted(uid) || IsOwnedElsewhere(uid) || IsWall(uid) ||
                HasComp<MapComponent>(uid) || HasComp<MapGridComponent>(uid) ||
                !IsMapOrGrid(transform.ParentUid) || !_coordinates.TryGetCoordinates(uid, out _))
            {
                continue;
            }
            roots.Add(uid);
        }
    }

    private bool IsMapOrGrid(EntityUid uid) => HasComp<MapComponent>(uid) || HasComp<MapGridComponent>(uid);

    private bool IsOwnedElsewhere(EntityUid uid) =>
        HasComp<ActorComponent>(uid) ||
        HasComp<NCCityActorBindingComponent>(uid) ||
        HasComp<NCZoneDeploymentComponent>(uid) ||
        HasComp<NCPropertyResidentItemComponent>(uid) ||
        HasComp<MobStateComponent>(uid);

    private bool IsWall(EntityUid uid) => _tags.HasTag(uid, WallTag);

    private int CountSerializableTree(EntityUid root)
    {
        var count = 0;
        var pending = new Stack<EntityUid>();
        pending.Push(root);
        while (pending.TryPop(out var uid))
        {
            if (IsOwnedElsewhere(uid) || IsWall(uid))
                continue;
            count++;
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                pending.Push(child);
        }
        return count;
    }

    private void OnIsSerializable(Entity<MetaDataComponent> entity, ref bool serializable)
    {
        if (_serializing && serializable && (IsOwnedElsewhere(entity.Owner) || IsWall(entity.Owner)))
            serializable = false;
    }

    private static bool TryReadSections(
        IReadOnlyList<NCRoundSnapshotSectionData> sections,
        out List<CityMapRootEntry> entries,
        out string error)
    {
        entries = [];
        error = string.Empty;
        var entityCount = 0;
        try
        {
            foreach (var section in sections.OrderBy(value => value.ChunkIndex))
            {
                if (section.FormatVersion != FormatVersion)
                {
                    error = $"unsupported format {section.FormatVersion}";
                    return false;
                }
                var document = JsonSerializer.Deserialize<CityMapDeltaDocument>(section.Payload);
                if (document is not { Version: FormatVersion, Roots: not null })
                {
                    error = $"chunk {section.ChunkIndex} is corrupt";
                    return false;
                }
                entries.AddRange(document.Roots);
                foreach (var root in document.Roots)
                {
                    if (root.EntityCount <= 0 || root.EntityCount > MaximumEntities - entityCount)
                    {
                        error = "document bounds were exceeded";
                        return false;
                    }
                    entityCount += root.EntityCount;
                }
                if (entries.Count > MaximumRoots)
                {
                    error = "document bounds were exceeded";
                    return false;
                }
            }
            return true;
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private void DeleteLoaded(IEnumerable<EntityUid> roots)
    {
        foreach (var root in roots)
        {
            if (Exists(root))
                Del(root);
        }
    }

    private sealed record CityMapDeltaDocument(int Version, List<CityMapRootEntry> Roots);
    private sealed record CityMapRootEntry(
        Guid NetworkId,
        int Z,
        float X,
        float Y,
        float Rotation,
        int EntityCount,
        string EntityYaml);
}
