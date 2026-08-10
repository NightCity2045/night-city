// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Linq;
using Content.Shared._NC.Agent.UI;
using Content.Shared.Atmos;
using Content.Shared.Pinpointer;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Client._NC.Agent;

/// <summary>
/// Standalone semantic renderer for Night City cartography.
/// It deliberately does not inherit the vanilla station NavMapControl.
/// </summary>
public sealed partial class NCAgentMapControl : LayoutContainer
{
    private const float CanvasTop = 48f;
    private const float CanvasBottom = 38f;
    private const float MinimumCanvasHeight = CanvasTop + CanvasBottom + 48f;
    private const float MinimumZoom = 0.45f;
    private const float MaximumZoom = 8f;
    private const float ArchitectureSignatureInterval = 0.75f;

    private static readonly Color CanvasColor = Color.FromHex("#030A0F");
    private static readonly Color GridColor = Color.FromHex("#17343D42");
    private static readonly Color FineGridColor = Color.FromHex("#15323A22");
    private static readonly Color BorderColor = Color.FromHex("#2AC9BEA0");
    private static readonly Color SignalColor = Color.FromHex("#62FFF1");
    private static readonly Color ScanColor = Color.FromHex("#42E9DC20");
    private static readonly Color DistrictFill = Color.FromHex("#123C4538");
    private static readonly Color DistrictLine = Color.FromHex("#38B9B0A0");
    private static readonly Color SectorFill = Color.FromHex("#15304142");
    private static readonly Color SectorLine = Color.FromHex("#508DFF99");
    private static readonly Color StreetFill = Color.FromHex("#211B1028");
    private static readonly Color StreetLine = Color.FromHex("#F0C75EB8");
    private static readonly Color BuildingFill = Color.FromHex("#0B2630C8");
    private static readonly Color BuildingLine = Color.FromHex("#75FFF0D8");
    private static readonly Color FloorColor = Color.FromHex("#081A23E8");
    private static readonly Color FloorCityColor = Color.FromHex("#081A2390");
    private static readonly Color WallFillColor = Color.FromHex("#17464FCC");
    private static readonly Color WallLineColor = Color.FromHex("#65E8DCE8");
    private static readonly Color DoorFillColor = Color.FromHex("#5A431DC0");
    private static readonly Color DoorLineColor = Color.FromHex("#FFD66BEF");

    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly Font _font;
    private readonly Font _smallFont;
    private readonly SharedTransformSystem _transformSystem;
    private readonly Label _zoomLabel;
    private readonly Label _lodLabel;
    private readonly Label _coordinateLabel;
    private readonly Label _networkLabel;
    private readonly Label _noticeLabel;
    private readonly Button _labelsButton;
    private readonly List<MapFeature> _features = [];
    private readonly HashSet<string> _drawnLabels = [];
    private readonly List<UIBox2> _labelRects = [];
    private readonly List<ArchitectureChunk> _architecture = [];

    private EntityUid? _mapGridUid;
    private int _navMapSignature = int.MinValue;
    private float _architectureSignatureTimer;
    private Vector2 _userPosition;
    private Vector2 _architectureBoundsMin;
    private Vector2 _architectureBoundsMax;
    private Vector2 _boundsMin;
    private Vector2 _boundsMax;
    private Vector2 _viewCenter;
    private Vector2 _targetCenter;
    private float _zoom = 1f;
    private float _targetZoom = 1f;
    private float _mapScale = 1f;
    private bool _hasBounds;
    private bool _hasArchitectureBounds;
    private bool _architectureAvailable;
    private bool _dragging;
    private bool _labelsVisible = true;
    private string _serverNotice = string.Empty;

    public NCAgentMapControl()
    {
        IoCManager.InjectDependencies(this);
        _transformSystem = _entities.System<SharedTransformSystem>();
        var cache = IoCManager.Resolve<IResourceCache>();
        var fontResource = cache.GetResource<FontResource>("/EngineFonts/NotoSans/NotoSans-Regular.ttf");
        _font = new VectorFont(fontResource, 12);
        _smallFont = new VectorFont(fontResource, 10);

        // The small intrinsic height prevents BoxContainer from collapsing the renderer before stretch allocation.
        MinSize = new Vector2(0f, MinimumCanvasHeight);
        HorizontalExpand = true;
        VerticalExpand = true;
        RectClipContent = true;
        MouseFilter = MouseFilterMode.Stop;

        _zoomLabel = CreateHudLabel("1.0X", SignalColor);
        _lodLabel = CreateHudLabel("STREETS", Color.FromHex("#F0C75E"));
        _coordinateLabel = CreateHudLabel("X -- // Y -- // Z --", Color.FromHex("#6CA8A4"));
        _networkLabel = CreateHudLabel("CITY GRID // OFFLINE", SignalColor);
        _noticeLabel = CreateHudLabel(string.Empty, Color.FromHex("#FFD66B"));
        _noticeLabel.HorizontalAlignment = HAlignment.Center;
        _noticeLabel.VerticalAlignment = VAlignment.Top;
        _noticeLabel.Margin = new Thickness(8f, CanvasTop + 5f);
        _noticeLabel.Visible = false;
        _labelsButton = CreateHudButton("LABELS ON", SignalColor);

        var topHud = CreateTopHud();
        AddChild(topHud);
        SetAnchorPreset(topHud, LayoutPreset.TopWide);

        var bottomHud = CreateBottomControls();
        AddChild(bottomHud);
        SetAnchorPreset(bottomHud, LayoutPreset.BottomWide);
        SetGrowVertical(bottomHud, GrowDirection.Begin);

        AddChild(_noticeLabel);
        SetAnchorPreset(_noticeLabel, LayoutPreset.CenterTop);
        SetGrowHorizontal(_noticeLabel, GrowDirection.Both);
    }

    /// <summary>
    /// Selects the grid whose synchronized NavMap chunks provide floors, walls, and doors.
    /// </summary>
    public void SetMapSource(EntityUid? gridUid)
    {
        _mapGridUid = gridUid;
        _navMapSignature = int.MinValue;
        RefreshArchitectureIfRequired(force: true);
    }

