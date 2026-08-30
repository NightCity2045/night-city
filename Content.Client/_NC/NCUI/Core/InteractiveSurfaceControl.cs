// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Client._NC.NCUI.Animation;
using Content.Client._NC.NCUI.Debug;
using Content.Client._NC.NCUI.DragDrop;
using Content.Client._NC.NCUI.Input;
using Content.Client._NC.NCUI.Rendering;
using Content.Client._NC.NCUI.Spatial;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.Client._NC.NCUI.Core;

/// <summary>
/// A single Robust control hosting an indexed, culled and independently-routed lightweight UI scene.
/// </summary>
[Virtual]
public partial class InteractiveSurfaceControl : Control
{
    [Dependency] private IGameTiming _timing = default!;

    private bool _panning;
    private bool _areaSelecting;
    private Vector2 _lastPointerPixel;
    private Vector2 _selectionStartSurface;
    private Vector2 _dragOffsetSurface;
    private UiNodeId? _lastDropTarget;
    private long _allocationBaseline;
    private uint _lastNodeCountVersion = uint.MaxValue;
    private float _networkSampleElapsed;
    private long _lastNetworkBytesIn;
    private long _lastNetworkBytesOut;

    public UiSurfaceDocument Document { get; }
    public UiViewportTransform Viewport { get; } = new();
    public UiInteractionRouter Interaction { get; }
    public UiSelectionService Selection { get; }
    public UiDragDropService DragDrop { get; }
    public UiToolController Tools { get; }
    public UiAnimationScheduler Animations { get; }
    public UiSurfaceRenderer Renderer { get; }
    public UiSurfacePalette Palette { get; set; } = UiSurfacePalette.NightCity;
    public NcUiDebugSettings DebugSettings { get; } = new();
    public NcUiMetrics Metrics { get; } = new();
    public NcUiEventTrace EventTrace { get; } = new();

    /// <summary>
    /// Creates the domain-owned typed payload when a draggable node begins moving.
    /// </summary>
    public Func<UiNodeId, IUiDragPayload?>? DragPayloadFactory { get; set; }

    public float WheelZoomFactor { get; set; } = 1.15f;
    public bool MoveDraggedNodes { get; set; } = true;
    public event Action<UiNodeId>? PrimaryAction;
    public event Action<UiNodeId>? ContextAction;
    public event Action<UIBox2>? SelectionAreaChanged;
    public event Action? SelectionBegan;
    public event Action? SelectionChanged;
    public event Action? SelectionEnded;
    public event Action<Vector2>? Panned;
    public event Action<float>? Zoomed;
    public event Action<UiPointerEventType, Vector2>? SurfaceEvent;

    public InteractiveSurfaceControl(float spatialCellSize = 64f)
    {
        IoCManager.InjectDependencies(this);
        MouseFilter = MouseFilterMode.Stop;
        RectClipContent = true;
        CanKeyboardFocus = true;

        Document = new UiSurfaceDocument(new UniformGridSpatialIndex(spatialCellSize));
        Interaction = new UiInteractionRouter(Document, Viewport);
        Selection = new UiSelectionService(Document);
        DragDrop = new UiDragDropService(Document);
        Tools = new UiToolController(new UiToolContext(Document, Viewport, Selection));
        Animations = new UiAnimationScheduler(Document);
        Renderer = new UiSurfaceRenderer(Document.Nodes);
        NcUiDebugRuntime.Register(this);

        Interaction.DragStarted += OnDragStarted;
        Interaction.DragMoved += OnDragMoved;
        Interaction.DragFinished += OnDragFinished;
        Interaction.EventDispatched += OnEventDispatched;
        DragDrop.PreviewChanged += OnDropPreviewChanged;
        DragDrop.Dropped += OnDropped;
        Selection.SelectionChanged += OnSelectionChanged;
    }

    public void CenterOn(UIBox2 bounds)
    {
        Viewport.SetCenter(bounds.Center);
        var scaleX = PixelWidth / Math.Max(1f, bounds.Width);
        var scaleY = PixelHeight / Math.Max(1f, bounds.Height);
        Viewport.Zoom = Math.Min(scaleX, scaleY) / Math.Max(0.001f, UIScale);
    }

