// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client._NC.NCUI.Core;
using Robust.Shared.Maths;

namespace Content.Client._NC.NCUI.Rendering;

/// <summary>
/// Reused visible-node buffer. It grows only when a viewport exceeds its previous high-water mark.
/// </summary>
public sealed class UiRenderQueue
{
    private UiNodeId[] _visible = new UiNodeId[1024];
    private readonly NodeComparer _comparer;
    private uint _lastRenderOrderVersion = uint.MaxValue;
    private UIBox2 _lastViewport;
    private bool _hasViewport;

    public int Count { get; private set; }
    public long RebuildCount { get; private set; }
    public ReadOnlySpan<UiNodeId> Span => _visible.AsSpan(0, Count);

    public UiRenderQueue(UiNodeStore store)
    {
        _comparer = new NodeComparer(store);
    }

    public void Build(UiSurfaceDocument document, UIBox2 viewport)
    {
        if (_hasViewport &&
            _lastRenderOrderVersion == document.RenderOrderVersion &&
            SameBounds(_lastViewport, viewport))
        {
            return;
        }

        while (true)
        {
            Count = document.SpatialIndex.QueryArea(viewport, _visible, out var truncated);
            if (!truncated)
                break;
            Array.Resize(ref _visible, _visible.Length * 2);
        }

        var write = 0;
        for (var i = 0; i < Count; i++)
        {
            if (!document.Nodes.TryGet(_visible[i], out var node) ||
                (node.Flags & UiNodeFlags.Visible) == 0 ||
                !Intersects(node.Bounds, viewport))
            {
                continue;
            }
            _visible[write++] = node.Id;
        }

        Count = write;
        Array.Sort(_visible, 0, Count, _comparer);
        RebuildCount++;
        _lastRenderOrderVersion = document.RenderOrderVersion;
        _lastViewport = viewport;
        _hasViewport = true;
    }

    private static bool SameBounds(UIBox2 first, UIBox2 second) =>
        first.Left == second.Left && first.Top == second.Top &&
        first.Right == second.Right && first.Bottom == second.Bottom;

    private static bool Intersects(UIBox2 first, UIBox2 second) =>
        first.Left <= second.Right && first.Right >= second.Left &&
        first.Top <= second.Bottom && first.Bottom >= second.Top;

    private sealed class NodeComparer(UiNodeStore store) : IComparer<UiNodeId>
    {
        public int Compare(UiNodeId first, UiNodeId second)
        {
            if (!store.TryGet(first, out var a) || !store.TryGet(second, out var b))
                return first.Value.CompareTo(second.Value);
            var z = a.ZIndex.CompareTo(b.ZIndex);
            return z != 0 ? z : first.Value.CompareTo(second.Value);
        }
    }
}
