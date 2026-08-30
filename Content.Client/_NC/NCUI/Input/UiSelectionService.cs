// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client._NC.NCUI.Core;
using Robust.Shared.Maths;

namespace Content.Client._NC.NCUI.Input;

/// <summary>
/// Stores selection by stable node ID, independent of pooled controls or current viewport visibility.
/// </summary>
public sealed class UiSelectionService
{
    private readonly UiSurfaceDocument _document;
    private readonly HashSet<UiNodeId> _selected = [];
    private UiNodeId[] _queryBuffer = new UiNodeId[256];

    public int Count => _selected.Count;
    public IReadOnlyCollection<UiNodeId> Selected => _selected;

    public event Action? SelectionChanged;

    public UiSelectionService(UiSurfaceDocument document)
    {
        _document = document;
        _document.NodeRemoved += OnNodeRemoved;
        _document.Cleared += Clear;
    }

    public bool Contains(UiNodeId id) => _selected.Contains(id);

    public void SelectOnly(UiNodeId id)
    {
        if (_selected.Count == 1 && _selected.Contains(id))
            return;
        ClearInternal();
        AddInternal(id);
        SelectionChanged?.Invoke();
    }

    public bool Add(UiNodeId id)
    {
        if (!AddInternal(id))
            return false;
        SelectionChanged?.Invoke();
        return true;
    }

    public bool Remove(UiNodeId id)
    {
        if (!_selected.Remove(id))
            return false;
        RestoreState(id);
        SelectionChanged?.Invoke();
        return true;
    }

    public void Toggle(UiNodeId id)
    {
        if (!Remove(id))
            Add(id);
    }

    public void SelectArea(UIBox2 area, bool additive = false)
    {
        if (!additive)
            ClearInternal();
        while (true)
        {
            var count = _document.SpatialIndex.QueryArea(area, _queryBuffer, out var truncated);
            for (var i = 0; i < count; i++)
            {
                var id = _queryBuffer[i];
                if (_document.Nodes.TryGet(id, out var node) &&
                    (node.Flags & UiNodeFlags.Selectable) != 0 &&
                    ContainsBox(area, node.Bounds))
                {
                    AddInternal(id);
                }
            }
            if (!truncated)
                break;
            Array.Resize(ref _queryBuffer, _queryBuffer.Length * 2);
        }
        SelectionChanged?.Invoke();
    }

    public void Clear()
    {
        if (_selected.Count == 0)
            return;
        ClearInternal();
        SelectionChanged?.Invoke();
    }

    private bool AddInternal(UiNodeId id)
    {
        if (!_document.Nodes.TryGet(id, out var node) ||
            (node.Flags & UiNodeFlags.Selectable) == 0 ||
            !_selected.Add(id))
        {
            return false;
        }
        _document.SetVisualState(id, UiNodeVisualState.Selected);
        return true;
    }

    private void ClearInternal()
    {
        foreach (var id in _selected)
            RestoreState(id);
        _selected.Clear();
    }

    private void RestoreState(UiNodeId id)
    {
        if (_document.Nodes.TryGet(id, out var node) && node.VisualState == UiNodeVisualState.Selected)
            _document.SetVisualState(id, UiNodeVisualState.Normal);
    }

    private void OnNodeRemoved(UiNodeId id)
    {
        if (_selected.Remove(id))
            SelectionChanged?.Invoke();
    }

    private static bool ContainsBox(UIBox2 outer, UIBox2 inner) =>
        outer.Left <= inner.Left && outer.Top <= inner.Top &&
        outer.Right >= inner.Right && outer.Bottom >= inner.Bottom;
}

