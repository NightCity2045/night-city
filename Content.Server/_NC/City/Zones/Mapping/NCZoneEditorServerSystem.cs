// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Editor;
using Content.Shared._NC.City.Zones.Prototypes;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared.Administration;
using Content.Shared.Administration.Managers;
using Robust.Server.Player;
using Robust.Shared.ContentPack;
using Robust.Shared.Enums;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Utility;

namespace Content.Server._NC.City.Zones.Mapping;

/// <summary>
/// Owns isolated per-mapper drafts and validates every editing operation on the server.
/// Full geometry is sent only to administrators with Mapping permission.
/// </summary>
public sealed partial class NCZoneEditorServerSystem : EntitySystem
{
    public static readonly ResPath NetworkSaveFileName = new("_zones.yml");

    private const int MaximumHistory = 64;
    private const int MaximumNameLength = 128;
    private const int MaximumVertices = 4096;
    private const int MaximumTilePatch = 16_384;
    private const int MaximumVolumeDepthSpan = 256;
    private const int MaximumZones = 10_000;
    private const int MaximumGeometryPerZone = 256;

    [Dependency] private ISharedAdminManager _admins = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ISerializationManager _serialization = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;

    private readonly Dictionary<NetUserId, DraftState> _drafts = [];

    public override void Initialize()
    {
        base.Initialize();
        _players.PlayerStatusChanged += OnPlayerStatusChanged;

        SubscribeNetworkEvent<NCZoneEditorSnapshotRequest>(OnSnapshotRequest);
        SubscribeNetworkEvent<NCZoneEditorCreateZoneRequest>(OnCreateZone);
        SubscribeNetworkEvent<NCZoneEditorUpdateZoneRequest>(OnUpdateZone);
        SubscribeNetworkEvent<NCZoneEditorDeleteZoneRequest>(OnDeleteZone);
        SubscribeNetworkEvent<NCZoneEditorAddGeometryRequest>(OnAddGeometry);
        SubscribeNetworkEvent<NCZoneEditorDeleteGeometryRequest>(OnDeleteGeometry);
        SubscribeNetworkEvent<NCZoneEditorSetGeometryScopeRequest>(OnSetGeometryScope);
        SubscribeNetworkEvent<NCZoneEditorSetVertexRequest>(OnSetVertex);
        SubscribeNetworkEvent<NCZoneEditorInsertVertexRequest>(OnInsertVertex);
        SubscribeNetworkEvent<NCZoneEditorRemoveVertexRequest>(OnRemoveVertex);
        SubscribeNetworkEvent<NCZoneEditorTilePatchRequest>(OnTilePatch);
        SubscribeNetworkEvent<NCZoneEditorHistoryRequest>(OnHistory);
        SubscribeNetworkEvent<NCZoneEditorValidationRequest>(OnValidation);
        SubscribeNetworkEvent<NCZoneEditorExportRequest>(OnExport);
    }

