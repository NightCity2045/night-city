// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.Client._NC.City.Zones.Overlays;
using Content.Client.Decals;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Editor;
using Content.Shared._NC.City.Zones.Prototypes;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Placement;
using Robust.Client.Player;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using static Robust.Shared.Input.Binding.PointerInputCmdHandler;

namespace Content.Client._NC.City.Zones;

/// <summary>
/// Maintains the authorized client draft and converts mapper input into bounded editor operations.
/// Geometry is previewed locally while the server remains authoritative.
/// </summary>
public sealed partial class NCZoneEditorClientSystem : EntitySystem
{
    private const int MaximumPendingVertices = 4096;

    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IInputManager _input = default!;
    [Dependency] private IEyeManager _eye = default!;
    [Dependency] private IPlacementManager _placement = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private DecalPlacementSystem _decals = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private InputSystem _inputSystem = default!;

    public NCZoneEditorSnapshot? Snapshot { get; private set; }
    public NCZoneId SelectedZone { get; private set; }
    public NCZoneEditorTool Tool { get; private set; }
    public IReadOnlyList<Vector2> PendingVertices => _pendingVertices;
    public NCMapCoordinates? CursorCoordinates { get; private set; }
    public NCZoneEditorValidationError[] ValidationErrors { get; private set; } = [];

    public event Action? SnapshotUpdated;
    public event Action<string, bool>? OperationCompleted;
    public event Action? ToolChanged;
    public event Action? ValidationUpdated;

    private readonly List<Vector2> _pendingVertices = [];
    private readonly HashSet<Vector2i> _pendingTiles = [];
    private NCZoneGeometryKind _drawingKind;
    private int _drawingZ;
    private int _drawingMinZ;
    private int _drawingMaxZ;
    private bool _drawingGlobal;
    private int _geometryIndex = -1;
    private int _vertexIndex = -1;
    private bool _tileAdd;
    private int _brushSize = 1;
    private bool _painting;
    private int _paintingZ;
    private Vector2i? _lastPaintCenter;
    private float _snapStep = 1f;
    private string? _pendingCreatedZoneName;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCZoneEditorSnapshot>(OnSnapshot);
        SubscribeNetworkEvent<NCZoneEditorPatch>(OnPatch);
        SubscribeNetworkEvent<NCZoneEditorOperationResult>(OnOperationResult);
        SubscribeNetworkEvent<NCZoneEditorValidationResult>(OnValidationResult);