    public void CancelInteraction()
    {
        if (_areaSelecting)
        {
            SelectionEnded?.Invoke();
            SurfaceEvent?.Invoke(UiPointerEventType.SelectionEnd,
                Viewport.PixelToSurface(Interaction.Capture.ActivePixelPosition));
        }
        _panning = false;
        _areaSelecting = false;
        Interaction.Cancel();
        DragDrop.Cancel();
        Tools.Cancel();
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        ConfigureViewport();
        Renderer.Draw(handle, Document, Viewport, Palette);
        DrawDiagnostics(handle);
        Metrics.SurfaceNodes = Document.Nodes.Count;
        Metrics.ActualControls = ChildCount + 1;
        Metrics.VisibleNodes = Renderer.Queue.Count;
        Metrics.DrawCommands = Renderer.LastDrawCommands;
        Metrics.DrawMilliseconds = Renderer.LastDrawMilliseconds;
        Metrics.HitTestMilliseconds = Interaction.LastHitTestMilliseconds;
        Metrics.SpatialBucketsQueried = Document.SpatialIndex.LastBucketsQueried;
        Metrics.AnimationsActive = Animations.ActiveCount;
        if (_lastNodeCountVersion != Document.Version)
        {
            var interactive = 0;
            foreach (var node in Document.Nodes.Span)
            {
                if ((node.Flags & UiNodeFlags.HitTest) != 0)
                    interactive++;
            }
            Metrics.InteractiveNodes = interactive;
            _lastNodeCountVersion = Document.Version;
        }
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (DebugSettings.Allocations)
            _allocationBaseline = GC.GetAllocatedBytesForCurrentThread();
        Animations.Update(args.DeltaSeconds);
        Interaction.Update(_timing.CurTime);
        _networkSampleElapsed += args.DeltaSeconds;
        if (_networkSampleElapsed >= 1f)
        {
            Metrics.NetworkBytesInPerSecond = (Metrics.NetworkBytesIn - _lastNetworkBytesIn) / _networkSampleElapsed;
            Metrics.NetworkBytesOutPerSecond = (Metrics.NetworkBytesOut - _lastNetworkBytesOut) / _networkSampleElapsed;
            _lastNetworkBytesIn = Metrics.NetworkBytesIn;
            _lastNetworkBytesOut = Metrics.NetworkBytesOut;
            _networkSampleElapsed = 0f;
        }
        Metrics.SurfaceUpdateMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (DebugSettings.Allocations)
            Metrics.AllocatedBytesPerFrame = GC.GetAllocatedBytesForCurrentThread() - _allocationBaseline;
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);
        if (args.Handled)
            return;
        ConfigureViewport();
        _lastPointerPixel = args.RelativePixelPosition;

        if (args.Function == EngineKeyFunctions.UIRightClick)
        {
            if (!Interaction.ContextAction(args.RelativePixelPosition, args.Function))
                _panning = true;
            args.Handle();
            return;
        }
        if (args.Function == EngineKeyFunctions.CloseModals)
        {
            CancelInteraction();
            args.Handle();
            return;
        }
        if (args.Function != EngineKeyFunctions.UIClick)
        {
            if (Interaction.KeyboardAction(args.Function))
                args.Handle();
            return;
        }

