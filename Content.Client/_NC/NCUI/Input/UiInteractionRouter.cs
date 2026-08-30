// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics;
using System.Numerics;
using Content.Client._NC.NCUI.Core;

namespace Content.Client._NC.NCUI.Input;

/// <summary>
/// Performs spatial hit-testing, event bubbling, hover transitions and logical pointer capture.
/// </summary>
public sealed class UiInteractionRouter
{
    private const float DragThresholdPixels = 4f;
    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan LongPressDelay = TimeSpan.FromMilliseconds(550);

    private readonly UiSurfaceDocument _document;
    private readonly UiViewportTransform _viewport;
    private readonly Dictionary<UiNodeId, IUiNodeInteractionHandler> _handlers = [];
    private UiNodeId[] _hitBuffer = new UiNodeId[64];
    private TimeSpan _pressedAt;
    private TimeSpan _lastClickAt;
    private UiNodeId? _lastClicked;
    private bool _longPressSent;

    public UiPointerCapture Capture { get; } = new();
    public double LastHitTestMilliseconds { get; private set; }
    public int LastHitCandidates { get; private set; }

    public event Action<UiNodeId, UiPointerEventType, bool>? EventDispatched;
    public event Action<UiNodeId?>? HoverChanged;
    public event Action<UiNodeId?>? FocusChanged;
    public event Action<UiNodeId>? DragStarted;
    public event Action<UiNodeId, Vector2>? DragMoved;
    public event Action<UiNodeId, UiNodeId?>? DragFinished;

    public UiInteractionRouter(UiSurfaceDocument document, UiViewportTransform viewport)
    {
        _document = document;
        _viewport = viewport;
        _document.NodeRemoved += OnNodeRemoved;
        _document.Cleared += Cancel;
    }

    public void RegisterHandler(UiNodeId node, IUiNodeInteractionHandler handler)
    {
        if (!_document.Nodes.Contains(node))
            throw new ArgumentException($"Unknown NCUI node {node}.", nameof(node));
        _handlers[node] = handler;
    }

    public void UnregisterHandler(UiNodeId node) => _handlers.Remove(node);