    public override void Shutdown()
    {
        _players.PlayerStatusChanged -= OnPlayerStatusChanged;
        _drafts.Clear();
        base.Shutdown();
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus is SessionStatus.Disconnected or SessionStatus.Zombie)
            _drafts.Remove(args.Session.UserId);
    }

    private void OnSnapshotRequest(NCZoneEditorSnapshotRequest request, EntitySessionEventArgs args)
    {
        if (!CanMap(args))
        {
            SendResult(args, false, "Mapping permission is required for the zone editor.");
            return;
        }

        var zoneSet = ResolveZoneSet(request.ZoneSet, args.SenderSession.AttachedEntity);
        NCZoneEditorSnapshot initialSnapshot;
        if (zoneSet != null)
        {
            initialSnapshot = CreateSnapshot(zoneSet);
        }
        else if (request.ZoneSet != null)
        {
            SendResult(args, false, $"Zone set '{request.ZoneSet}' does not exist.");
            return;
        }
        else if (args.SenderSession.AttachedEntity is not { } attached ||
                 !_coordinates.TryGetCoordinates(attached, out var coordinates))
        {
            SendResult(args, false, "Stand on a configured Z-network before opening the zone editor.");
            return;
        }
        else
        {
            // Mapping must be able to start before a zone-set prototype exists.
            // The generated stable ID is exported with the draft and survives subsequent reloads.
            var generatedId = $"NCZones{coordinates.NetworkId.Value:N}";
            initialSnapshot = new NCZoneEditorSnapshot(
                generatedId,
                coordinates.NetworkId,
                []);
        }

        if (!request.DiscardDraft &&
            _drafts.TryGetValue(args.SenderSession.UserId, out var existing) &&
            existing.Snapshot.ZoneSet == initialSnapshot.ZoneSet)
        {
            SendSnapshot(args, existing);
            return;
        }

        var draft = new DraftState(initialSnapshot);
        _drafts[args.SenderSession.UserId] = draft;
        SendSnapshot(args, draft);
    }

    private void OnCreateZone(NCZoneEditorCreateZoneRequest request, EntitySessionEventArgs args)
    {
        string? error = null;
        if (!TryGetDraft(args, out var draft) ||
            !_prototypes.TryIndex<NCZoneKindPrototype>(request.Kind, out var kind) ||
            !TryNormalizeName(request.Name, out var name) ||
            !ValidateParent(draft.Snapshot, kind, request.Parent, default, out error))
        {
            SendResult(args, false, error ?? "The requested zone type or name is invalid.");
            return;
        }

        if (draft.Snapshot.Zones.Length >= MaximumZones)
        {
            SendResult(args, false, "The draft reached the 10,000 zone safety limit.");
            return;
        }

        SaveUndo(draft);
        var zoneId = new NCZoneId(Guid.NewGuid());
        var zone = new NCZoneEditorZone(
            zoneId,
            request.Parent,
            kind.ID,
            name,
            0,
            NCZoneActivityMode.Active,
            kind.EditorColor.RByte,
            kind.EditorColor.GByte,
            kind.EditorColor.BByte,
            []);

        draft.Snapshot = ReplaceZones(
            draft.Snapshot,
            [.. draft.Snapshot.Zones, zone]);
        Commit(args, draft, $"Created zone {name} ({zoneId}).");
    }

    private void OnUpdateZone(NCZoneEditorUpdateZoneRequest request, EntitySessionEventArgs args)
    {
        string? error = null;
        if (!TryGetDraft(args, out var draft) ||
            !TryFindZone(draft.Snapshot, request.ZoneId, out var index, out var zone) ||
            !_prototypes.TryIndex<NCZoneKindPrototype>(zone.Kind, out var kind) ||
            !TryNormalizeName(request.Name, out var name) ||
            !ValidateParent(draft.Snapshot, kind, request.Parent, zone.Id, out error))
        {
            SendResult(args, false, error ?? "The zone update is invalid.");
            return;
        }

        if (!kind.SupportsActivityMode && request.ActivityMode != NCZoneActivityMode.Active)
        {
            SendResult(args, false, $"{kind.ID} zones do not support activity modes.");
            return;
        }

        SaveUndo(draft);
        var zones = draft.Snapshot.Zones.ToArray();
        zones[index] = CloneZone(
            zone,
            name: name,
            parent: request.Parent,
            priority: request.Priority,
            activityMode: request.ActivityMode);
        draft.Snapshot = ReplaceZones(draft.Snapshot, zones);
        Commit(args, draft, $"Updated zone {name}.");
    }

    private void OnDeleteZone(NCZoneEditorDeleteZoneRequest request, EntitySessionEventArgs args)
    {
        if (!TryGetDraft(args, out var draft) ||
            !TryFindZone(draft.Snapshot, request.ZoneId, out _, out var zone))
        {
            SendResult(args, false, "Zone does not exist in the current draft.");
            return;
        }

        if (draft.Snapshot.Zones.Any(candidate => candidate.Parent == request.ZoneId))
        {
            SendResult(args, false, "Delete or reparent child zones first.");
            return;
        }

        SaveUndo(draft);
        draft.Snapshot = ReplaceZones(
            draft.Snapshot,
            draft.Snapshot.Zones.Where(candidate => candidate.Id != request.ZoneId).ToArray());
        Commit(args, draft, $"Deleted zone {zone.Name}.");
    }

    private void OnAddGeometry(NCZoneEditorAddGeometryRequest request, EntitySessionEventArgs args)
    {
        // Polygon and Volume share one 2D outline. The Z scope determines the serialized legacy kind.
        var storedKind = request.Kind is NCZoneGeometryKind.Polygon or NCZoneGeometryKind.Volume
            ? !request.Global && request.MinZ != request.MaxZ
                ? NCZoneGeometryKind.Volume
                : NCZoneGeometryKind.Polygon
            : request.Kind;
        // Polygon rings are stored without a repeated closing vertex. Normalize external/editor requests so
        // clicking the first point again cannot create a false zero-length edge during validation.
        var vertices = request.Vertices;
        if (storedKind is NCZoneGeometryKind.Polygon or NCZoneGeometryKind.Volume &&
            vertices.Length > 1 &&
            vertices[0] == vertices[^1])
        {
            vertices = vertices[..^1];
        }

        if (!TryGetDraft(args, out var draft) ||
            !TryFindZone(draft.Snapshot, request.ZoneId, out var zoneIndex, out var zone) ||
            !_prototypes.TryIndex<NCZoneKindPrototype>(zone.Kind, out var kind) ||
            !kind.AllowedGeometry.Contains(storedKind))
        {
            SendResult(args, false, "This geometry type is not allowed for the selected zone.");
            return;
        }

        if (vertices.Length > MaximumVertices ||
            request.Tiles.Length > MaximumTilePatch ||
            vertices.Any(vertex => !IsFinite(vertex)) ||
            zone.Geometry.Length >= MaximumGeometryPerZone)
        {
            SendResult(args, false, "The geometry exceeds editor safety limits.");
            return;
        }

        if (request.Kind is NCZoneGeometryKind.Polygon or NCZoneGeometryKind.Volume &&
            vertices.Length < 3)
        {
            SendResult(args, false, "A polygon requires at least three vertices.");
            return;
        }

        if (!request.Global &&
            storedKind == NCZoneGeometryKind.Volume &&
            (request.MinZ > request.MaxZ ||
             (long) request.MaxZ - request.MinZ > MaximumVolumeDepthSpan))
        {
            SendResult(args, false, "Volume Z range is inverted or exceeds 256 floors.");
            return;
        }

        if (request.Global && request.Kind == NCZoneGeometryKind.TileMask)
        {
            SendResult(args, false, "Tile-mask geometry must remain bound to explicit floors.");
            return;
        }

        if (!request.Global &&
            storedKind != NCZoneGeometryKind.Volume &&
            !FloorExists(draft.Snapshot, request.Z))
        {
            SendResult(args, false, "Geometry references a floor that is not loaded in this Z-network.");
            return;
        }

        SaveUndo(draft);
        var geometry = new NCZoneEditorGeometry(
            storedKind,
            request.Global,
            request.Z,
            request.MinZ,
            request.MaxZ,
            vertices.ToArray(),
            EncodeTiles(request.Tiles, request.Z));

        var zones = draft.Snapshot.Zones.ToArray();
        zones[zoneIndex] = CloneZone(zone, geometry: [.. zone.Geometry, geometry]);
        draft.Snapshot = ReplaceZones(draft.Snapshot, zones);
        Commit(args, draft, $"Added {storedKind} geometry.");
    }

    private void OnSetGeometryScope(
        NCZoneEditorSetGeometryScopeRequest request,
        EntitySessionEventArgs args)
    {
        if (!TryGetGeometry(args, request.ZoneId, request.GeometryIndex,
                out var draft, out var zoneIndex, out var zone, out var geometry) ||
            geometry.Kind == NCZoneGeometryKind.TileMask ||
            (!request.Global &&
             (request.MinZ > request.MaxZ ||
              (long) request.MaxZ - request.MinZ > MaximumVolumeDepthSpan)))
        {
            SendResult(args, false, "The requested geometry Z scope is invalid.");
            return;
        }

        var targetKind = request.Global || request.MinZ == request.MaxZ
            ? NCZoneGeometryKind.Polygon
            : NCZoneGeometryKind.Volume;

        if (!_prototypes.TryIndex<NCZoneKindPrototype>(zone.Kind, out var kind) ||
            !kind.AllowedGeometry.Contains(targetKind) ||
            (!request.Global &&
             targetKind != NCZoneGeometryKind.Volume &&
             !FloorExists(draft.Snapshot, request.MinZ)))
        {
            SendResult(args, false, "The selected zone cannot use this Z range.");
            return;
        }

        SaveUndo(draft);
        ReplaceGeometry(
            draft,
            zoneIndex,
            zone,
            request.GeometryIndex,
            CloneGeometry(
                geometry,
                kind: targetKind,
                global: request.Global,
                z: request.MinZ,
                minZ: request.MinZ,
                maxZ: request.MaxZ));
        Commit(
            args,
            draft,
            request.Global
                ? "Geometry now applies to all Z-levels."
                : $"Geometry now applies to Z={request.MinZ}..{request.MaxZ}.");
    }

    private void OnDeleteGeometry(NCZoneEditorDeleteGeometryRequest request, EntitySessionEventArgs args)
    {
        if (!TryGetGeometry(args, request.ZoneId, request.GeometryIndex,
                out var draft, out var zoneIndex, out var zone, out _))
        {
            return;
        }

        SaveUndo(draft);
        var geometry = zone.Geometry.Where((_, index) => index != request.GeometryIndex).ToArray();
        var zones = draft.Snapshot.Zones.ToArray();
        zones[zoneIndex] = CloneZone(zone, geometry: geometry);
        draft.Snapshot = ReplaceZones(draft.Snapshot, zones);
        Commit(args, draft, "Deleted geometry.");
    }

    private void OnSetVertex(NCZoneEditorSetVertexRequest request, EntitySessionEventArgs args)
    {
        if (!TryGetGeometry(args, request.ZoneId, request.GeometryIndex,
                out var draft, out var zoneIndex, out var zone, out var geometry) ||
            geometry.Kind is not (NCZoneGeometryKind.Polygon or NCZoneGeometryKind.Volume) ||
            request.VertexIndex < 0 ||
            request.VertexIndex >= geometry.Vertices.Length ||
            !IsFinite(request.Position))
        {
            SendResult(args, false, "The selected polygon vertex is invalid.");
            return;
        }

        SaveUndo(draft);
        var vertices = geometry.Vertices.ToArray();
        vertices[request.VertexIndex] = request.Position;
        ReplaceGeometry(draft, zoneIndex, zone, request.GeometryIndex, CloneGeometry(geometry, vertices: vertices));
        Commit(args, draft, $"Moved vertex {request.VertexIndex + 1}.");
    }

    private void OnRemoveVertex(NCZoneEditorRemoveVertexRequest request, EntitySessionEventArgs args)
    {
        if (!TryGetGeometry(args, request.ZoneId, request.GeometryIndex,
                out var draft, out var zoneIndex, out var zone, out var geometry) ||
            geometry.Kind is not (NCZoneGeometryKind.Polygon or NCZoneGeometryKind.Volume) ||
            request.VertexIndex < 0 ||
            request.VertexIndex >= geometry.Vertices.Length ||
            geometry.Vertices.Length <= 3)
        {
            SendResult(args, false, "A polygon must retain at least three vertices.");
            return;
        }

        SaveUndo(draft);
        var vertices = geometry.Vertices
            .Where((_, index) => index != request.VertexIndex)
            .ToArray();
        ReplaceGeometry(draft, zoneIndex, zone, request.GeometryIndex, CloneGeometry(geometry, vertices: vertices));
        Commit(args, draft, $"Removed vertex {request.VertexIndex + 1}.");
    }

    private void OnInsertVertex(NCZoneEditorInsertVertexRequest request, EntitySessionEventArgs args)
    {
        if (!TryGetGeometry(args, request.ZoneId, request.GeometryIndex,
                out var draft, out var zoneIndex, out var zone, out var geometry) ||
            geometry.Kind is not (NCZoneGeometryKind.Polygon or NCZoneGeometryKind.Volume) ||
            request.AfterVertexIndex < 0 ||
            request.AfterVertexIndex >= geometry.Vertices.Length ||
            geometry.Vertices.Length >= MaximumVertices ||
            !IsFinite(request.Position))
        {
            SendResult(args, false, "The new polygon vertex is invalid.");
            return;
        }

        SaveUndo(draft);
        var vertices = geometry.Vertices.ToList();
        vertices.Insert(request.AfterVertexIndex + 1, request.Position);
        ReplaceGeometry(
            draft,
            zoneIndex,
            zone,
            request.GeometryIndex,
            CloneGeometry(geometry, vertices: vertices.ToArray()));
        Commit(args, draft, $"Inserted vertex after {request.AfterVertexIndex + 1}.");
    }

    private void OnTilePatch(NCZoneEditorTilePatchRequest request, EntitySessionEventArgs args)
    {
        if (request.Tiles.Length is 0 or > MaximumTilePatch ||
            !TryGetGeometry(args, request.ZoneId, request.GeometryIndex,
                out var draft, out var zoneIndex, out var zone, out var geometry) ||
            geometry.Kind != NCZoneGeometryKind.TileMask)
        {
            SendResult(args, false, "The tile-mask patch is invalid or too large.");
            return;
        }

        if (!_coordinates.TryGetMap(draft.Snapshot.NetworkId, request.Z, out _))
        {
            SendResult(args, false, $"Floor Z={request.Z} is not loaded in this Z-network.");
            return;
        }

        SaveUndo(draft);
        var tiles = DecodeTiles(geometry.Chunks);
        foreach (var tile in request.Tiles)
        {
            var entry = new TileAtDepth(request.Z, tile);
            if (request.Add)
                tiles.Add(entry);
            else
                tiles.Remove(entry);
        }

        var chunks = EncodeTiles(
            tiles.Where(tile => tile.Z == request.Z).Select(tile => tile.Position),
            request.Z);
        var otherChunks = geometry.Chunks.Where(chunk => chunk.Z != request.Z);
        ReplaceGeometry(
            draft,
            zoneIndex,
            zone,
            request.GeometryIndex,
            CloneGeometry(geometry, chunks: [.. otherChunks, .. chunks]));
        Commit(args, draft, request.Add ? "Painted tile mask." : "Erased tile mask.");
    }

    private void OnHistory(NCZoneEditorHistoryRequest request, EntitySessionEventArgs args)
    {
        if (!TryGetDraft(args, out var draft))
            return;

        var source = request.Redo ? draft.Redo : draft.Undo;
        var destination = request.Redo ? draft.Undo : draft.Redo;
        if (!source.TryPop(out var snapshot))
        {
            SendResult(args, false, request.Redo ? "Nothing to redo." : "Nothing to undo.");
            return;
        }

        destination.Push(CloneSnapshot(draft.Snapshot));
        draft.Snapshot = CloneSnapshot(snapshot, draft.Snapshot.Revision + 1, true);
        SendSnapshot(args, draft);
        SendResult(args, true, request.Redo ? "Redone." : "Undone.");
    }

    private void OnValidation(NCZoneEditorValidationRequest request, EntitySessionEventArgs args)
    {
        if (!TryGetDraft(args, out var draft))
            return;

        RaiseNetworkEvent(
            new NCZoneEditorValidationResult(ValidateDraft(draft.Snapshot)),
            args.SenderSession);
    }

    private void OnExport(NCZoneEditorExportRequest request, EntitySessionEventArgs args)
    {
        if (!TryGetDraft(args, out var draft))
            return;

        var errors = ValidateDraft(draft.Snapshot);
        RaiseNetworkEvent(new NCZoneEditorValidationResult(errors), args.SenderSession);
        if (errors.Length != 0)
        {
            SendResult(args, false, $"Fix {errors.Length} validation error(s) before exporting.");
            return;
        }

        if (!TryExportDraft(args.SenderSession.UserId, request.FileName, out var path))
        {
            SendResult(args, false, "The export file name is invalid.");
            return;
        }

        draft.Snapshot = CloneSnapshot(draft.Snapshot, draft.Snapshot.Revision + 1, false);
        SendSnapshot(args, draft);
        SendResult(args, true, $"Exported zone YAML to user data: {path}");
    }

    public bool TryExportDraft(NetUserId userId, string fileName, out ResPath path)
    {
        path = default;
        if (!_drafts.TryGetValue(userId, out var draft) ||
            ValidateDraft(draft.Snapshot).Length != 0 ||
            string.IsNullOrWhiteSpace(fileName) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileName.Contains('/') ||
            fileName.Contains('\\'))
        {
            return false;
        }

        if (!fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            fileName += ".yml";

        path = new ResPath($"/ZoneExports/{fileName}");
        _resources.UserData.CreateDir(path.Directory);
        using var writer = _resources.UserData.OpenWriteText(path);
        WriteSnapshot(writer, draft.Snapshot);
        return true;
    }

    /// <summary>
    /// Checks whether the invoking mapper has a valid draft for a Z-network, falling back to its loaded prototype.
    /// This preflight is used before any map file is overwritten by znetwork-save.
    /// </summary>
    public bool TryValidateNetworkSave(
        NetUserId? userId,
        NCZNetworkId networkId,
        out bool hasZones,
        out string? error)
    {
        hasZones = false;
        error = null;

        if (!TryResolveNetworkSnapshot(userId, networkId, out var snapshot))
            return true;

        hasZones = true;
        var errors = ValidateDraft(snapshot);
        if (errors.Length == 0)
            return true;

        error = $"Zone draft contains {errors.Length} validation error(s); maps were not saved.";
        return false;
    }

    /// <summary>
    /// Writes the validated zone set belonging to a persistent Z-network beside its map files.
    /// </summary>
    public bool TrySaveNetworkZones(
        NetUserId? userId,
        NCZNetworkId networkId,
        ResPath path,
        out string? error)
    {
        error = null;
        if (!TryResolveNetworkSnapshot(userId, networkId, out var snapshot))
        {
            error = "No zone draft or loaded zone set belongs to this Z-network.";
            return false;
        }

        var errors = ValidateDraft(snapshot);
        if (errors.Length != 0)
        {
            error = $"Zone draft contains {errors.Length} validation error(s).";
            return false;
        }

        try
        {
            _resources.UserData.CreateDir(path.Directory);
            using var writer = _resources.UserData.OpenWriteText(path);
            WriteSnapshot(writer, snapshot);
            return true;
        }
        catch (Exception exception)
        {
            error = $"Unable to write zone set {path}: {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// Validates and publishes a zone-set sidecar loaded from a Z-network save. Prototype reload then rebuilds
    /// NCZoneSystem's spatial index through its normal PrototypesReloaded event.
    /// </summary>
    public bool TryLoadNetworkZones(ResPath path, NCZNetworkId expectedNetworkId, out string? error)
    {
        error = null;
        if (!TryReadZoneSnapshot(path, out var snapshot, out error))
            return false;

        if (snapshot.NetworkId != expectedNetworkId)
        {
            error = $"Zone set {path} belongs to NCZNetworkId {snapshot.NetworkId}, expected {expectedNetworkId}.";
            return false;
        }

        var validationErrors = ValidateDraft(snapshot);
        if (validationErrors.Length != 0)
        {
            error = $"Zone set {path} contains {validationErrors.Length} validation error(s).";
            return false;
        }

        foreach (var other in _prototypes.EnumeratePrototypes<NCZoneSetPrototype>())
        {
            if (other.ID != snapshot.ZoneSet && other.NetworkId == snapshot.NetworkId)
            {
                error = $"NCZNetworkId {snapshot.NetworkId} is already owned by zone set {other.ID}.";
                return false;
            }

            if (other.ID == snapshot.ZoneSet && other.NetworkId != snapshot.NetworkId)
            {
                error = $"Zone-set ID {snapshot.ZoneSet} is already assigned to another Z-network.";
                return false;
            }

            if (other.ID == snapshot.ZoneSet)
                continue;

            var foreignIds = other.Zones.Select(zone => zone.Id).ToHashSet();
            if (snapshot.Zones.Any(zone => foreignIds.Contains(zone.Id)))
            {
                error = $"Zone set {path} reuses an NCZoneId owned by {other.ID}.";
                return false;
            }
        }

        try
        {
            var modified = new Dictionary<Type, HashSet<string>>();
            using var reader = _resources.UserData.OpenText(path);
            _prototypes.LoadFromStream(reader, overwrite: true, modified);
            _prototypes.ReloadPrototypes(modified);
        }
        catch (Exception exception)
        {
            error = $"Unable to load zone set {path}: {exception.Message}";
            return false;
        }

        // Any in-memory draft for this network predates the loaded sidecar and must not hide it in the editor.
        foreach (var user in _drafts
                     .Where(pair => pair.Value.Snapshot.NetworkId == expectedNetworkId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _drafts.Remove(user);
        }

        return true;
    }

    private bool TryResolveNetworkSnapshot(
        NetUserId? userId,
        NCZNetworkId networkId,
        out NCZoneEditorSnapshot snapshot)
    {
        if (userId is { } mapper &&
            _drafts.TryGetValue(mapper, out var draft) &&
            draft.Snapshot.NetworkId == networkId)
        {
            snapshot = draft.Snapshot;
            return true;
        }

        var zoneSet = _prototypes
            .EnumeratePrototypes<NCZoneSetPrototype>()
            .FirstOrDefault(candidate => candidate.NetworkId == networkId);
        if (zoneSet != null)
        {
            snapshot = CreateSnapshot(zoneSet);
            return true;
        }

        snapshot = default!;
        return false;
    }

    private bool TryReadZoneSnapshot(
        ResPath path,
        out NCZoneEditorSnapshot snapshot,
        out string? error)
    {
        snapshot = default!;
        error = null;

        if (!_resources.UserData.Exists(path))
        {
            error = $"Zone set {path} does not exist.";
            return false;
        }

        try
        {
            using var reader = _resources.UserData.OpenText(path);
            var documents = DataNodeParser.ParseYamlStream(reader).ToArray();
            if (documents.Length != 1 ||
                documents[0].Root is not SequenceDataNode { Sequence.Count: 1 } sequence ||
                sequence.Sequence[0] is not MappingDataNode mapping ||
                !mapping.TryGetValue("type", out var typeNode) ||
                typeNode is not ValueDataNode { Value: "ncZoneSet" })
            {
                error = $"Zone sidecar {path} must contain exactly one ncZoneSet prototype.";
                return false;
            }

            var data = mapping.Copy();
            data.Remove("type");
            var zoneSet = _serialization.Read<NCZoneSetPrototype>(data, notNullableOverride: true);
            snapshot = CreateSnapshot(zoneSet);
            return true;
        }
        catch (Exception exception)
        {
            error = $"Unable to parse zone set {path}: {exception.Message}";
            return false;
        }
    }

    private bool CanMap(EntitySessionEventArgs args)
    {
        return _admins.HasAdminFlag(args.SenderSession, AdminFlags.Mapping);
    }

    private bool TryGetDraft(EntitySessionEventArgs args, out DraftState draft)
    {
        draft = default!;
        if (!CanMap(args))
        {
            SendResult(args, false, "Mapping permission is required for the zone editor.");
            return false;
        }

        if (_drafts.TryGetValue(args.SenderSession.UserId, out var found))
        {
            draft = found;
            return true;
        }

        SendResult(args, false, "Open a zone set before editing.");
        return false;
    }

    private bool TryGetGeometry(
        EntitySessionEventArgs args,
        NCZoneId zoneId,
        int geometryIndex,
        out DraftState draft,
        out int zoneIndex,
        out NCZoneEditorZone zone,
        out NCZoneEditorGeometry geometry)
    {
        zoneIndex = -1;
        zone = default!;
        geometry = default!;
        if (!TryGetDraft(args, out draft) ||
            !TryFindZone(draft.Snapshot, zoneId, out zoneIndex, out zone) ||
            geometryIndex < 0 ||
            geometryIndex >= zone.Geometry.Length)
        {
            SendResult(args, false, "Geometry does not exist in the current draft.");
            return false;
        }

        geometry = zone.Geometry[geometryIndex];
        return true;
    }

    private NCZoneSetPrototype? ResolveZoneSet(string? requested, EntityUid? attached)
    {
        if (requested != null)
            return _prototypes.TryIndex<NCZoneSetPrototype>(requested, out var explicitSet)
                ? explicitSet
                : null;

        if (attached is not { } entity ||
            !_coordinates.TryGetCoordinates(entity, out var coordinates))
        {
            return null;
        }

        return _prototypes
            .EnumeratePrototypes<NCZoneSetPrototype>()
            .FirstOrDefault(candidate => candidate.NetworkId == coordinates.NetworkId);
    }

    private bool ValidateParent(
        NCZoneEditorSnapshot snapshot,
        NCZoneKindPrototype kind,
        NCZoneId parentId,
        NCZoneId editedZone,
        out string? error)
    {
        error = null;
        if (!parentId.IsValid)
        {
            if (!kind.RequiresParent)
                return true;

            error = $"{kind.ID} requires a parent zone.";
            return false;
        }

        if (!TryFindZone(snapshot, parentId, out _, out var parent))
        {
            error = "Parent zone does not exist.";
            return false;
        }

        if (!kind.AllowedParents.Contains(parent.Kind))
        {
            error = $"{kind.ID} cannot be a child of {parent.Kind}.";
            return false;
        }

        var visited = new HashSet<NCZoneId> { editedZone };
        var current = parent;
        while (current.Id.IsValid)
        {
            if (!visited.Add(current.Id))
            {
                error = "The parent selection would create a hierarchy cycle.";
                return false;
            }

            if (!current.Parent.IsValid ||
                !TryFindZone(snapshot, current.Parent, out _, out current))
            {
                break;
            }
        }

        return true;
    }

    private bool FloorExists(NCZoneEditorSnapshot snapshot, int z)
    {
        return _coordinates.TryGetMap(snapshot.NetworkId, z, out _);
    }

    private NCZoneEditorValidationError[] ValidateDraft(NCZoneEditorSnapshot snapshot)
    {
        var errors = new List<NCZoneEditorValidationError>();
        var ids = new HashSet<NCZoneId>();

        foreach (var zone in snapshot.Zones)
        {
            if (!zone.Id.IsValid || !ids.Add(zone.Id))
                errors.Add(new NCZoneEditorValidationError(zone.Id, -1, "Zone ID is empty or duplicated."));

            if (!_prototypes.TryIndex<NCZoneKindPrototype>(zone.Kind, out var kind))
            {
                errors.Add(new NCZoneEditorValidationError(zone.Id, -1, $"Unknown zone kind {zone.Kind}."));
                continue;
            }

            if (!ValidateParent(snapshot, kind, zone.Parent, zone.Id, out var parentError))
                errors.Add(new NCZoneEditorValidationError(zone.Id, -1, parentError!));

            if (zone.Geometry.Length == 0)
                errors.Add(new NCZoneEditorValidationError(zone.Id, -1, "Zone has no geometry."));

            for (var index = 0; index < zone.Geometry.Length; index++)
            {
                var geometry = zone.Geometry[index];
                if (!kind.AllowedGeometry.Contains(geometry.Kind))
                {
                    errors.Add(new NCZoneEditorValidationError(
                        zone.Id,
                        index,
                        $"{zone.Kind} does not allow {geometry.Kind}."));
                    continue;
                }

                ValidateGeometry(zone.Id, index, geometry, errors);
            }
        }

        return errors.ToArray();
    }

    private static void ValidateGeometry(
        NCZoneId zoneId,
        int index,
        NCZoneEditorGeometry geometry,
        List<NCZoneEditorValidationError> errors)
    {
        if (geometry.Kind is NCZoneGeometryKind.Polygon or NCZoneGeometryKind.Volume)
        {
            if (geometry.Vertices.Length < 3)
            {
                errors.Add(new NCZoneEditorValidationError(zoneId, index, "Polygon requires at least three vertices."));
                return;
            }

            if (geometry.Vertices.Any(vertex => !IsFinite(vertex)))
                errors.Add(new NCZoneEditorValidationError(zoneId, index, "Polygon contains a non-finite vertex."));

            if (HasZeroLengthEdge(geometry.Vertices))
                errors.Add(new NCZoneEditorValidationError(zoneId, index, "Polygon contains a repeated consecutive vertex."));
            else if (HasSelfIntersection(geometry.Vertices))
                errors.Add(new NCZoneEditorValidationError(zoneId, index, "Polygon intersects itself."));

            var twiceArea = 0f;
            for (var i = 0; i < geometry.Vertices.Length; i++)
            {
                var next = geometry.Vertices[(i + 1) % geometry.Vertices.Length];
                twiceArea += geometry.Vertices[i].X * next.Y - next.X * geometry.Vertices[i].Y;
            }

            if (MathF.Abs(twiceArea) < 0.0001f)
                errors.Add(new NCZoneEditorValidationError(zoneId, index, "Polygon has zero area."));
        }

        if (!geometry.Global &&
            geometry.Kind == NCZoneGeometryKind.Volume &&
            geometry.MinZ > geometry.MaxZ)
            errors.Add(new NCZoneEditorValidationError(zoneId, index, "Volume minimum Z exceeds maximum Z."));

        if (!geometry.Global &&
            geometry.Kind == NCZoneGeometryKind.Volume &&
            (long) geometry.MaxZ - geometry.MinZ > MaximumVolumeDepthSpan)
        {
            errors.Add(new NCZoneEditorValidationError(zoneId, index, "Volume spans more than 256 Z-levels."));
        }

        if (geometry.Kind == NCZoneGeometryKind.TileMask &&
            geometry.Global)
        {
            errors.Add(new NCZoneEditorValidationError(zoneId, index, "Tile-mask cannot be global."));
        }

        if (geometry.Kind == NCZoneGeometryKind.TileMask &&
            !geometry.Chunks.Any(chunk => chunk.Rows.Any(row => row != 0)))
        {
            errors.Add(new NCZoneEditorValidationError(zoneId, index, "Tile-mask is empty."));
        }
    }

    private static bool HasSelfIntersection(IReadOnlyList<Vector2> vertices)
    {
        for (var i = 0; i < vertices.Count; i++)
        {
            var nextI = (i + 1) % vertices.Count;
            for (var j = i + 1; j < vertices.Count; j++)
            {
                var nextJ = (j + 1) % vertices.Count;
                if (i == j || nextI == j || nextJ == i)
                    continue;

                if (SegmentsIntersect(vertices[i], vertices[nextI], vertices[j], vertices[nextJ]))
                    return true;
            }
        }

        return false;
    }

    private static bool HasZeroLengthEdge(IReadOnlyList<Vector2> vertices)
    {
        for (var i = 0; i < vertices.Count; i++)
        {
            if (vertices[i] == vertices[(i + 1) % vertices.Count])
                return true;
        }

        return false;
    }

    private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        static float Cross(Vector2 first, Vector2 second, Vector2 third)
        {
            return (second.X - first.X) * (third.Y - first.Y) -
                   (second.Y - first.Y) * (third.X - first.X);
        }

        var abC = Cross(a, b, c);
        var abD = Cross(a, b, d);
        var cdA = Cross(c, d, a);
        var cdB = Cross(c, d, b);
        return abC * abD < 0f && cdA * cdB < 0f;
    }

    private void Commit(EntitySessionEventArgs args, DraftState draft, string message)
    {
        var previous = draft.Undo.Count != 0
            ? draft.Undo.Peek()
            : draft.Snapshot;
        draft.Snapshot = CloneSnapshot(draft.Snapshot, draft.Snapshot.Revision + 1, true);
        SendPatch(args, previous, draft.Snapshot);
        SendResult(args, true, message);
    }

    private static void SaveUndo(DraftState draft)
    {
        draft.Undo.Push(CloneSnapshot(draft.Snapshot));
        while (draft.Undo.Count > MaximumHistory)
            RemoveOldest(draft.Undo);
        draft.Redo.Clear();
    }

    private static void RemoveOldest(Stack<NCZoneEditorSnapshot> stack)
    {
        var values = stack.ToArray();
        stack.Clear();
        for (var index = values.Length - 2; index >= 0; index--)
            stack.Push(values[index]);
    }

    private void SendSnapshot(EntitySessionEventArgs args, DraftState draft)
    {
        RaiseNetworkEvent(CloneSnapshot(draft.Snapshot), args.SenderSession);
    }

    private void SendPatch(
        EntitySessionEventArgs args,
        NCZoneEditorSnapshot previous,
        NCZoneEditorSnapshot current)
    {
        var previousById = previous.Zones.ToDictionary(zone => zone.Id);
        var currentIds = current.Zones.Select(zone => zone.Id).ToHashSet();
        var upserted = current.Zones
            .Where(zone =>
                !previousById.TryGetValue(zone.Id, out var old) ||
                !ReferenceEquals(old, zone))
            .ToArray();
        var removed = previous.Zones
            .Where(zone => !currentIds.Contains(zone.Id))
            .Select(zone => zone.Id)
            .ToArray();

        RaiseNetworkEvent(
            new NCZoneEditorPatch(
                previous.Revision,
                current.Revision,
                current.Dirty,
                upserted,
                removed),
            args.SenderSession);
    }

    private void SendResult(EntitySessionEventArgs args, bool success, string message)
    {
        RaiseNetworkEvent(new NCZoneEditorOperationResult(success, message), args.SenderSession);
    }

    private static bool TryFindZone(
        NCZoneEditorSnapshot snapshot,
        NCZoneId id,
        out int index,
        out NCZoneEditorZone zone)
    {
        for (index = 0; index < snapshot.Zones.Length; index++)
        {
            if (snapshot.Zones[index].Id != id)
                continue;

            zone = snapshot.Zones[index];
            return true;
        }

        index = -1;
        zone = default!;
        return false;
    }

    private static bool TryNormalizeName(string source, out string name)
    {
        name = source.Trim();
        return name.Length is > 0 and <= MaximumNameLength;
    }

    private static bool IsFinite(Vector2 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y);
    }

    private static void ReplaceGeometry(
        DraftState draft,
        int zoneIndex,
        NCZoneEditorZone zone,
        int geometryIndex,
        NCZoneEditorGeometry replacement)
    {
        var geometry = zone.Geometry.ToArray();
        geometry[geometryIndex] = replacement;
        var zones = draft.Snapshot.Zones.ToArray();
        zones[zoneIndex] = CloneZone(zone, geometry: geometry);
        draft.Snapshot = ReplaceZones(draft.Snapshot, zones);
    }

    private static NCZoneEditorSnapshot ReplaceZones(
        NCZoneEditorSnapshot snapshot,
        NCZoneEditorZone[] zones)
    {
        return new NCZoneEditorSnapshot(
            snapshot.ZoneSet,
            snapshot.NetworkId,
            zones,
            snapshot.Revision,
            snapshot.Dirty);
    }

    private static NCZoneEditorSnapshot CloneSnapshot(
        NCZoneEditorSnapshot source,
        int? revision = null,
        bool? dirty = null)
    {
        // Editor DTOs are immutable after construction. Keep structural sharing between undo
        // revisions so a single vertex edit does not duplicate the entire city in memory.
        return new NCZoneEditorSnapshot(
            source.ZoneSet,
            source.NetworkId,
            source.Zones.ToArray(),
            revision ?? source.Revision,
            dirty ?? source.Dirty);
    }

    private static NCZoneEditorZone CloneZone(
        NCZoneEditorZone source,
        string? name = null,
        NCZoneId? parent = null,
        int? priority = null,
        NCZoneActivityMode? activityMode = null,
        NCZoneEditorGeometry[]? geometry = null)
    {
        return new NCZoneEditorZone(
            source.Id,
            parent ?? source.Parent,
            source.Kind,
            name ?? source.Name,
            priority ?? source.Priority,
            activityMode ?? source.ActivityMode,
            source.ColorR,
            source.ColorG,
            source.ColorB,
            geometry ?? source.Geometry);
    }

    private static NCZoneEditorGeometry CloneGeometry(
        NCZoneEditorGeometry source,
        NCZoneGeometryKind? kind = null,
        bool? global = null,
        int? z = null,
        int? minZ = null,
        int? maxZ = null,
        Vector2[]? vertices = null,
        NCZoneEditorTileChunk[]? chunks = null)
    {
        return new NCZoneEditorGeometry(
            kind ?? source.Kind,
            global ?? source.Global,
            z ?? source.Z,
            minZ ?? source.MinZ,
            maxZ ?? source.MaxZ,
            vertices ?? source.Vertices,
            chunks ?? source.Chunks);
    }

    private static NCZoneEditorTileChunk[] EncodeTiles(IEnumerable<Vector2i> source, int z)
    {
        var chunks = new Dictionary<Vector2i, ulong[]>();
        foreach (var tile in source.Distinct())
        {
            var origin = new Vector2i(FloorToChunk(tile.X), FloorToChunk(tile.Y));
            if (!chunks.TryGetValue(origin, out var rows))
                chunks[origin] = rows = new ulong[32];

            var localX = tile.X - origin.X;
            var localY = tile.Y - origin.Y;
            rows[localY] |= 1UL << localX;
        }

        return chunks
            .OrderBy(pair => pair.Key.X)
            .ThenBy(pair => pair.Key.Y)
            .Select(pair => new NCZoneEditorTileChunk(z, pair.Key, TrimRows(pair.Value)))
            .ToArray();
    }

    private static HashSet<TileAtDepth> DecodeTiles(IEnumerable<NCZoneEditorTileChunk> chunks)
    {
        var result = new HashSet<TileAtDepth>();
        foreach (var chunk in chunks)
        {
            for (var y = 0; y < chunk.Rows.Length; y++)
            {
                var row = chunk.Rows[y];
                while (row != 0)
                {
                    var x = BitOperations.TrailingZeroCount(row);
                    result.Add(new TileAtDepth(
                        chunk.Z,
                        new Vector2i(chunk.Origin.X + x, chunk.Origin.Y + y)));
                    row &= row - 1;
                }
            }
        }

        return result;
    }

    private static int FloorToChunk(int coordinate)
    {
        return (int) MathF.Floor(coordinate / 32f) * 32;
    }

    private static ulong[] TrimRows(ulong[] rows)
    {
        var count = rows.Length;
        while (count > 0 && rows[count - 1] == 0)
            count--;
        return rows[..count];
    }

    private static void WriteSnapshot(TextWriter writer, NCZoneEditorSnapshot snapshot)
    {
        writer.WriteLine("# SPDX-FileCopyrightText: 2026 Astro");
        writer.WriteLine("# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0");
        writer.WriteLine("# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.");
        writer.WriteLine();
        writer.WriteLine("# Generated by the Night City zone editor.");
        writer.WriteLine("- type: ncZoneSet");
        writer.WriteLine($"  id: {snapshot.ZoneSet}");
        writer.WriteLine($"  networkId: {snapshot.NetworkId}");
        writer.WriteLine("  zones:");

        foreach (var zone in snapshot.Zones)
        {
            writer.WriteLine($"  - id: {zone.Id}");
            writer.WriteLine($"    kind: {zone.Kind}");
            if (zone.Parent.IsValid)
                writer.WriteLine($"    parent: {zone.Parent}");
            writer.WriteLine($"    name: {JsonSerializer.Serialize(zone.Name)}");
            if (zone.Priority != 0)
                writer.WriteLine($"    priority: {zone.Priority}");
            if (zone.ActivityMode != NCZoneActivityMode.Active)
                writer.WriteLine($"    activityMode: {zone.ActivityMode}");
            writer.WriteLine("    geometry:");

            foreach (var geometry in zone.Geometry)
            {
                writer.WriteLine($"    - kind: {geometry.Kind}");
                if (geometry.Global)
                {
                    writer.WriteLine("      global: true");
                    WriteVertices(writer, geometry.Vertices);
                    continue;
                }

                switch (geometry.Kind)
                {
                    case NCZoneGeometryKind.Polygon:
                        writer.WriteLine($"      z: {geometry.Z}");
                        WriteVertices(writer, geometry.Vertices);
                        break;
                    case NCZoneGeometryKind.Volume:
                        writer.WriteLine($"      minZ: {geometry.MinZ}");
                        writer.WriteLine($"      maxZ: {geometry.MaxZ}");
                        WriteVertices(writer, geometry.Vertices);
                        break;
                    case NCZoneGeometryKind.TileMask:
                        writer.WriteLine("      chunks:");
                        foreach (var chunk in geometry.Chunks)
                        {
                            writer.WriteLine($"      - z: {chunk.Z}");
                            writer.WriteLine($"        origin: {chunk.Origin.X},{chunk.Origin.Y}");
                            writer.WriteLine($"        rows: [{string.Join(", ", chunk.Rows)}]");
                        }
                        break;
                }
            }
        }
    }

    private static void WriteVertices(TextWriter writer, IReadOnlyList<Vector2> vertices)
    {
        writer.WriteLine("      vertices:");
        foreach (var vertex in vertices)
        {
            writer.WriteLine(
                "      - " +
                vertex.X.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                vertex.Y.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    private NCZoneEditorSnapshot CreateSnapshot(NCZoneSetPrototype zoneSet)
    {
        var zones = new NCZoneEditorZone[zoneSet.Zones.Count];
        for (var zoneIndex = 0; zoneIndex < zoneSet.Zones.Count; zoneIndex++)
        {
            var definition = zoneSet.Zones[zoneIndex];
            var kind = _prototypes.Index(definition.Kind);
            var geometry = new NCZoneEditorGeometry[definition.Geometry.Count];

            for (var geometryIndex = 0; geometryIndex < definition.Geometry.Count; geometryIndex++)
            {
                var source = definition.Geometry[geometryIndex];
                geometry[geometryIndex] = new NCZoneEditorGeometry(
                    source.Kind,
                    source.Global,
                    source.Z,
                    source.MinZ,
                    source.MaxZ,
                    source.Vertices.ToArray(),
                    source.Chunks
                        .Select(chunk => new NCZoneEditorTileChunk(
                            chunk.Z,
                            chunk.Origin,
                            chunk.Rows.ToArray()))
                        .ToArray());
            }

            zones[zoneIndex] = new NCZoneEditorZone(
                definition.Id,
                definition.Parent,
                definition.Kind.Id,
                definition.Name,
                definition.Priority,
                definition.ActivityMode,
                kind.EditorColor.RByte,
                kind.EditorColor.GByte,
                kind.EditorColor.BByte,
                geometry);
        }

        return new NCZoneEditorSnapshot(zoneSet.ID, zoneSet.NetworkId, zones);
    }

    private sealed class DraftState(NCZoneEditorSnapshot snapshot)
    {
        public NCZoneEditorSnapshot Snapshot = snapshot;
        public readonly Stack<NCZoneEditorSnapshot> Undo = [];
        public readonly Stack<NCZoneEditorSnapshot> Redo = [];
    }

    private readonly record struct TileAtDepth(int Z, Vector2i Position);
}