        var toolResult = Tools.PointerDown(CreateToolPointer(UiPointerEventType.PointerDown,
            args.RelativePixelPosition, Vector2.Zero, args.Function));
        if (toolResult == UiToolResult.Cancel)
            Tools.Cancel();
        if (toolResult != UiToolResult.Ignored)
        {
            args.Handle();
            return;
        }
        Interaction.PointerDown(args.RelativePixelPosition, _timing.CurTime, args.Function);
        if (Interaction.Capture.PressedNode == null)
        {
            _areaSelecting = true;
            _selectionStartSurface = Viewport.PixelToSurface(args.RelativePixelPosition);
            SelectionBegan?.Invoke();
            SurfaceEvent?.Invoke(UiPointerEventType.SelectionBegin, _selectionStartSurface);
        }
        args.Handle();
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);
        if (args.Function == EngineKeyFunctions.UIRightClick && _panning)
        {
            _panning = false;
            args.Handle();
            return;
        }
        if (args.Function != EngineKeyFunctions.UIClick)
            return;

        var toolResult = Tools.PointerUp(CreateToolPointer(UiPointerEventType.PointerUp,
            args.RelativePixelPosition, Vector2.Zero, args.Function));
        if (toolResult == UiToolResult.Complete)
            Tools.Deactivate();
        if (toolResult == UiToolResult.Cancel)
            Tools.Cancel();
        if (toolResult == UiToolResult.Ignored)
            Interaction.PointerUp(args.RelativePixelPosition, _timing.CurTime, args.Function);
        if (_areaSelecting)
        {
            _areaSelecting = false;
            var area = NormalizeBox(_selectionStartSurface, Viewport.PixelToSurface(args.RelativePixelPosition));
            Selection.SelectArea(area);
            SelectionAreaChanged?.Invoke(area);
            SelectionEnded?.Invoke();
            SurfaceEvent?.Invoke(UiPointerEventType.SelectionEnd, area.BottomRight);
        }
        args.Handle();
    }

    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);
        ConfigureViewport();
        var pixel = args.RelativePixelPosition;
        var delta = pixel - _lastPointerPixel;
        _lastPointerPixel = pixel;
        if (_panning)
        {
            Viewport.PanPixels(delta);
            Panned?.Invoke(Viewport.Center);
            SurfaceEvent?.Invoke(UiPointerEventType.Pan, Viewport.Center);
            args.Handle();
            return;
        }
        if (_areaSelecting)
        {
            var area = NormalizeBox(_selectionStartSurface, Viewport.PixelToSurface(pixel));
            Selection.SelectArea(area);
            SelectionAreaChanged?.Invoke(area);
            args.Handle();
            return;
        }

        var toolResult = Tools.PointerMove(CreateToolPointer(UiPointerEventType.PointerMove,
            pixel, delta, default));
        if (toolResult == UiToolResult.Ignored)
            Interaction.PointerMove(pixel, delta, _timing.CurTime);
        args.Handle();
    }

    protected override void MouseWheel(GUIMouseWheelEventArgs args)
    {
        base.MouseWheel(args);
        ConfigureViewport();
        if (Interaction.Wheel(args.RelativePixelPosition, args.Delta.Y))
        {
            args.Handle();
            return;
        }
        var factor = args.Delta.Y > 0f ? WheelZoomFactor : 1f / WheelZoomFactor;
        Viewport.ZoomAt(args.RelativePixelPosition, factor);
        Zoomed?.Invoke(Viewport.Zoom);
        SurfaceEvent?.Invoke(UiPointerEventType.Zoom, Viewport.PixelToSurface(args.RelativePixelPosition));
        args.Handle();
    }

    protected override void MouseExited()
    {
        base.MouseExited();
        if (!_panning && !Interaction.Capture.Dragging)
            Interaction.PointerMove(new Vector2(-1_000_000f), Vector2.Zero, _timing.CurTime);
    }

    protected override void TextEntered(GUITextEnteredEventArgs args)
    {
        base.TextEntered(args);
        Interaction.TextCommit(args.Text);
    }

    protected override void Resized()
    {
        base.Resized();
        ConfigureViewport();
    }

    protected override void UIScaleChanged()
    {
        base.UIScaleChanged();
        ConfigureViewport();
    }

    private void ConfigureViewport() => Viewport.Configure(PixelSize, UIScale);

    private UiPointerEvent CreateToolPointer(
        UiPointerEventType type,
        Vector2 pixel,
        Vector2 delta,
        BoundKeyFunction function)
    {
        return new UiPointerEvent
        {
            Type = type,
            PixelPosition = pixel,
            SurfacePosition = Viewport.PixelToSurface(pixel),
            PixelDelta = delta,
            Function = function,
        };
    }

    private void OnDragStarted(UiNodeId source)
    {
        if (Document.TryGetNode(source, out var node))
            _dragOffsetSurface = node.Bounds.TopLeft - Viewport.PixelToSurface(Interaction.Capture.ActivePixelPosition);
        var payload = DragPayloadFactory?.Invoke(source);
        if (payload != null)
            DragDrop.Begin(source, payload);
    }

    private void OnDragMoved(UiNodeId source, Vector2 surfacePosition)
    {
        if (MoveDraggedNodes)
            MoveNodeDuringDrag(source, surfacePosition + _dragOffsetSurface);
        if (!DragDrop.Active)
            return;
        DragDrop.Move(Interaction.HitTest(surfacePosition), surfacePosition);
    }

    protected virtual void MoveNodeDuringDrag(UiNodeId source, Vector2 topLeft)
    {
        if (Document.TryGetNode(source, out var node))
            Document.SetBounds(source, UIBox2.FromDimensions(topLeft, node.Bounds.Size));
    }

    private void OnDragFinished(UiNodeId source, UiNodeId? target)
    {
        if (!DragDrop.Active)
            return;
        DragDrop.Move(target, Viewport.PixelToSurface(Interaction.Capture.ActivePixelPosition));
        DragDrop.Drop(Viewport.PixelToSurface(Interaction.Capture.ActivePixelPosition));
    }

    private void OnEventDispatched(UiNodeId node, UiPointerEventType type, bool handled)
    {
        if (DebugSettings.Events)
            EventTrace.Record(new NcUiEventTraceEntry(_timing.CurTime, node.Value, type, handled));
        if (type == UiPointerEventType.Click && Document.Nodes.TryGet(node, out var surfaceNode))
        {
            if ((surfaceNode.Flags & UiNodeFlags.Selectable) != 0)
                Selection.SelectOnly(node);
            if (surfaceNode.PrimaryAction.IsValid)
                PrimaryAction?.Invoke(node);
        }
        else if (type == UiPointerEventType.ContextAction)
        {
            ContextAction?.Invoke(node);
        }
    }

    private void OnDropPreviewChanged(UiNodeId? target, UiDropPreview? preview)
    {
        if (_lastDropTarget == target)
            return;
        if (_lastDropTarget is { } previous)
            Interaction.RouteToNode(UiPointerEventType.DragLeave, previous, Interaction.Capture.ActivePixelPosition);
        _lastDropTarget = target;
        if (target is { } next)
            Interaction.RouteToNode(UiPointerEventType.DragEnter, next, Interaction.Capture.ActivePixelPosition);
    }

    private void OnDropped(UiNodeId source, UiNodeId target, IUiDragPayload payload, UiDropPreview preview)
    {
        Interaction.RouteToNode(UiPointerEventType.Drop, target, Interaction.Capture.ActivePixelPosition);
        _lastDropTarget = null;
    }

    private void OnSelectionChanged()
    {
        SelectionChanged?.Invoke();
        SurfaceEvent?.Invoke(UiPointerEventType.SelectionChanged,
            Viewport.PixelToSurface(Interaction.Capture.ActivePixelPosition));
    }

    private static UIBox2 NormalizeBox(Vector2 first, Vector2 second) =>
        new(Vector2.Min(first, second), Vector2.Max(first, second));

    private void DrawDiagnostics(DrawingHandleScreen handle)
    {
        if (!DebugSettings.Bounds &&
            !DebugSettings.Hitboxes &&
            !DebugSettings.Spatial &&
            !DebugSettings.Events &&
            !DebugSettings.Layout)
        {
            if (_areaSelecting)
                handle.DrawRect(Viewport.SurfaceToPixel(NormalizeBox(_selectionStartSurface,
                    Viewport.PixelToSurface(_lastPointerPixel))), Color.Cyan.WithAlpha(0.5f), false);
            return;
        }
        if (DebugSettings.Layout)
        {
            var bounds = UIBox2.FromDimensions(Vector2.Zero, PixelSize);
            var center = PixelSize * 0.5f;
            handle.DrawRect(bounds, Color.DeepSkyBlue.WithAlpha(0.7f), false);
            handle.DrawLine(center - new Vector2(8f, 0f), center + new Vector2(8f, 0f), Color.DeepSkyBlue);
            handle.DrawLine(center - new Vector2(0f, 8f), center + new Vector2(0f, 8f), Color.DeepSkyBlue);
        }
        if (DebugSettings.Bounds || DebugSettings.Hitboxes)
        {
            foreach (var node in Document.Nodes.Span)
            {
                if ((node.Flags & UiNodeFlags.Visible) == 0 || !Viewport.ViewportBounds.Intersects(node.Bounds))
                    continue;
                var color = DebugSettings.Hitboxes && (node.Flags & UiNodeFlags.HitTest) != 0
                    ? Color.Lime.WithAlpha(0.65f)
                    : Color.Cyan.WithAlpha(0.5f);
                handle.DrawRect(Viewport.SurfaceToPixel(node.Bounds), color, false);
            }
        }
        if (DebugSettings.Spatial && Document.SpatialIndex is UniformGridSpatialIndex grid)
        {
            foreach (var (x, y, _) in grid.EnumerateBuckets())
            {
                var topLeft = new Vector2(x * grid.CellSize, y * grid.CellSize);
                handle.DrawRect(Viewport.SurfaceToPixel(UIBox2.FromDimensions(topLeft,
                    new Vector2(grid.CellSize))), Color.Yellow.WithAlpha(0.25f), false);
            }
        }
        if (DebugSettings.Events &&
            Interaction.Capture.HoveredNode is { } hovered &&
            Document.TryGetNode(hovered, out var hoveredNode))
        {
            // The event mode makes the current routing target visible without adding a real Control per node.
            handle.DrawRect(Viewport.SurfaceToPixel(hoveredNode.Bounds), Color.Magenta.WithAlpha(0.9f), false);
        }
        if (_areaSelecting)
            handle.DrawRect(Viewport.SurfaceToPixel(NormalizeBox(_selectionStartSurface,
                Viewport.PixelToSurface(_lastPointerPixel))), Color.Cyan.WithAlpha(0.75f), false);
    }
}