        // Editor placement keys are consumed only while a zone tool is active.
        CommandBinds.Builder
            .BindBefore(EngineKeyFunctions.EditorPlaceObject, new PointerStateInputCmdHandler(
                OnPrimaryDown,
                OnPrimaryUp,
                outsidePrediction: true),
                typeof(PlacementManager),
                typeof(DecalPlacementSystem))
            .BindBefore(EngineKeyFunctions.EditorCancelPlace, new PointerInputCmdHandler(
                OnSecondary,
                outsidePrediction: true),
                typeof(PlacementManager),
                typeof(DecalPlacementSystem))
            .Register<NCZoneEditorClientSystem>();
    }

    public override void Shutdown()
    {
        CommandBinds.Unregister<NCZoneEditorClientSystem>();

        // UI controllers can be disposed before entity systems during client shutdown.
        // Drop callbacks before Close() mutates editor state and raises notifications.
        SnapshotUpdated = null;
        OperationCompleted = null;
        ToolChanged = null;
        ValidationUpdated = null;
        Close();
        base.Shutdown();
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);
        if (Tool == NCZoneEditorTool.None || !_input.MouseScreenPosition.IsValid)
        {
            CursorCoordinates = null;
            return;
        }

        var map = _eye.PixelToMap(_input.MouseScreenPosition);
        if (!_coordinates.TryConvert(map, out var coordinates) ||
            Snapshot?.NetworkId != coordinates.NetworkId)
        {
            CursorCoordinates = null;
            return;
        }

        CursorCoordinates = coordinates;
        if (_painting)
            AddBrushTiles(coordinates);
    }

    public bool Enabled => _overlays.HasOverlay<NCZoneEditorOverlay>();

    public void Open(string? zoneSet = null, bool discardDraft = false)
    {
        if (!Enabled)
            _overlays.AddOverlay(new NCZoneEditorOverlay(this));

        Request(zoneSet, discardDraft);
    }

    public void Close()
    {
        CancelTool();
        _overlays.RemoveOverlay<NCZoneEditorOverlay>();
        Snapshot = null;
        SelectedZone = default;
        ValidationErrors = [];
        SnapshotUpdated?.Invoke();
    }

    public void Request(string? zoneSet = null, bool discardDraft = false)
    {
        RaiseNetworkEvent(new NCZoneEditorSnapshotRequest(zoneSet, discardDraft));
    }

    public bool Select(NCZoneId zoneId)
    {
        if (zoneId.IsValid && Snapshot?.Zones.All(zone => zone.Id != zoneId) != false)
            return false;

        CancelTool();
        SelectedZone = zoneId;
        SnapshotUpdated?.Invoke();
        return true;
    }

    public void CreateZone(string kind, string name, NCZoneId parent)
    {
        _pendingCreatedZoneName = name;
        RaiseNetworkEvent(new NCZoneEditorCreateZoneRequest(kind, name, parent));
    }

    public void UpdateZone(
        NCZoneId zoneId,
        string name,
        NCZoneId parent,
        int priority,
        NCZoneActivityMode activityMode)
    {
        RaiseNetworkEvent(new NCZoneEditorUpdateZoneRequest(
            zoneId,
            name,
            parent,
            priority,
            activityMode));
    }

    public void DeleteZone(NCZoneId zoneId)
    {
        RaiseNetworkEvent(new NCZoneEditorDeleteZoneRequest(zoneId));
    }

    public void DeleteGeometry(NCZoneId zoneId, int geometryIndex)
    {
        RaiseNetworkEvent(new NCZoneEditorDeleteGeometryRequest(zoneId, geometryIndex));
    }

    public void RemoveVertex(NCZoneId zoneId, int geometryIndex, int vertexIndex)
    {
        RaiseNetworkEvent(new NCZoneEditorRemoveVertexRequest(zoneId, geometryIndex, vertexIndex));
    }

    public void SetVertex(NCZoneId zoneId, int geometryIndex, int vertexIndex, Vector2 position)
    {
        RaiseNetworkEvent(new NCZoneEditorSetVertexRequest(
            zoneId,
            geometryIndex,
            vertexIndex,
            position));
    }

    public void InsertVertex(
        NCZoneId zoneId,
        int geometryIndex,
        int afterVertexIndex,
        Vector2 position)
    {
        RaiseNetworkEvent(new NCZoneEditorInsertVertexRequest(
            zoneId,
            geometryIndex,
            afterVertexIndex,
            position));
    }

    public void Undo()
    {
        CancelTool();
        RaiseNetworkEvent(new NCZoneEditorHistoryRequest(false));
    }

    public void Redo()
    {
        CancelTool();
        RaiseNetworkEvent(new NCZoneEditorHistoryRequest(true));
    }

    public void ValidateDraft()
    {
        RaiseNetworkEvent(new NCZoneEditorValidationRequest());
    }

    public void Export(string fileName)
    {
        RaiseNetworkEvent(new NCZoneEditorExportRequest(fileName));
    }

    public bool StartPolygon(
        NCZoneGeometryKind kind,
        int z,
        int minZ,
        int maxZ,
        float snapStep,
        bool global = false)
    {
        if (!SelectedZone.IsValid ||
            kind is not (NCZoneGeometryKind.Polygon or NCZoneGeometryKind.Volume))
        {
            return false;
        }

        PrepareTool();
        Tool = kind == NCZoneGeometryKind.Polygon
            ? NCZoneEditorTool.DrawPolygon
            : NCZoneEditorTool.DrawVolume;
        _drawingKind = kind;
        _drawingZ = z;
        _drawingMinZ = minZ;
        _drawingMaxZ = maxZ;
        _drawingGlobal = global;
        _snapStep = Math.Clamp(snapStep, 0.05f, 16f);
        ToolChanged?.Invoke();
        return true;
    }

    public bool FinishPolygon()
    {
        if (Tool is not (NCZoneEditorTool.DrawPolygon or NCZoneEditorTool.DrawVolume) ||
            _pendingVertices.Count < 3)
        {
            return false;
        }

        RaiseNetworkEvent(new NCZoneEditorAddGeometryRequest(
            SelectedZone,
            _drawingKind,
            _drawingZ,
            _drawingMinZ,
            _drawingMaxZ,
            _pendingVertices.ToArray(),
            [],
            _drawingGlobal));
        CancelTool();
        return true;
    }

    public bool CreateTileMask(int z)
    {
        if (!SelectedZone.IsValid)
            return false;

        RaiseNetworkEvent(new NCZoneEditorAddGeometryRequest(
            SelectedZone,
            NCZoneGeometryKind.TileMask,
            z,
            z,
            z,
            [],
            [],
            false));
        return true;
    }

    /// <summary>
    /// Submits one complete geometry operation. The interactive tools call the same server request.
    /// </summary>
    public void SubmitGeometry(
        NCZoneId zoneId,
        NCZoneGeometryKind kind,
        int z,
        int minZ,
        int maxZ,
        Vector2[] vertices,
        Vector2i[] tiles,
        bool global = false)
    {
        RaiseNetworkEvent(new NCZoneEditorAddGeometryRequest(
            zoneId,
            kind,
            z,
            minZ,
            maxZ,
            vertices,
            tiles,
            global));
    }

    /// <summary>
    /// Changes the vertical scope of existing polygonal geometry without exposing editor components.
    /// </summary>
    public void SetGeometryScope(
        NCZoneId zoneId,
        int geometryIndex,
        bool global,
        int minZ,
        int maxZ)
    {
        RaiseNetworkEvent(new NCZoneEditorSetGeometryScopeRequest(
            zoneId,
            geometryIndex,
            global,
            minZ,
            maxZ));
    }

    /// <summary>
    /// Commits a bounded tile-mask stroke as one undoable operation.
    /// </summary>
    public void PatchTiles(
        NCZoneId zoneId,
        int geometryIndex,
        int z,
        Vector2i[] tiles,
        bool add)
    {
        RaiseNetworkEvent(new NCZoneEditorTilePatchRequest(
            zoneId,
            geometryIndex,
            z,
            tiles,
            add));
    }

    public bool StartTileBrush(int geometryIndex, int brushSize, bool add)
    {
        if (!TryGetSelectedGeometry(geometryIndex, out var geometry) ||
            geometry.Kind != NCZoneGeometryKind.TileMask)
        {
            return false;
        }

        PrepareTool();
        Tool = add ? NCZoneEditorTool.PaintTiles : NCZoneEditorTool.EraseTiles;
        _geometryIndex = geometryIndex;
        _brushSize = Math.Clamp(brushSize, 1, 9);
        if (_brushSize % 2 == 0)
            _brushSize++;
        _tileAdd = add;
        ToolChanged?.Invoke();
        return true;
    }

    public bool StartMoveVertex(int geometryIndex, int vertexIndex, float snapStep)
    {
        if (!TryGetSelectedGeometry(geometryIndex, out var geometry) ||
            geometry.Kind is not (NCZoneGeometryKind.Polygon or NCZoneGeometryKind.Volume) ||
            vertexIndex < 0 ||
            vertexIndex >= geometry.Vertices.Length)
        {
            return false;
        }

        PrepareTool();
        Tool = NCZoneEditorTool.MoveVertex;
        _geometryIndex = geometryIndex;
        _vertexIndex = vertexIndex;
        _snapStep = Math.Clamp(snapStep, 0.05f, 16f);
        ToolChanged?.Invoke();
        return true;
    }

    public bool StartInsertVertex(int geometryIndex, int afterVertexIndex, float snapStep)
    {
        if (!TryGetSelectedGeometry(geometryIndex, out var geometry) ||
            geometry.Kind is not (NCZoneGeometryKind.Polygon or NCZoneGeometryKind.Volume) ||
            afterVertexIndex < 0 ||
            afterVertexIndex >= geometry.Vertices.Length)
        {
            return false;
        }

        PrepareTool();
        Tool = NCZoneEditorTool.InsertVertex;
        _geometryIndex = geometryIndex;
        _vertexIndex = afterVertexIndex;
        _snapStep = Math.Clamp(snapStep, 0.05f, 16f);
        ToolChanged?.Invoke();
        return true;
    }

    public void CancelTool()
    {
        var wasActive = Tool != NCZoneEditorTool.None;
        Tool = NCZoneEditorTool.None;
        _pendingVertices.Clear();
        _pendingTiles.Clear();
        _painting = false;
        _lastPaintCenter = null;

        // PlacementManager normally owns the editor context. Zone tools restore entity input themselves.
        if (wasActive)
            _inputSystem.SetEntityContextActive();

        ToolChanged?.Invoke();
    }

    public bool TryGetCurrentDepth(out int z)
    {
        z = default;
        if (_players.LocalEntity is not { } entity ||
            !_coordinates.TryGetCoordinates(entity, out var coordinates) ||
            Snapshot?.NetworkId != coordinates.NetworkId)
        {
            return false;
        }

        z = coordinates.Z;
        return true;
    }

    private void PrepareTool()
    {
        CancelTool();
        // Zone tools and entity/decal placement must never consume the same click.
        _placement.Clear();
        _decals.SetActive(false);
        _input.Contexts.SetActiveContext("editor");
    }

    private bool OnPrimaryDown(ICommonSession? session, EntityCoordinates coordinates, EntityUid uid)
    {
        if (Tool == NCZoneEditorTool.None ||
            !TryConvert(coordinates, out var stable))
        {
            return false;
        }

        switch (Tool)
        {
            case NCZoneEditorTool.DrawPolygon:
            case NCZoneEditorTool.DrawVolume:
                if (_pendingVertices.Count < MaximumPendingVertices)
                {
                    var position = Snap(stable.Position);

                    // Clicking the first vertex is the conventional way to close a polygon. The closing edge is
                    // implicit in stored geometry, so finish without appending a duplicate endpoint.
                    if (_pendingVertices.Count >= 3 && position == _pendingVertices[0])
                    {
                        FinishPolygon();
                        break;
                    }

                    // Ignore repeated clicks because they would create a zero-length polygon edge.
                    if (_pendingVertices.Count != 0 && position == _pendingVertices[^1])
                        break;

                    _pendingVertices.Add(position);
                    ToolChanged?.Invoke();
                }
                break;
            case NCZoneEditorTool.MoveVertex:
                RaiseNetworkEvent(new NCZoneEditorSetVertexRequest(
                    SelectedZone,
                    _geometryIndex,
                    _vertexIndex,
                    Snap(stable.Position)));
                CancelTool();
                break;
            case NCZoneEditorTool.InsertVertex:
                RaiseNetworkEvent(new NCZoneEditorInsertVertexRequest(
                    SelectedZone,
                    _geometryIndex,
                    _vertexIndex,
                    Snap(stable.Position)));
                CancelTool();
                break;
            case NCZoneEditorTool.PaintTiles:
            case NCZoneEditorTool.EraseTiles:
                _painting = true;
                _paintingZ = stable.Z;
                AddBrushTiles(stable);
                break;
        }

        return true;
    }

    private bool OnPrimaryUp(ICommonSession? session, EntityCoordinates coordinates, EntityUid uid)
    {
        if (Tool is not (NCZoneEditorTool.PaintTiles or NCZoneEditorTool.EraseTiles))
            return Tool != NCZoneEditorTool.None;

        _painting = false;
        if (_pendingTiles.Count != 0)
        {
            RaiseNetworkEvent(new NCZoneEditorTilePatchRequest(
                SelectedZone,
                _geometryIndex,
                _paintingZ,
                _pendingTiles.ToArray(),
                _tileAdd));
        }

        _pendingTiles.Clear();
        _lastPaintCenter = null;
        return true;
    }

    private bool OnSecondary(in PointerInputCmdArgs args)
    {
        if (Tool == NCZoneEditorTool.None)
            return false;

        if (Tool is NCZoneEditorTool.DrawPolygon or NCZoneEditorTool.DrawVolume &&
            _pendingVertices.Count >= 3)
        {
            FinishPolygon();
        }
        else
        {
            CancelTool();
        }

        return true;
    }

    private bool TryConvert(EntityCoordinates source, out NCMapCoordinates coordinates)
    {
        coordinates = default;
        if (!source.IsValid(EntityManager))
            return false;

        var map = _transform.ToMapCoordinates(source);
        return _coordinates.TryConvert(map, out coordinates) &&
               Snapshot?.NetworkId == coordinates.NetworkId;
    }

    private Vector2 Snap(Vector2 position)
    {
        return new Vector2(
            MathF.Round(position.X / _snapStep) * _snapStep,
            MathF.Round(position.Y / _snapStep) * _snapStep);
    }

    private void AddBrushTiles(NCMapCoordinates coordinates)
    {
        var center = ToTile(coordinates.Position);
        if (_lastPaintCenter == center)
            return;

        // Fill every cell between mouse samples. Fast cursor movement otherwise left holes
        // because input events are less frequent than crossed map tiles.
        if (_lastPaintCenter is { } previous)
        {
            foreach (var tile in RasterizeLine(previous, center))
                AddBrushAt(tile);
        }
        else
        {
            AddBrushAt(center);
        }

        _lastPaintCenter = center;
    }

    private void AddBrushAt(Vector2i center)
    {
        var radius = _brushSize / 2;
        for (var x = -radius; x <= radius; x++)
        {
            for (var y = -radius; y <= radius; y++)
                _pendingTiles.Add(center + new Vector2i(x, y));
        }
    }

    private static IEnumerable<Vector2i> RasterizeLine(Vector2i start, Vector2i end)
    {
        var x = start.X;
        var y = start.Y;
        var dx = Math.Abs(end.X - start.X);
        var dy = -Math.Abs(end.Y - start.Y);
        var stepX = start.X < end.X ? 1 : -1;
        var stepY = start.Y < end.Y ? 1 : -1;
        var error = dx + dy;

        while (true)
        {
            yield return new Vector2i(x, y);
            if (x == end.X && y == end.Y)
                yield break;

            var doubled = error * 2;
            if (doubled >= dy)
            {
                error += dy;
                x += stepX;
            }

            if (doubled <= dx)
            {
                error += dx;
                y += stepY;
            }
        }
    }

    private bool TryGetSelectedGeometry(int index, out NCZoneEditorGeometry geometry)
    {
        geometry = default!;
        var zone = Snapshot?.Zones.FirstOrDefault(candidate => candidate.Id == SelectedZone);
        if (zone == null || index < 0 || index >= zone.Geometry.Length)
            return false;

        geometry = zone.Geometry[index];
        return true;
    }

    private static Vector2i ToTile(Vector2 position)
    {
        return new Vector2i(
            (int) MathF.Floor(position.X),
            (int) MathF.Floor(position.Y));
    }

    private void OnSnapshot(NCZoneEditorSnapshot snapshot)
    {
        Snapshot = snapshot;

        if (_pendingCreatedZoneName != null &&
            snapshot.Zones.LastOrDefault(zone => zone.Name == _pendingCreatedZoneName) is { } created)
        {
            SelectedZone = created.Id;
            _pendingCreatedZoneName = null;
        }
        else if (SelectedZone.IsValid && snapshot.Zones.All(zone => zone.Id != SelectedZone))
        {
            SelectedZone = default;
        }

        // A freshly opened editor should be usable immediately without a hidden selection step.
        if (!SelectedZone.IsValid && snapshot.Zones.FirstOrDefault() is { } first)
            SelectedZone = first.Id;

        SnapshotUpdated?.Invoke();
    }

    private void OnPatch(NCZoneEditorPatch patch)
    {
        if (Snapshot is not { } snapshot || snapshot.Revision != patch.BaseRevision)
        {
            // A lost or reordered patch cannot be applied safely; request the authoritative draft.
            Request();
            return;
        }

        var removed = patch.Removed.ToHashSet();
        var upserted = patch.Upserted.ToDictionary(zone => zone.Id);
        var zones = new List<NCZoneEditorZone>(snapshot.Zones.Length + upserted.Count);
        foreach (var zone in snapshot.Zones)
        {
            if (removed.Contains(zone.Id))
                continue;

            if (upserted.Remove(zone.Id, out var replacement))
                zones.Add(replacement);
            else
                zones.Add(zone);
        }

        zones.AddRange(upserted.Values);
        OnSnapshot(new NCZoneEditorSnapshot(
            snapshot.ZoneSet,
            snapshot.NetworkId,
            zones.ToArray(),
            patch.Revision,
            patch.Dirty));
    }

    private void OnOperationResult(NCZoneEditorOperationResult result)
    {
        if (!result.Success)
            _pendingCreatedZoneName = null;

        OperationCompleted?.Invoke(result.Message, result.Success);
    }

    private void OnValidationResult(NCZoneEditorValidationResult result)
    {
        ValidationErrors = result.Errors;
        ValidationUpdated?.Invoke();
    }

}

public enum NCZoneEditorTool : byte
{
    None,
    DrawPolygon,
    DrawVolume,
    MoveVertex,
    InsertVertex,
    PaintTiles,
    EraseTiles,
}
