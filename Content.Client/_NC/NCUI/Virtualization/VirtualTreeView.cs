// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Client._NC.NCUI.Virtualization;

/// <summary>
/// Stable-key expansion model that flattens only visible tree branches for VirtualTreeControl.
/// </summary>
public sealed class VirtualTreeView<T, TKey> where TKey : notnull
{
    private readonly List<T> _roots = [];
    private readonly List<Entry> _visible = [];
    private readonly HashSet<TKey> _expanded = [];
    private readonly HashSet<TKey> _knownKeys = [];
    private readonly Stack<(T Item, int Depth)> _stack = [];
    private readonly Func<T, TKey> _keySelector;
    private readonly Func<T, IReadOnlyList<T>> _childrenSelector;

    public int Count => _visible.Count;
    public IReadOnlySet<TKey> ExpandedKeys => _expanded;
    public int Revision { get; private set; }

    public event Action? Changed;

    public VirtualTreeView(Func<T, TKey> keySelector, Func<T, IReadOnlyList<T>> childrenSelector)
    {
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        _childrenSelector = childrenSelector ?? throw new ArgumentNullException(nameof(childrenSelector));
    }

    public T this[int index] => _visible[index].Item;
    public TKey KeyAt(int index) => _keySelector(_visible[index].Item);
    public int DepthAt(int index) => _visible[index].Depth;
    public bool HasChildrenAt(int index) => _childrenSelector(_visible[index].Item).Count != 0;
    public bool IsExpanded(TKey key) => _expanded.Contains(key);

    public void Reset(IEnumerable<T> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots.Clear();
        _roots.AddRange(roots);
        Rebuild();
    }

    public bool Toggle(TKey key) => _expanded.Contains(key) ? Collapse(key) : Expand(key);

    public bool Expand(TKey key)
    {
        if (!_knownKeys.Contains(key) || !_expanded.Add(key))
            return false;
        Rebuild();
        return true;
    }

    public bool Collapse(TKey key)
    {
        if (!_expanded.Remove(key))
            return false;
        Rebuild();
        return true;
    }

    public void CollapseAll()
    {
        if (_expanded.Count == 0)
            return;
        _expanded.Clear();
        Rebuild();
    }

    public void ExpandAll()
    {
        DiscoverAllKeys(expandBranches: true);
        Rebuild();
    }

    private void Rebuild()
    {
        _visible.Clear();
        _knownKeys.Clear();
        _stack.Clear();
        for (var i = _roots.Count - 1; i >= 0; i--)
            _stack.Push((_roots[i], 0));

        while (_stack.TryPop(out var current))
        {
            var key = _keySelector(current.Item);
            if (!_knownKeys.Add(key))
                throw new InvalidOperationException($"Duplicate NCUI tree key '{key}'.");
            _visible.Add(new Entry(current.Item, current.Depth));
            if (!_expanded.Contains(key))
                continue;

            var children = _childrenSelector(current.Item);
            for (var i = children.Count - 1; i >= 0; i--)
                _stack.Push((children[i], current.Depth + 1));
        }

        _expanded.RemoveWhere(key => !_knownKeys.Contains(key));
        Revision++;
        Changed?.Invoke();
    }

    private void DiscoverAllKeys(bool expandBranches)
    {
        _knownKeys.Clear();
        _stack.Clear();
        for (var i = _roots.Count - 1; i >= 0; i--)
            _stack.Push((_roots[i], 0));
        while (_stack.TryPop(out var current))
        {
            var key = _keySelector(current.Item);
            if (!_knownKeys.Add(key))
                throw new InvalidOperationException($"Duplicate NCUI tree key '{key}'.");
            var children = _childrenSelector(current.Item);
            if (expandBranches && children.Count != 0)
                _expanded.Add(key);
            for (var i = children.Count - 1; i >= 0; i--)
                _stack.Push((children[i], current.Depth + 1));
        }
    }

    private readonly record struct Entry(T Item, int Depth);
}