    /// <summary>
    /// Replaces all renderer data with one immutable server snapshot and fits it to the viewport.
    /// Polygon triangulation and tile-row merging happen once here, never in Draw.
    /// </summary>
    public void SetMapData(NCAgentMapUpdateMessage update)
    {
        _features.Clear();
        _userPosition = update.UserPosition;
        _serverNotice = update.Notice;

        foreach (var source in update.Features)
        {
            var feature = new MapFeature(source);
            if (!feature.HasGeometry)
                continue;

            _features.Add(feature);
        }

        _features.Sort(static (left, right) => left.Kind.CompareTo(right.Kind));
        RecalculateBounds();
        FitCity();

        var network = update.NetworkId.Length > 8 ? update.NetworkId[..8].ToUpperInvariant() : update.NetworkId.ToUpperInvariant();
        _networkLabel.Text = string.IsNullOrEmpty(network)
            ? "CITY GRID // OFFLINE"
            : $"CITY GRID // {network} // Z {update.Z:+0;-0;0}";
        _coordinateLabel.Text = $"X {_userPosition.X:0.0} // Y {_userPosition.Y:0.0} // Z {update.Z:+0;-0;0}";
        RefreshNotice();
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);
        if (args.Function != EngineKeyFunctions.Use)
            return;

        _dragging = true;
        args.Handle();
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);
        if (args.Function != EngineKeyFunctions.Use)
            return;

        _dragging = false;
        args.Handle();
    }

    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);
        if (!_dragging || _mapScale <= 0f)
            return;

        // Moving the pointer drags the map itself, so the logical camera moves in the opposite direction.
        var worldDelta = new Vector2(args.Relative.X, -args.Relative.Y) / _mapScale;
        _viewCenter -= worldDelta;
        _targetCenter = _viewCenter;
        args.Handle();
    }

    protected override void MouseWheel(GUIMouseWheelEventArgs args)
    {
        base.MouseWheel(args);
        SetTargetZoom(_targetZoom * MathF.Pow(1.18f, args.Delta.Y));
        args.Handle();
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        _architectureSignatureTimer += args.DeltaSeconds;
        if (_architectureSignatureTimer >= ArchitectureSignatureInterval)
        {
            _architectureSignatureTimer = 0f;
            RefreshArchitectureIfRequired();
        }

        // Smooth camera movement keeps wheel zoom and recenter actions readable on the small Agent screen.
        var blend = 1f - MathF.Exp(-12f * args.DeltaSeconds);
        _zoom = float.Lerp(_zoom, _targetZoom, blend);
        _viewCenter = Vector2.Lerp(_viewCenter, _targetCenter, blend);

        var lod = GetLod();
        _zoomLabel.Text = $"{_zoom:0.0}X";
        _lodLabel.Text = lod switch
        {
            MapLod.City => "DISTRICTS",
            MapLod.Streets => "STREETS",
            _ => "BUILDINGS",
        };
        _lodLabel.FontColorOverride = lod switch
        {
            MapLod.City => Color.FromHex("#E05CFF"),
            MapLod.Streets => Color.FromHex("#F0C75E"),
            _ => SignalColor,
        };
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        _mapScale = CalculateMapScale();
        DrawCanvas(handle);

        var lod = GetLod();
        DrawArchitectureFloors(handle, lod);

        foreach (var feature in _features)
        {
            if (ShouldDraw(feature.Kind, lod) && IsFeatureVisible(feature))
                DrawFeature(handle, feature, lod);
        }

        DrawArchitectureDetails(handle, lod);

        if (_labelsVisible)
            DrawLabels(handle, lod);

        DrawUserMarker(handle);
        DrawSignalOverlay(handle);
    }

    private Control CreateTopHud()
    {
        var statusRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
        };
        statusRow.AddChild(_networkLabel);
        statusRow.AddChild(new Control { HorizontalExpand = true });
        statusRow.AddChild(_lodLabel);
        statusRow.AddChild(CreateHudLabel(" // ", Color.FromHex("#427C78")));
        statusRow.AddChild(_zoomLabel);

        // Two compact rows preserve all map status information on the portrait Agent screen.
        var content = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new Thickness(8f, 3f),
        };
        content.AddChild(statusRow);
        content.AddChild(_coordinateLabel);
        return WrapHud(content, HAlignment.Stretch, VAlignment.Top);
    }

    private Control CreateBottomControls()
    {
        var zoomOut = CreateHudButton("-", SignalColor);
        zoomOut.OnPressed += _ => SetTargetZoom(_targetZoom / 1.35f);

        var zoomIn = CreateHudButton("+", SignalColor);
        zoomIn.OnPressed += _ => SetTargetZoom(_targetZoom * 1.35f);

        var center = CreateHudButton("CENTER", Color.FromHex("#8DFFF5"));
        center.OnPressed += _ => CenterOnUser();

        var fit = CreateHudButton("FIT", Color.FromHex("#8FB8FF"));
        fit.OnPressed += _ => FitCity();

        _labelsButton.OnPressed += _ => ToggleLabels();

        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            Margin = new Thickness(7f, 4f),
        };
        row.AddChild(zoomOut);
        row.AddChild(zoomIn);
        row.AddChild(center);
        row.AddChild(fit);
        row.AddChild(_labelsButton);
        row.AddChild(new Control { HorizontalExpand = true });
        return WrapHud(row, HAlignment.Stretch, VAlignment.Bottom);
    }

    private static PanelContainer WrapHud(Control content, HAlignment horizontal, VAlignment vertical)
    {
        var panel = new PanelContainer
        {
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            HorizontalExpand = horizontal == HAlignment.Stretch,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.FromHex("#07151BEF"),
                BorderColor = Color.FromHex("#1E696480"),
                BorderThickness = new Thickness(0f, 1f),
            },
        };
        panel.AddChild(content);
        return panel;
    }

    private static Label CreateHudLabel(string text, Color color)
    {
        return new Label
        {
            Text = text,
            FontColorOverride = color,
            VerticalAlignment = VAlignment.Center,
        };
    }

    private static Button CreateHudButton(string text, Color color)
    {
        var button = new Button
        {
            Text = text,
            MinWidth = text.Length <= 1 ? 30f : 58f,
            Margin = new Thickness(2f, 0f),
        };
        button.Label.FontColorOverride = color;
        return button;
    }

    private void ToggleLabels()
    {
        _labelsVisible = !_labelsVisible;
        _labelsButton.Text = _labelsVisible ? "LABELS ON" : "LABELS OFF";
        _labelsButton.Label.FontColorOverride = _labelsVisible ? SignalColor : Color.FromHex("#77B8B3");
    }

    private void SetTargetZoom(float value)
    {
        _targetZoom = Math.Clamp(value, MinimumZoom, MaximumZoom);
    }

    private void CenterOnUser()
    {
        _targetCenter = _userPosition;
        if (_targetZoom < 1.25f)
            _targetZoom = 1.25f;
    }

    private void FitCity()
    {
        _targetCenter = (_boundsMin + _boundsMax) / 2f;
        _viewCenter = _targetCenter;
        _targetZoom = 1f;
        _zoom = 1f;
    }

    private void RecalculateBounds()
    {
        _hasBounds = false;
        IncludePoint(_userPosition);

        foreach (var feature in _features)
        {
            IncludePoint(feature.BoundsMin);
            IncludePoint(feature.BoundsMax);
        }

        if (_hasArchitectureBounds)
        {
            IncludePoint(_architectureBoundsMin);
            IncludePoint(_architectureBoundsMax);
        }

        EnsureUsefulBounds();
    }

    private void RefreshNotice()
    {
        var notice = _serverNotice;
        if (string.IsNullOrWhiteSpace(notice) && !_architectureAvailable)
            notice = "LOCAL ARCHITECTURE UNAVAILABLE // ZONES ONLY";

        _noticeLabel.Text = notice;
        _noticeLabel.Visible = !string.IsNullOrWhiteSpace(notice);
    }

    private void IncludePoint(Vector2 point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
            return;

        if (!_hasBounds)
        {
            _boundsMin = point;
            _boundsMax = point;
            _hasBounds = true;
            return;
        }

        _boundsMin = Vector2.Min(_boundsMin, point);
        _boundsMax = Vector2.Max(_boundsMax, point);
    }

    private void EnsureUsefulBounds()
    {
        if (!_hasBounds)
        {
            _boundsMin = new Vector2(-8f);
            _boundsMax = new Vector2(8f);
            _hasBounds = true;
            return;
        }

        var size = _boundsMax - _boundsMin;
        if (size.X < 8f)
        {
            _boundsMin.X -= (8f - size.X) / 2f;
            _boundsMax.X += (8f - size.X) / 2f;
        }
        if (size.Y < 8f)
        {
            _boundsMin.Y -= (8f - size.Y) / 2f;
            _boundsMax.Y += (8f - size.Y) / 2f;
        }
    }

    private float CalculateMapScale()
    {
        var canvasHeight = MathF.Max(1f, PixelHeight - CanvasTop - CanvasBottom);
        var worldSize = Vector2.Max(_boundsMax - _boundsMin, new Vector2(1f));
        var fitX = MathF.Max(1f, PixelWidth - 54f) / worldSize.X;
        var fitY = MathF.Max(1f, canvasHeight - 42f) / worldSize.Y;
        return MathF.Max(0.01f, MathF.Min(fitX, fitY) * _zoom);
    }

    private Vector2 MapToScreen(Vector2 point)
    {
        var canvasHeight = MathF.Max(1f, PixelHeight - CanvasTop - CanvasBottom);
        var center = new Vector2(PixelWidth / 2f, CanvasTop + canvasHeight / 2f);
        var relative = point - _viewCenter;
        return center + new Vector2(relative.X, -relative.Y) * _mapScale;
    }

    private void DrawCanvas(DrawingHandleScreen handle)
    {
        handle.DrawRect(PixelSizeBox, CanvasColor);

        var coarseSpacing = Math.Clamp(32f * _zoom, 24f, 64f);
        var fineSpacing = coarseSpacing / 2f;
        var coarseOffset = new Vector2(
            PositiveModulo(-_viewCenter.X * _mapScale, coarseSpacing),
            PositiveModulo(_viewCenter.Y * _mapScale, coarseSpacing));
        var fineOffset = new Vector2(
            PositiveModulo(-_viewCenter.X * _mapScale, fineSpacing),
            PositiveModulo(_viewCenter.Y * _mapScale, fineSpacing));

        DrawGrid(handle, fineSpacing, fineOffset, FineGridColor);
        DrawGrid(handle, coarseSpacing, coarseOffset, GridColor);
    }

    private void DrawGrid(DrawingHandleScreen handle, float spacing, Vector2 offset, Color color)
    {
        for (var x = offset.X; x <= PixelWidth; x += spacing)
            handle.DrawLine(new Vector2(x, CanvasTop), new Vector2(x, PixelHeight - CanvasBottom), color);
        for (var y = CanvasTop + offset.Y; y <= PixelHeight - CanvasBottom; y += spacing)
            handle.DrawLine(new Vector2(0f, y), new Vector2(PixelWidth, y), color);
    }

    /// <summary>
    /// Compares chunk identities periodically and rebuilds only when the vanilla NavMap system replaces geometry.
    /// Modified client chunks are replaced as objects, so tile contents never need to be hashed or scanned here.
    /// </summary>
    private void RefreshArchitectureIfRequired(bool force = false)
    {
        if (_mapGridUid is not { Valid: true } gridUid)
        {
            _navMapSignature = 0;
            if (_architecture.Count == 0 && !_architectureAvailable)
                return;

            ClearArchitecture();
            RecalculateBounds();
            RefreshNotice();
            return;
        }

        if (!_entities.TryGetComponent<NavMapComponent>(gridUid, out var navMap))
        {
            _navMapSignature = 0;
            if (_architecture.Count == 0 && !_architectureAvailable)
                return;

            ClearArchitecture();
            RecalculateBounds();
            RefreshNotice();
            return;
        }

        var signature = CalculateNavMapSignature(navMap);
        if (!force && signature == _navMapSignature)
            return;

        _navMapSignature = signature;
        RebuildArchitecture(gridUid);
        RecalculateBounds();
        RefreshNotice();
    }

    private static int CalculateNavMapSignature(NavMapComponent navMap)
    {
        unchecked
        {
            var signature = navMap.Chunks.Count;
            foreach (var (origin, chunk) in navMap.Chunks)
            {
                signature = signature * 397 ^ origin.GetHashCode();
                signature = signature * 397 ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(chunk);
            }
            return signature;
        }
    }

    private void RebuildArchitecture(EntityUid gridUid)
    {
        ClearArchitecture();
        if (!_entities.TryGetComponent<NavMapComponent>(gridUid, out var navMap) ||
            !_entities.TryGetComponent<MapGridComponent>(gridUid, out var grid) ||
            !_entities.TryGetComponent<TransformComponent>(gridUid, out var transform) ||
            navMap.Chunks.Count == 0)
        {
            return;
        }

        var worldMatrix = _transformSystem.GetWorldMatrix(transform);
        foreach (var chunk in navMap.Chunks.Values)
        {
            var builder = new ArchitectureChunkBuilder(worldMatrix);
            BuildFloorGeometry(chunk, grid.TileSize, builder);
            BuildWallAndDoorGeometry(navMap, chunk, grid.TileSize, builder);
            if (!builder.HasGeometry)
                continue;

            var architectureChunk = new ArchitectureChunk(builder);
            _architecture.Add(architectureChunk);
            IncludeArchitecturePoint(architectureChunk.BoundsMin);
            IncludeArchitecturePoint(architectureChunk.BoundsMax);
        }

        _architectureAvailable = _architecture.Count > 0;
    }

    private void ClearArchitecture()
    {
        _architecture.Clear();
        _hasArchitectureBounds = false;
        _architectureAvailable = false;
    }

    private void IncludeArchitecturePoint(Vector2 point)
    {
        if (!_hasArchitectureBounds)
        {
            _architectureBoundsMin = point;
            _architectureBoundsMax = point;
            _hasArchitectureBounds = true;
            return;
        }

        _architectureBoundsMin = Vector2.Min(_architectureBoundsMin, point);
        _architectureBoundsMax = Vector2.Max(_architectureBoundsMax, point);
    }

    private static void BuildFloorGeometry(
        NavMapChunk chunk,
        float tileSize,
        ArchitectureChunkBuilder builder)
    {
        // Merge adjacent floor cells within each chunk row to reduce triangle count substantially.
        for (var y = 0; y < SharedNavMapSystem.ChunkSize; y++)
        {
            var x = 0;
            while (x < SharedNavMapSystem.ChunkSize)
            {
                while (x < SharedNavMapSystem.ChunkSize &&
                       !HasCategory(chunk, x, y, SharedNavMapSystem.FloorMask))
                {
                    x++;
                }
                if (x >= SharedNavMapSystem.ChunkSize)
                    break;

                var start = x;
                while (x < SharedNavMapSystem.ChunkSize &&
                       HasCategory(chunk, x, y, SharedNavMapSystem.FloorMask))
                {
                    x++;
                }

                var tile = chunk.Origin * SharedNavMapSystem.ChunkSize + new Vector2i(start, y);
                var minimum = new Vector2(tile.X, tile.Y) * tileSize;
                var maximum = minimum + new Vector2((x - start) * tileSize, tileSize);
                builder.AddFloorQuad(minimum, maximum);
            }
        }
    }

    private static void BuildWallAndDoorGeometry(
        NavMapComponent navMap,
        NavMapChunk chunk,
        float tileSize,
        ArchitectureChunkBuilder builder)
    {
        for (var i = 0; i < SharedNavMapSystem.ArraySize; i++)
        {
            var tileData = chunk.TileData[i];
            var relative = SharedNavMapSystem.GetTileFromIndex(i);
            var tile = chunk.Origin * SharedNavMapSystem.ChunkSize + relative;
            var minimum = new Vector2(tile.X, tile.Y) * tileSize;
            var maximum = minimum + new Vector2(tileSize);

            var wallData = (tileData & SharedNavMapSystem.WallMask) >> (int) NavMapChunkType.Wall;
            if (wallData != 0)
            {
                if (wallData == SharedNavMapSystem.AllDirMask)
                    builder.AddWallQuad(minimum + new Vector2(tileSize * 0.08f), maximum - new Vector2(tileSize * 0.08f));

                AddWallEdge(navMap, builder, tile, minimum, tileSize,
                    wallData, AtmosDirection.North, AtmosDirection.South, Vector2i.Up);
                AddWallEdge(navMap, builder, tile, minimum, tileSize,
                    wallData, AtmosDirection.South, AtmosDirection.North, Vector2i.Down);
                AddWallEdge(navMap, builder, tile, minimum, tileSize,
                    wallData, AtmosDirection.East, AtmosDirection.West, Vector2i.Right);
                AddWallEdge(navMap, builder, tile, minimum, tileSize,
                    wallData, AtmosDirection.West, AtmosDirection.East, Vector2i.Left);
            }

            var doorData = (tileData & SharedNavMapSystem.AirlockMask) >> (int) NavMapChunkType.Airlock;
            if (doorData == 0)
                continue;

            var inset = new Vector2(tileSize * 0.18f);
            builder.AddDoorQuad(minimum + inset, maximum - inset);
            var center = minimum + new Vector2(tileSize / 2f);
            if ((doorData & ((int) AtmosDirection.North | (int) AtmosDirection.South)) != 0)
            {
                builder.AddDoorLine(
                    new Vector2(minimum.X + tileSize * 0.2f, center.Y),
                    new Vector2(maximum.X - tileSize * 0.2f, center.Y));
            }
            if ((doorData & ((int) AtmosDirection.East | (int) AtmosDirection.West)) != 0)
            {
                builder.AddDoorLine(
                    new Vector2(center.X, minimum.Y + tileSize * 0.2f),
                    new Vector2(center.X, maximum.Y - tileSize * 0.2f));
            }
        }
    }

    private static void AddWallEdge(
        NavMapComponent navMap,
        ArchitectureChunkBuilder builder,
        Vector2i tile,
        Vector2 minimum,
        float tileSize,
        int wallData,
        AtmosDirection direction,
        AtmosDirection opposite,
        Vector2i neighborOffset)
    {
        var directionMask = (int) direction;
        if ((wallData & directionMask) == 0 ||
            (GetCategory(navMap, tile + neighborOffset, NavMapChunkType.Wall) & (int) opposite) != 0)
        {
            return;
        }

        var maximum = minimum + new Vector2(tileSize);
        switch (direction)
        {
            case AtmosDirection.North:
                builder.AddWallLine(new Vector2(minimum.X, maximum.Y), maximum);
                break;
            case AtmosDirection.South:
                builder.AddWallLine(minimum, new Vector2(maximum.X, minimum.Y));
                break;
            case AtmosDirection.East:
                builder.AddWallLine(new Vector2(maximum.X, minimum.Y), maximum);
                break;
            case AtmosDirection.West:
                builder.AddWallLine(minimum, new Vector2(minimum.X, maximum.Y));
                break;
        }
    }

    private static bool HasCategory(NavMapChunk chunk, int x, int y, int mask)
    {
        var index = SharedNavMapSystem.GetTileIndex(new Vector2i(x, y));
        return (chunk.TileData[index] & mask) != 0;
    }

    private static int GetCategory(NavMapComponent navMap, Vector2i tile, NavMapChunkType type)
    {
        var chunkX = (int) MathF.Floor(tile.X / (float) SharedNavMapSystem.ChunkSize);
        var chunkY = (int) MathF.Floor(tile.Y / (float) SharedNavMapSystem.ChunkSize);
        var chunkIndex = new Vector2i(chunkX, chunkY);
        if (!navMap.Chunks.TryGetValue(chunkIndex, out var chunk))
            return 0;

        var relative = tile - chunkIndex * SharedNavMapSystem.ChunkSize;
        var index = SharedNavMapSystem.GetTileIndex(relative);
        var mask = type switch
        {
            NavMapChunkType.Wall => SharedNavMapSystem.WallMask,
            NavMapChunkType.Airlock => SharedNavMapSystem.AirlockMask,
            _ => SharedNavMapSystem.FloorMask,
        };
        return (chunk.TileData[index] & mask) >> (int) type;
    }

    private void DrawArchitectureFloors(DrawingHandleScreen handle, MapLod lod)
    {
        var color = lod == MapLod.City ? FloorCityColor : FloorColor;
        foreach (var chunk in _architecture)
        {
            if (!IsArchitectureVisible(chunk))
                continue;

            DrawArchitectureTriangles(handle, chunk.FloorTriangles, chunk.ScreenFloorTriangles, color);
        }
    }

    private void DrawArchitectureDetails(DrawingHandleScreen handle, MapLod lod)
    {
        if (lod == MapLod.City)
            return;

        foreach (var chunk in _architecture)
        {
            if (!IsArchitectureVisible(chunk))
                continue;

            DrawArchitectureTriangles(handle, chunk.WallTriangles, chunk.ScreenWallTriangles, WallFillColor);
            DrawArchitectureLines(handle, chunk.WallLines, chunk.ScreenWallLines, WallLineColor);
            DrawArchitectureTriangles(handle, chunk.DoorTriangles, chunk.ScreenDoorTriangles, DoorFillColor);
            DrawArchitectureLines(handle, chunk.DoorLines, chunk.ScreenDoorLines, DoorLineColor);
        }
    }

    private void DrawArchitectureTriangles(
        DrawingHandleScreen handle,
        Vector2[] mapVertices,
        Vector2[] screenVertices,
        Color color)
    {
        for (var i = 0; i < mapVertices.Length; i++)
            screenVertices[i] = MapToScreen(mapVertices[i]);
        if (screenVertices.Length > 0)
            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, screenVertices.AsSpan(), color);
    }

    private void DrawArchitectureLines(
        DrawingHandleScreen handle,
        Vector2[] mapVertices,
        Vector2[] screenVertices,
        Color color)
    {
        for (var i = 0; i < mapVertices.Length; i++)
            screenVertices[i] = MapToScreen(mapVertices[i]);
        if (screenVertices.Length > 0)
            handle.DrawPrimitives(DrawPrimitiveTopology.LineList, screenVertices.AsSpan(), color);
    }

    private bool IsArchitectureVisible(ArchitectureChunk chunk)
    {
        return IsMapBoundsVisible(chunk.BoundsMin, chunk.BoundsMax);
    }

    private void DrawFeature(DrawingHandleScreen handle, MapFeature feature, MapLod lod)
    {
        var (fill, line) = GetFeatureColors(feature.Kind, lod);

        if (feature.FillTriangles.Length > 0)
        {
            for (var i = 0; i < feature.FillTriangles.Length; i++)
                feature.ScreenTriangles[i] = MapToScreen(feature.FillTriangles[i]);
            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, feature.ScreenTriangles.AsSpan(), fill);
        }

        if (feature.Vertices.Length >= 2)
        {
            for (var i = 0; i < feature.Vertices.Length; i++)
            {
                var next = (i + 1) % feature.Vertices.Length;
                handle.DrawLine(MapToScreen(feature.Vertices[i]), MapToScreen(feature.Vertices[next]), line);
            }
        }

        foreach (var run in feature.TileRuns)
        {
            var first = MapToScreen(run.Origin);
            var last = MapToScreen(run.Origin + new Vector2(run.Length, 1f));
            var box = new UIBox2(
                MathF.Min(first.X, last.X),
                MathF.Min(first.Y, last.Y),
                MathF.Max(first.X, last.X),
                MathF.Max(first.Y, last.Y));
            handle.DrawRect(box, fill);
            if (_mapScale >= 4f)
                handle.DrawRect(box, line, filled: false);
        }
    }

    private void DrawLabels(DrawingHandleScreen handle, MapLod lod)
    {
        _drawnLabels.Clear();
        _labelRects.Clear();

        foreach (var feature in _features)
        {
            if (!ShouldLabel(feature.Kind, lod) ||
                !IsFeatureVisible(feature) ||
                !_drawnLabels.Add(feature.Name))
            {
                continue;
            }

            var position = MapToScreen(feature.LabelPosition);
            if (!IsInCanvas(position))
                continue;

            var font = lod == MapLod.City ? _font : _smallFont;
            var label = feature.LabelText;
            var dimensions = handle.GetDimensions(font, label, 1f);
            var rect = new UIBox2(
                position.X - dimensions.X / 2f - 4f,
                position.Y - dimensions.Y / 2f - 2f,
                position.X + dimensions.X / 2f + 4f,
                position.Y + dimensions.Y / 2f + 2f);
            if (OverlapsExistingLabel(rect))
                continue;

            _labelRects.Add(rect);
            handle.DrawRect(rect, CanvasColor.WithAlpha(0.78f));
            handle.DrawString(font, position - dimensions / 2f, label, GetLabelColor(feature.Kind));
        }
    }

    private void DrawUserMarker(DrawingHandleScreen handle)
    {
        var marker = MapToScreen(_userPosition);
        if (!IsInCanvas(marker))
            return;

        var pulse = (float) (_timing.RealTime.TotalSeconds % 1.6d / 1.6d);
        handle.DrawCircle(marker, 7f + pulse * 18f, SignalColor.WithAlpha((1f - pulse) * 0.12f));
        handle.DrawCircle(marker, 5f, SignalColor.WithAlpha(0.34f));
        handle.DrawCircle(marker, 2.5f, SignalColor);
        handle.DrawLine(marker - new Vector2(13f, 0f), marker - new Vector2(7f, 0f), SignalColor.WithAlpha(0.7f));
        handle.DrawLine(marker + new Vector2(7f, 0f), marker + new Vector2(13f, 0f), SignalColor.WithAlpha(0.7f));
        handle.DrawLine(marker - new Vector2(0f, 13f), marker - new Vector2(0f, 7f), SignalColor.WithAlpha(0.7f));
        handle.DrawLine(marker + new Vector2(0f, 7f), marker + new Vector2(0f, 13f), SignalColor.WithAlpha(0.7f));
    }

    private void DrawSignalOverlay(DrawingHandleScreen handle)
    {
        var scanPhase = (float) (_timing.RealTime.TotalSeconds % 3.2d / 3.2d);
        var scanY = CanvasTop + scanPhase * MathF.Max(0f, PixelHeight - CanvasTop - CanvasBottom);
        handle.DrawRect(new UIBox2(0f, scanY - 2f, PixelWidth, scanY + 2f), ScanColor);
        DrawCornerBrackets(handle);
    }

    private void DrawCornerBrackets(DrawingHandleScreen handle)
    {
        const float inset = 6f;
        const float length = 20f;
        var top = CanvasTop + inset;
        var right = PixelWidth - inset;
        var bottom = PixelHeight - CanvasBottom - inset;

        handle.DrawLine(new Vector2(inset, top), new Vector2(inset + length, top), BorderColor);
        handle.DrawLine(new Vector2(inset, top), new Vector2(inset, top + length), BorderColor);
        handle.DrawLine(new Vector2(right, top), new Vector2(right - length, top), BorderColor);
        handle.DrawLine(new Vector2(right, top), new Vector2(right, top + length), BorderColor);
        handle.DrawLine(new Vector2(inset, bottom), new Vector2(inset + length, bottom), BorderColor);
        handle.DrawLine(new Vector2(inset, bottom), new Vector2(inset, bottom - length), BorderColor);
        handle.DrawLine(new Vector2(right, bottom), new Vector2(right - length, bottom), BorderColor);
        handle.DrawLine(new Vector2(right, bottom), new Vector2(right, bottom - length), BorderColor);
    }

    private bool IsFeatureVisible(MapFeature feature)
    {
        return IsMapBoundsVisible(feature.BoundsMin, feature.BoundsMax);
    }

    private bool IsMapBoundsVisible(Vector2 boundsMin, Vector2 boundsMax)
    {
        var first = MapToScreen(boundsMin);
        var second = MapToScreen(boundsMax);
        var left = MathF.Min(first.X, second.X);
        var right = MathF.Max(first.X, second.X);
        var top = MathF.Min(first.Y, second.Y);
        var bottom = MathF.Max(first.Y, second.Y);
        return right >= 0f && left <= PixelWidth && bottom >= CanvasTop && top <= PixelHeight - CanvasBottom;
    }

    private bool IsInCanvas(Vector2 point)
    {
        return point.X >= 0f && point.X <= PixelWidth && point.Y >= CanvasTop && point.Y <= PixelHeight - CanvasBottom;
    }

    private bool OverlapsExistingLabel(UIBox2 candidate)
    {
        foreach (var existing in _labelRects)
        {
            if (candidate.Left <= existing.Right && candidate.Right >= existing.Left &&
                candidate.Top <= existing.Bottom && candidate.Bottom >= existing.Top)
            {
                return true;
            }
        }
        return false;
    }

    private MapLod GetLod()
    {
        if (_zoom < 0.72f)
            return MapLod.City;
        return _zoom < 2.5f ? MapLod.Streets : MapLod.Buildings;
    }

    private static bool ShouldDraw(NCAgentMapFeatureKind kind, MapLod lod)
    {
        return lod switch
        {
            MapLod.City => kind is NCAgentMapFeatureKind.District or NCAgentMapFeatureKind.Sector,
            MapLod.Streets => true,
            MapLod.Buildings => kind is NCAgentMapFeatureKind.Street or NCAgentMapFeatureKind.Building,
            _ => false,
        };
    }

    private static bool ShouldLabel(NCAgentMapFeatureKind kind, MapLod lod)
    {
        return lod switch
        {
            MapLod.City => kind is NCAgentMapFeatureKind.District or NCAgentMapFeatureKind.Sector,
            MapLod.Streets => kind is NCAgentMapFeatureKind.Street or NCAgentMapFeatureKind.Building,
            MapLod.Buildings => kind == NCAgentMapFeatureKind.Building,
            _ => false,
        };
    }

    private (Color Fill, Color Line) GetFeatureColors(NCAgentMapFeatureKind kind, MapLod lod)
    {
        var alphaScale = lod == MapLod.Buildings ? 0.7f : 1f;
        if (_architectureAvailable)
            alphaScale *= 0.36f;

        return kind switch
        {
            NCAgentMapFeatureKind.District => (DistrictFill.WithAlpha(DistrictFill.A * alphaScale), DistrictLine),
            NCAgentMapFeatureKind.Sector => (SectorFill.WithAlpha(SectorFill.A * alphaScale), SectorLine),
            NCAgentMapFeatureKind.Street => (StreetFill.WithAlpha(StreetFill.A * alphaScale), StreetLine),
            _ => (BuildingFill.WithAlpha(BuildingFill.A * alphaScale), BuildingLine),
        };
    }

    private static Color GetLabelColor(NCAgentMapFeatureKind kind)
    {
        return kind switch
        {
            NCAgentMapFeatureKind.District => Color.FromHex("#EE9BFF"),
            NCAgentMapFeatureKind.Sector => Color.FromHex("#8FB8FF"),
            NCAgentMapFeatureKind.Street => Color.FromHex("#FFE28D"),
            _ => Color.FromHex("#A3FFF7"),
        };
    }

    private static float PositiveModulo(float value, float divisor)
    {
        var result = value % divisor;
        return result < 0f ? result + divisor : result;
    }

    private enum MapLod : byte
    {
        City,
        Streets,
        Buildings,
    }

    /// <summary>
    /// Immutable per-NavMap-chunk geometry permits cheap viewport culling before vertex transformation.
    /// </summary>
    private sealed class ArchitectureChunk
    {
        public readonly Vector2[] FloorTriangles;
        public readonly Vector2[] ScreenFloorTriangles;
        public readonly Vector2[] WallTriangles;
        public readonly Vector2[] ScreenWallTriangles;
        public readonly Vector2[] DoorTriangles;
        public readonly Vector2[] ScreenDoorTriangles;
        public readonly Vector2[] WallLines;
        public readonly Vector2[] ScreenWallLines;
        public readonly Vector2[] DoorLines;
        public readonly Vector2[] ScreenDoorLines;
        public readonly Vector2 BoundsMin;
        public readonly Vector2 BoundsMax;

        public ArchitectureChunk(ArchitectureChunkBuilder builder)
        {
            FloorTriangles = builder.FloorTriangles.ToArray();
            ScreenFloorTriangles = new Vector2[FloorTriangles.Length];
            WallTriangles = builder.WallTriangles.ToArray();
            ScreenWallTriangles = new Vector2[WallTriangles.Length];
            DoorTriangles = builder.DoorTriangles.ToArray();
            ScreenDoorTriangles = new Vector2[DoorTriangles.Length];
            WallLines = builder.WallLines.ToArray();
            ScreenWallLines = new Vector2[WallLines.Length];
            DoorLines = builder.DoorLines.ToArray();
            ScreenDoorLines = new Vector2[DoorLines.Length];
            BoundsMin = builder.BoundsMin;
            BoundsMax = builder.BoundsMax;
        }
    }

    /// <summary>
    /// Converts grid-local NavMap primitives to stable map space once while a chunk cache is rebuilt.
    /// </summary>
    private sealed class ArchitectureChunkBuilder(Matrix3x2 worldMatrix)
    {
        public readonly List<Vector2> FloorTriangles = [];
        public readonly List<Vector2> WallTriangles = [];
        public readonly List<Vector2> DoorTriangles = [];
        public readonly List<Vector2> WallLines = [];
        public readonly List<Vector2> DoorLines = [];

        public Vector2 BoundsMin { get; private set; }
        public Vector2 BoundsMax { get; private set; }
        public bool HasGeometry { get; private set; }

        public void AddFloorQuad(Vector2 minimum, Vector2 maximum)
        {
            AddQuad(FloorTriangles, minimum, maximum);
        }

        public void AddWallQuad(Vector2 minimum, Vector2 maximum)
        {
            AddQuad(WallTriangles, minimum, maximum);
        }

        public void AddDoorQuad(Vector2 minimum, Vector2 maximum)
        {
            AddQuad(DoorTriangles, minimum, maximum);
        }

        public void AddWallLine(Vector2 start, Vector2 end)
        {
            AddLine(WallLines, start, end);
        }

        public void AddDoorLine(Vector2 start, Vector2 end)
        {
            AddLine(DoorLines, start, end);
        }

        private void AddQuad(List<Vector2> destination, Vector2 minimum, Vector2 maximum)
        {
            var first = ToMap(minimum);
            var second = ToMap(new Vector2(maximum.X, minimum.Y));
            var third = ToMap(maximum);
            var fourth = ToMap(new Vector2(minimum.X, maximum.Y));
            destination.Add(first);
            destination.Add(second);
            destination.Add(third);
            destination.Add(first);
            destination.Add(third);
            destination.Add(fourth);
            Include(first);
            Include(second);
            Include(third);
            Include(fourth);
        }

        private void AddLine(List<Vector2> destination, Vector2 start, Vector2 end)
        {
            var mapStart = ToMap(start);
            var mapEnd = ToMap(end);
            destination.Add(mapStart);
            destination.Add(mapEnd);
            Include(mapStart);
            Include(mapEnd);
        }

        private Vector2 ToMap(Vector2 local)
        {
            return Vector2.Transform(local, worldMatrix);
        }

        private void Include(Vector2 point)
        {
            if (!HasGeometry)
            {
                BoundsMin = point;
                BoundsMax = point;
                HasGeometry = true;
                return;
            }

            BoundsMin = Vector2.Min(BoundsMin, point);
            BoundsMax = Vector2.Max(BoundsMax, point);
        }
    }

    private readonly record struct TileRun(Vector2 Origin, int Length);

    private sealed class MapFeature
    {
        public readonly NCAgentMapFeatureKind Kind;
        public readonly string Name;
        public readonly string LabelText;
        public readonly Vector2[] Vertices;
        public readonly Vector2[] FillTriangles;
        public readonly Vector2[] ScreenTriangles;
        public readonly List<TileRun> TileRuns = [];
        public readonly Vector2 BoundsMin;
        public readonly Vector2 BoundsMax;
        public readonly Vector2 LabelPosition;
        public readonly bool HasGeometry;

        public MapFeature(NCAgentMapFeature source)
        {
            Kind = source.Kind;
            Name = source.Name;
            LabelText = source.Name.ToUpperInvariant();

            if (source.GeometryKind == NCAgentMapGeometryKind.Polygon)
            {
                Vertices = SanitizeVertices(source.Vertices);
                FillTriangles = Triangulate(Vertices);
                ScreenTriangles = new Vector2[FillTriangles.Length];
                HasGeometry = Vertices.Length >= 3;
                if (HasGeometry)
                {
                    GetBounds(Vertices, out BoundsMin, out BoundsMax);
                    LabelPosition = (BoundsMin + BoundsMax) / 2f;
                    return;
                }
            }
            else
            {
                Vertices = [];
                FillTriangles = [];
                ScreenTriangles = [];
                BuildTileRuns(source.Chunks, TileRuns);
                HasGeometry = TileRuns.Count > 0;
                if (HasGeometry)
                {
                    GetRunBounds(TileRuns, out BoundsMin, out BoundsMax);
                    LabelPosition = (BoundsMin + BoundsMax) / 2f;
                    return;
                }
            }

            Vertices = [];
            FillTriangles = [];
            ScreenTriangles = [];
            BoundsMin = default;
            BoundsMax = default;
            LabelPosition = default;
        }

        private static Vector2[] SanitizeVertices(List<Vector2> source)
        {
            if (source.Count < 3)
                return [];

            var count = source.Count;
            if (Vector2.DistanceSquared(source[0], source[^1]) < 0.0001f)
                count--;
            if (count < 3)
                return [];

            var result = new Vector2[count];
            for (var i = 0; i < count; i++)
                result[i] = source[i];
            return result;
        }

        private static void BuildTileRuns(List<NCAgentMapTileChunk> chunks, List<TileRun> result)
        {
            foreach (var chunk in chunks)
            {
                var rowCount = Math.Min(32, chunk.Rows.Count);
                for (var y = 0; y < rowCount; y++)
                {
                    var row = chunk.Rows[y];
                    var x = 0;
                    while (x < 32)
                    {
                        while (x < 32 && (row & (1UL << x)) == 0)
                            x++;
                        if (x >= 32)
                            break;

                        var start = x;
                        while (x < 32 && (row & (1UL << x)) != 0)
                            x++;
                        result.Add(new TileRun(new Vector2(chunk.OriginX + start, chunk.OriginY + y), x - start));
                    }
                }
            }
        }

        private static void GetBounds(IReadOnlyList<Vector2> vertices, out Vector2 minimum, out Vector2 maximum)
        {
            minimum = vertices[0];
            maximum = vertices[0];
            for (var i = 1; i < vertices.Count; i++)
            {
                minimum = Vector2.Min(minimum, vertices[i]);
                maximum = Vector2.Max(maximum, vertices[i]);
            }
        }

        private static void GetRunBounds(IReadOnlyList<TileRun> runs, out Vector2 minimum, out Vector2 maximum)
        {
            minimum = runs[0].Origin;
            maximum = runs[0].Origin + new Vector2(runs[0].Length, 1f);
            for (var i = 1; i < runs.Count; i++)
            {
                minimum = Vector2.Min(minimum, runs[i].Origin);
                maximum = Vector2.Max(maximum, runs[i].Origin + new Vector2(runs[i].Length, 1f));
            }
        }

        /// <summary>
        /// Ear clipping supports mapper-authored concave simple polygons without assuming rectangles.
        /// Invalid polygons remain outlined instead of producing incorrect fill triangles.
        /// </summary>
        private static Vector2[] Triangulate(IReadOnlyList<Vector2> vertices)
        {
            if (vertices.Count < 3)
                return [];

            var indices = Enumerable.Range(0, vertices.Count).ToList();
            if (SignedArea(vertices) < 0f)
                indices.Reverse();

            var triangles = new List<Vector2>((vertices.Count - 2) * 3);
            var safety = vertices.Count * vertices.Count;
            while (indices.Count > 3 && safety-- > 0)
            {
                var clipped = false;
                for (var i = 0; i < indices.Count; i++)
                {
                    var previous = indices[(i - 1 + indices.Count) % indices.Count];
                    var current = indices[i];
                    var next = indices[(i + 1) % indices.Count];
                    var a = vertices[previous];
                    var b = vertices[current];
                    var c = vertices[next];
                    if (Cross(b - a, c - a) <= 0.00001f || ContainsVertex(vertices, indices, previous, current, next, a, b, c))
                        continue;

                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(c);
                    indices.RemoveAt(i);
                    clipped = true;
                    break;
                }

                if (!clipped)
                    return [];
            }

            if (indices.Count == 3)
            {
                triangles.Add(vertices[indices[0]]);
                triangles.Add(vertices[indices[1]]);
                triangles.Add(vertices[indices[2]]);
            }
            return triangles.ToArray();
        }

        private static bool ContainsVertex(
            IReadOnlyList<Vector2> vertices,
            IReadOnlyList<int> indices,
            int previous,
            int current,
            int next,
            Vector2 a,
            Vector2 b,
            Vector2 c)
        {
            foreach (var index in indices)
            {
                if (index == previous || index == current || index == next)
                    continue;
                if (PointInTriangle(vertices[index], a, b, c))
                    return true;
            }
            return false;
        }

        private static bool PointInTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
        {
            var ab = Cross(b - a, point - a);
            var bc = Cross(c - b, point - b);
            var ca = Cross(a - c, point - c);
            return ab >= -0.00001f && bc >= -0.00001f && ca >= -0.00001f;
        }

        private static float SignedArea(IReadOnlyList<Vector2> vertices)
        {
            var area = 0f;
            for (var i = 0; i < vertices.Count; i++)
            {
                var next = (i + 1) % vertices.Count;
                area += vertices[i].X * vertices[next].Y - vertices[next].X * vertices[i].Y;
            }
            return area / 2f;
        }

        private static float Cross(Vector2 left, Vector2 right)
        {
            return left.X * right.Y - left.Y * right.X;
        }
    }
}
