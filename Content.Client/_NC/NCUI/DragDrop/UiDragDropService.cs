// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Client._NC.NCUI.Core;

namespace Content.Client._NC.NCUI.DragDrop;

/// <summary>
/// One typed drag-and-drop transaction with deterministic enter/leave previews and server-pending presentation.
/// </summary>
public sealed class UiDragDropService
{
    private readonly UiSurfaceDocument _document;
    private readonly Dictionary<UiNodeId, IUiDropTarget> _targets = [];

    public UiNodeId? Source { get; private set; }
    public UiNodeId? HoveredTarget { get; private set; }
    public IUiDragPayload? Payload { get; private set; }
    public UiDropPreview? Preview { get; private set; }
    public bool Active => Source != null && Payload != null;

    public event Action<UiNodeId, IUiDragPayload>? DragBegan;
    public event Action<UiNodeId?, UiDropPreview?>? PreviewChanged;
    public event Action<UiNodeId, UiNodeId, IUiDragPayload, UiDropPreview>? Dropped;
    public event Action? Cancelled;

    public UiDragDropService(UiSurfaceDocument document)
    {
        _document = document;
        _document.NodeRemoved += OnNodeRemoved;
        _document.Cleared += Cancel;
    }

    public void RegisterTarget(UiNodeId node, IUiDropTarget target)
    {
        if (!_document.Nodes.TryGet(node, out var surfaceNode) ||
            (surfaceNode.Flags & UiNodeFlags.DropTarget) == 0)
        {
            throw new ArgumentException("A drop target must reference a node marked DropTarget.", nameof(node));
        }
        _targets[node] = target;
    }

    public void UnregisterTarget(UiNodeId node) => _targets.Remove(node);

    public bool Begin(UiNodeId source, IUiDragPayload payload)
    {
        if (Active || !_document.Nodes.TryGet(source, out var node) ||
            (node.Flags & UiNodeFlags.Draggable) == 0)
        {
            return false;
        }
        Source = source;
        Payload = payload;
        _document.SetVisualState(source, UiNodeVisualState.Dragging);
        DragBegan?.Invoke(source, payload);
        return true;
    }

    public void Move(UiNodeId? target, Vector2 surfacePosition)
    {
        if (Source is not { } source || Payload == null)
            return;
        var candidate = target is { } id && _targets.ContainsKey(id) ? id : (UiNodeId?)null;
        if (candidate == HoveredTarget && candidate == null)
            return;

        RestoreTarget();
        HoveredTarget = candidate;
        Preview = null;
        if (candidate is { } targetId && _targets.TryGetValue(targetId, out var dropTarget))
        {
            var context = new UiDragContext(source, targetId, surfacePosition);
            Preview = dropTarget.CanAccept(context, Payload);
            _document.SetVisualState(targetId, IsAccepted(Preview.Value)
                ? UiNodeVisualState.ValidTarget
                : UiNodeVisualState.InvalidTarget);
        }
        PreviewChanged?.Invoke(HoveredTarget, Preview);
    }

    public bool Drop(Vector2 surfacePosition)
    {
        if (Source is not { } source || HoveredTarget is not { } target ||
            Payload == null || Preview is not { } preview || !_targets.TryGetValue(target, out var dropTarget) ||
            !IsAccepted(preview))
        {
            Cancel();
            return false;
        }

        var payload = Payload;
        var context = new UiDragContext(source, target, surfacePosition);
        dropTarget.Drop(context, payload);
        Dropped?.Invoke(source, target, payload, preview);
        Finish(UiNodeVisualState.PendingServer);
        return true;
    }

    public void ResolvePending(UiNodeId source, bool accepted)
    {
        if (_document.Nodes.TryGet(source, out var node) && node.VisualState == UiNodeVisualState.PendingServer)
            _document.SetVisualState(source, accepted ? UiNodeVisualState.Normal : UiNodeVisualState.Error);
    }

    public void Cancel()
    {
        if (!Active)
            return;
        Finish(UiNodeVisualState.Normal);
        Cancelled?.Invoke();
    }

    private void Finish(UiNodeVisualState sourceState)
    {
        RestoreTarget();
        if (Source is { } source && _document.Nodes.Contains(source))
            _document.SetVisualState(source, sourceState);
        Source = null;
        HoveredTarget = null;
        Payload = null;
        Preview = null;
    }

    private void RestoreTarget()
    {
        if (HoveredTarget is { } target && _document.Nodes.TryGet(target, out var node) &&
            node.VisualState is UiNodeVisualState.ValidTarget or UiNodeVisualState.InvalidTarget)
        {
            _document.SetVisualState(target, UiNodeVisualState.Normal);
        }
    }

    private void OnNodeRemoved(UiNodeId id)
    {
        _targets.Remove(id);
        if (Source == id || HoveredTarget == id)
            Cancel();
    }

    private static bool IsAccepted(UiDropPreview preview) =>
        preview is UiDropPreview.Accepted or UiDropPreview.AcceptedWithWarning or
            UiDropPreview.RejectedServerCheckRequired;
}
