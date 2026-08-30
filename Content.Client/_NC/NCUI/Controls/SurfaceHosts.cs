// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Client._NC.NCUI.Core;
using Content.Client._NC.NCUI.Input;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;

namespace Content.Client._NC.NCUI.Controls;

/// <summary>
/// Hosts one real Robust tooltip control over a logical surface node.
/// </summary>
public sealed class RichTooltipHost : LayoutContainer
{
    private Control? _tooltip;

    public void Show(Control tooltip, Vector2 position)
    {
        Hide();
        _tooltip = tooltip;
        AddChild(tooltip);
        SetPosition(tooltip, position);
        tooltip.Visible = true;
    }

    public void Hide()
    {
        if (_tooltip == null)
            return;
        RemoveChild(_tooltip);
        _tooltip = null;
    }
}

public sealed record NcUiContextAction(string LocalizedLabel, Action Callback, bool Enabled = true);

/// <summary>
/// Builds a small real-control context menu; visible labels are supplied already localized by the presenter.
/// </summary>
public sealed class ContextActionHost : BoxContainer
{
    public ContextActionHost()
    {
        Orientation = LayoutOrientation.Vertical;
        Visible = false;
    }

    public void Show(IEnumerable<NcUiContextAction> actions)
    {
        RemoveAllChildren();
        foreach (var action in actions)
        {
            var button = new Button
            {
                Text = action.LocalizedLabel,
                Disabled = !action.Enabled,
            };
            button.OnPressed += _ => action.Callback();
            AddChild(button);
        }
        Visible = true;
    }

    public void Hide()
    {
        Visible = false;
        RemoveAllChildren();
    }
}

/// <summary>
/// Presenter-owned inspector host that rebuilds only when semantic selection changes.
/// </summary>
public sealed class SelectionInspector : BoxContainer
{
    public SelectionInspector()
    {
        Orientation = LayoutOrientation.Vertical;
    }

    public void SetContent(Control? content)
    {
        RemoveAllChildren();
        if (content != null)
            AddChild(content);
    }
}

public sealed class DragGhostLayer : LayoutContainer
{
    private Control? _ghost;

    public void Begin(Control ghost, Vector2 position)
    {
        Cancel();
        _ghost = ghost;
        ghost.MouseFilter = MouseFilterMode.Ignore;
        AddChild(ghost);
        Move(position);
    }

    public void Move(Vector2 position)
    {
        if (_ghost != null)
            SetPosition(_ghost, position);
    }

    public void Cancel()
    {
        if (_ghost == null)
            return;
        RemoveChild(_ghost);
        _ghost = null;
    }
}

/// <summary>
/// Places a real LineEdit over a lightweight text node so focus, IME and keyboard behavior stay native to Robust UI.
/// </summary>
public sealed class SurfaceTextEditorHost : LayoutContainer
{
    private readonly LineEdit _editor;
    private InteractiveSurfaceControl? _surface;
    private UiNodeId? _node;
    private Action<string>? _commit;
    private Action? _cancel;
    private bool _finishing;

    public bool Active => _node != null;

    public SurfaceTextEditorHost()
    {
        MouseFilter = MouseFilterMode.Ignore;
        _editor = new LineEdit
        {
            Visible = false,
            SelectAllOnFocus = true,
            MouseFilter = MouseFilterMode.Stop,
        };
        _editor.OnTextEntered += args => Finish(true, args.Text);
        _editor.OnFocusExit += args => Finish(true, args.Text);
        AddChild(_editor);
    }

    public void Begin(
        InteractiveSurfaceControl surface,
        UiNodeId node,
        string initialText,
        Action<string> commit,
        Action? cancel = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(commit);
        if (!surface.Document.Nodes.Contains(node))
            throw new ArgumentException("The NCUI text node does not exist.", nameof(node));
        Cancel();
        _surface = surface;
        _node = node;
        _commit = commit;
        _cancel = cancel;
        _editor.Text = initialText;
        _editor.Visible = true;
        UpdatePlacement();
        _editor.GrabKeyboardFocus();
    }

    public void Commit() => Finish(true, _editor.Text);

    public void Cancel() => Finish(false, _editor.Text);

    protected override void FrameUpdate(Robust.Shared.Timing.FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (Active)
            UpdatePlacement();
    }

    private void UpdatePlacement()
    {
        if (_surface == null || _node is not { } node || !_surface.Document.TryGetNode(node, out var surfaceNode))
        {
            Cancel();
            return;
        }
        var box = _surface.Viewport.SurfaceToPixel(surfaceNode.Bounds);
        var scale = Math.Max(0.001f, UIScale);
        _editor.MinSize = new Vector2(Math.Max(48f, box.Width / scale), Math.Max(24f, box.Height / scale));
        SetPosition(_editor, box.TopLeft / scale);
    }

    private void Finish(bool commit, string text)
    {
        if (_finishing || _node == null)
            return;
        _finishing = true;
        var commitCallback = _commit;
        var cancelCallback = _cancel;
        _surface = null;
        _node = null;
        _commit = null;
        _cancel = null;
        _editor.Visible = false;
        _editor.ReleaseKeyboardFocus();
        if (commit)
            commitCallback?.Invoke(text);
        else
            cancelCallback?.Invoke();
        _finishing = false;
    }
}

/// <summary>
/// Lightweight overview of node bounds and the current camera without duplicating scene controls.
/// </summary>
public sealed class SurfaceMinimap : Control
{
    private uint _boundsVersion = uint.MaxValue;
    private UIBox2 _documentBounds;
    public InteractiveSurfaceControl? Surface { get; set; }
    public Color NodeColor { get; set; } = Color.FromHex("#4FDDD0").WithAlpha(0.45f);
    public Color ViewportColor { get; set; } = Color.FromHex("#FFFFFF").WithAlpha(0.8f);

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        if (Surface == null || Surface.Document.Nodes.Count == 0)
            return;
        if (_boundsVersion != Surface.Document.RenderOrderVersion)
        {
            _documentBounds = GetDocumentBounds(Surface);
            _boundsVersion = Surface.Document.RenderOrderVersion;
        }
        var bounds = _documentBounds;
        if (bounds.Width <= 0f || bounds.Height <= 0f)
            return;
        var scale = Math.Min(PixelWidth / bounds.Width, PixelHeight / bounds.Height);
        var offset = (new Vector2(PixelWidth, PixelHeight) - bounds.Size * scale) * 0.5f - bounds.TopLeft * scale;
        foreach (var node in Surface.Document.Nodes.Span)
        {
            if ((node.Flags & UiNodeFlags.Visible) == 0)
                continue;
            handle.DrawRect(new UIBox2(node.Bounds.TopLeft * scale + offset,
                node.Bounds.BottomRight * scale + offset), NodeColor);
        }
        var viewport = Surface.Viewport.ViewportBounds;
        handle.DrawRect(new UIBox2(viewport.TopLeft * scale + offset,
            viewport.BottomRight * scale + offset), ViewportColor, false);
    }

    private static UIBox2 GetDocumentBounds(InteractiveSurfaceControl surface)
    {
        var first = surface.Document.Nodes.GetAt(0).Bounds;
        var min = first.TopLeft;
        var max = first.BottomRight;
        for (var i = 1; i < surface.Document.Nodes.Count; i++)
        {
            var bounds = surface.Document.Nodes.GetAt(i).Bounds;
            min = Vector2.Min(min, bounds.TopLeft);
            max = Vector2.Max(max, bounds.BottomRight);
        }
        return new UIBox2(min, max);
    }
}