    public UiNodeId? HitTest(Vector2 surfacePoint)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            LastHitCandidates = _document.SpatialIndex.QueryPoint(surfacePoint, _hitBuffer, out var truncated);
            if (!truncated)
                break;
            Array.Resize(ref _hitBuffer, _hitBuffer.Length * 2);
        }

        UiNodeId? best = null;
        var bestZ = int.MinValue;
        for (var i = 0; i < LastHitCandidates; i++)
        {
            var id = _hitBuffer[i];
            if (!_document.Nodes.TryGet(id, out var node) ||
                (node.Flags & (UiNodeFlags.Visible | UiNodeFlags.Enabled | UiNodeFlags.HitTest)) !=
                (UiNodeFlags.Visible | UiNodeFlags.Enabled | UiNodeFlags.HitTest) ||
                !_document.HitShapes.Contains(node.HitShape, node.Bounds, TransformHitPoint(node, surfacePoint)))
            {
                continue;
            }

            if (node.ZIndex < bestZ || node.ZIndex == bestZ && best is { } current && id.Value < current.Value)
                continue;
            best = id;
            bestZ = node.ZIndex;
        }

        LastHitTestMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return best;
    }

    public void PointerDown(Vector2 pixelPosition, TimeSpan now, Robust.Shared.Input.BoundKeyFunction function)
    {
        var surface = _viewport.PixelToSurface(pixelPosition);
        var target = HitTest(surface);
        Capture.ActivePixelPosition = pixelPosition;
        Capture.PressPixelPosition = pixelPosition;
        Capture.PressedNode = target;
        Capture.CapturedNode = null;
        Capture.DragSourceNode = null;
        Capture.Dragging = false;
        _pressedAt = now;
        _longPressSent = false;

        SetFocus(target);
        if (target is not { } id)
            return;
        SetTransientState(id, UiNodeVisualState.Pressed);
        var args = Create(UiPointerEventType.PointerDown, id, pixelPosition, Vector2.Zero, function);
        Dispatch(id, ref args);
        if (args.CapturePointer || _document.Nodes.TryGet(id, out var node) &&
            (node.Flags & UiNodeFlags.Draggable) != 0)
        {
            Capture.CapturedNode = id;
        }
    }

    public void PointerMove(Vector2 pixelPosition, Vector2 pixelDelta, TimeSpan now)
    {
        Capture.ActivePixelPosition = pixelPosition;
        var surface = _viewport.PixelToSurface(pixelPosition);
        var actualTarget = HitTest(surface);
        UpdateHover(actualTarget, pixelPosition);
        var routedTarget = Capture.CapturedNode ?? actualTarget;
        if (routedTarget is { } routed)
        {
            var args = Create(UiPointerEventType.PointerMove, routed, pixelPosition, pixelDelta, default);
            Dispatch(routed, ref args);
        }

        if (Capture.PressedNode is not { } pressed || Capture.Dragging && Capture.DragSourceNode == null)
            return;
        if (!Capture.Dragging &&
            Vector2.DistanceSquared(pixelPosition, Capture.PressPixelPosition) >=
            DragThresholdPixels * DragThresholdPixels &&
            _document.Nodes.TryGet(pressed, out var node) && (node.Flags & UiNodeFlags.Draggable) != 0)
        {
            Capture.Dragging = true;
            Capture.DragSourceNode = pressed;
            Capture.CapturedNode = pressed;
            SetTransientState(pressed, UiNodeVisualState.Dragging);
            var begin = Create(UiPointerEventType.DragBegin, pressed, pixelPosition, pixelDelta, default);
            Dispatch(pressed, ref begin);
            DragStarted?.Invoke(pressed);
        }

        if (!Capture.Dragging || Capture.DragSourceNode is not { } source)
            return;
        var move = Create(UiPointerEventType.DragMove, source, pixelPosition, pixelDelta, default);
        Dispatch(source, ref move);
        DragMoved?.Invoke(source, surface);
    }

    public void PointerUp(Vector2 pixelPosition, TimeSpan now, Robust.Shared.Input.BoundKeyFunction function)
    {
        var surface = _viewport.PixelToSurface(pixelPosition);
        var actualTarget = HitTest(surface);
        var routedTarget = Capture.CapturedNode ?? actualTarget;
        if (routedTarget is { } routed)
        {
            var up = Create(UiPointerEventType.PointerUp, routed, pixelPosition, Vector2.Zero, function);
            Dispatch(routed, ref up);
        }

        if (Capture.Dragging && Capture.DragSourceNode is { } source)
        {
            DragFinished?.Invoke(source, actualTarget);
        }
        else if (Capture.PressedNode is { } pressed && actualTarget == pressed)
        {
            var type = _lastClicked == pressed && now - _lastClickAt <= DoubleClickWindow
                ? UiPointerEventType.DoubleClick
                : UiPointerEventType.Click;
            var click = Create(type, pressed, pixelPosition, Vector2.Zero, function);
            Dispatch(pressed, ref click);
            _lastClicked = pressed;
            _lastClickAt = now;
        }

        RestoreVisual(Capture.PressedNode);
        RestoreVisual(Capture.DragSourceNode);
        Capture.PressedNode = null;
        Capture.CapturedNode = null;
        Capture.DragSourceNode = null;
        Capture.Dragging = false;
        _longPressSent = false;
    }

    public bool Wheel(Vector2 pixelPosition, float delta)
    {
        var target = HitTest(_viewport.PixelToSurface(pixelPosition));
        if (target is not { } id)
            return false;
        var args = Create(UiPointerEventType.Wheel, id, pixelPosition, Vector2.Zero, default);
        args.WheelDelta = delta;
        Dispatch(id, ref args);
        return args.Handled;
    }

    public bool ContextAction(Vector2 pixelPosition, Robust.Shared.Input.BoundKeyFunction function)
    {
        var target = HitTest(_viewport.PixelToSurface(pixelPosition));
        if (target is not { } id)
            return false;
        var args = Create(UiPointerEventType.ContextAction, id, pixelPosition, Vector2.Zero, function);
        Dispatch(id, ref args);
        return true;
    }

    public bool KeyboardAction(Robust.Shared.Input.BoundKeyFunction function)
    {
        if (Capture.FocusedNode is not { } focused)
            return false;
        var args = Create(UiPointerEventType.KeyboardAction, focused,
            Capture.ActivePixelPosition, Vector2.Zero, function);
        Dispatch(focused, ref args);
        return args.Handled;
    }

    public bool TextCommit(string text)
    {
        if (Capture.FocusedNode is not { } focused)
            return false;
        var args = Create(UiPointerEventType.TextCommit, focused,
            Capture.ActivePixelPosition, Vector2.Zero, default);
        args.Text = text;
        Dispatch(focused, ref args);
        return args.Handled;
    }

    public void RouteToNode(UiPointerEventType type, UiNodeId node, Vector2 pixelPosition)
    {
        if (!_document.Nodes.Contains(node))
            return;
        var args = Create(type, node, pixelPosition, Vector2.Zero, default);
        Dispatch(node, ref args);
    }

    public void Update(TimeSpan now)
    {
        if (_longPressSent || Capture.Dragging || Capture.PressedNode is not { } pressed ||
            now - _pressedAt < LongPressDelay)
            return;
        _longPressSent = true;
        var args = Create(UiPointerEventType.LongPress, pressed, Capture.ActivePixelPosition, Vector2.Zero, default);
        Dispatch(pressed, ref args);
    }

    public void Cancel()
    {
        if (Capture.DragSourceNode is { } source)
        {
            var args = Create(UiPointerEventType.DragCancel, source, Capture.ActivePixelPosition, Vector2.Zero, default);
            Dispatch(source, ref args);
        }
        RestoreVisual(Capture.PressedNode);
        RestoreVisual(Capture.DragSourceNode);
        Capture.Clear();
    }

    private UiPointerEvent Create(
        UiPointerEventType type,
        UiNodeId target,
        Vector2 pixel,
        Vector2 delta,
        Robust.Shared.Input.BoundKeyFunction function)
    {
        return new UiPointerEvent
        {
            Type = type,
            Target = target,
            PixelPosition = pixel,
            SurfacePosition = _viewport.PixelToSurface(pixel),
            PixelDelta = delta,
            Function = function,
        };
    }

    private void Dispatch(UiNodeId target, ref UiPointerEvent args)
    {
        var current = target;
        while (current.IsValid && _document.Nodes.TryGet(current, out var node))
        {
            args.CurrentTarget = current;
            if (_handlers.TryGetValue(current, out var handler))
                handler.Handle(_document, current, ref args);
            EventDispatched?.Invoke(current, args.Type, args.Handled);
            if (args.CapturePointer)
                Capture.CapturedNode = current;
            if (args.ReleasePointer && Capture.CapturedNode == current)
                Capture.CapturedNode = null;
            if (args.Handled || node.Parent is not { } parent)
                break;
            current = parent;
        }
    }

    private void UpdateHover(UiNodeId? target, Vector2 pixelPosition)
    {
        if (Capture.HoveredNode == target)
            return;
        if (Capture.HoveredNode is { } previous)
        {
            var leave = Create(UiPointerEventType.PointerLeave, previous, pixelPosition, Vector2.Zero, default);
            Dispatch(previous, ref leave);
            RestoreVisual(previous);
        }
        Capture.HoveredNode = target;
        if (target is { } next)
        {
            var enter = Create(UiPointerEventType.PointerEnter, next, pixelPosition, Vector2.Zero, default);
            Dispatch(next, ref enter);
            SetTransientState(next, UiNodeVisualState.Hovered);
        }
        HoverChanged?.Invoke(target);
    }

    private void SetFocus(UiNodeId? target)
    {
        UiNodeId? focus = null;
        if (target is { } id && _document.Nodes.TryGet(id, out var node) &&
            (node.Flags & UiNodeFlags.Focusable) != 0)
        {
            focus = id;
        }
        if (Capture.FocusedNode == focus)
            return;
        Capture.FocusedNode = focus;
        FocusChanged?.Invoke(focus);
    }

    private void SetTransientState(UiNodeId id, UiNodeVisualState state)
    {
        if (!_document.Nodes.TryGet(id, out var node) ||
            node.VisualState is UiNodeVisualState.Selected or UiNodeVisualState.PendingServer or
                UiNodeVisualState.Error or UiNodeVisualState.Warning)
        {
            return;
        }
        _document.SetVisualState(id, state);
    }

    private void RestoreVisual(UiNodeId? id)
    {
        if (id is not { } nodeId || !_document.Nodes.TryGet(nodeId, out var node))
            return;
        if (node.VisualState is UiNodeVisualState.Hovered or UiNodeVisualState.Pressed or UiNodeVisualState.Dragging)
            _document.SetVisualState(nodeId, Capture.HoveredNode == nodeId
                ? UiNodeVisualState.Hovered
                : UiNodeVisualState.Normal);
    }

    private void OnNodeRemoved(UiNodeId id)
    {
        _handlers.Remove(id);
        if (Capture.HoveredNode == id)
            Capture.HoveredNode = null;
        if (Capture.FocusedNode == id)
            Capture.FocusedNode = null;
        if (Capture.CapturedNode == id || Capture.PressedNode == id || Capture.DragSourceNode == id)
            Cancel();
    }

    private static Vector2 TransformHitPoint(in UiSurfaceNode node, Vector2 point)
    {
        var center = node.Bounds.Center;
        if (!MathHelper.CloseTo(node.Rotation, 0f))
        {
            var local = point - center;
            var sine = MathF.Sin(-node.Rotation);
            var cosine = MathF.Cos(-node.Rotation);
            point = center + new Vector2(
                local.X * cosine - local.Y * sine,
                local.X * sine + local.Y * cosine);
        }
        if (!MathHelper.CloseTo(node.Scale, 1f) && node.Scale > 0f)
            point = center + (point - center) / node.Scale;
        return point;
    }
}
