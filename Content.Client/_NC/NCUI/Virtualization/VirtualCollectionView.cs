// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Client._NC.NCUI.Virtualization;

/// <summary>
/// Typed data view that owns filtering, sorting and stable-key selection independently of recycled controls.
/// </summary>
public sealed class VirtualCollectionView<T, TKey> where TKey : notnull
{
    private readonly List<T> _source = [];
    private readonly List<int> _view = [];
    private readonly HashSet<TKey> _selection = [];
    private readonly Func<T, TKey> _keySelector;
    private Predicate<T>? _filter;
    private Comparison<T>? _comparison;

    public int Count => _view.Count;
    public int SourceCount => _source.Count;
    public IReadOnlySet<TKey> Selection => _selection;
    public int Revision { get; private set; }
    public event Action? Changed;
    public event Action? SelectionChanged;

    public VirtualCollectionView(Func<T, TKey> keySelector)
    {
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
    }

    public T this[int viewIndex] => _source[_view[viewIndex]];
    public TKey KeyAt(int viewIndex) => _keySelector(this[viewIndex]);

    public void Reset(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _source.Clear();
        _source.AddRange(items);
        RemoveMissingSelection();
        Rebuild();
    }

    public void Add(T item)
    {
        _source.Add(item);
        Rebuild();
    }

    public bool Remove(TKey key)
    {
        for (var i = 0; i < _source.Count; i++)
        {
            if (!EqualityComparer<TKey>.Default.Equals(_keySelector(_source[i]), key))
                continue;
            _source.RemoveAt(i);
            var selectionChanged = _selection.Remove(key);
            Rebuild();
            if (selectionChanged)
                SelectionChanged?.Invoke();
            return true;
        }
        return false;
    }

    public void SetFilter(Predicate<T>? filter)
    {
        _filter = filter;
        Rebuild();
    }

    public void SetSort(Comparison<T>? comparison)
    {
        _comparison = comparison;
        Rebuild();
    }

    public int IndexOf(TKey key)
    {
        for (var i = 0; i < _view.Count; i++)
        {
            if (EqualityComparer<TKey>.Default.Equals(KeyAt(i), key))
                return i;
        }
        return -1;
    }

    public bool IsSelected(TKey key) => _selection.Contains(key);

    public void SelectOnly(TKey key)
    {
        if (_selection.Count == 1 && _selection.Contains(key))
            return;
        _selection.Clear();
        _selection.Add(key);
        SelectionChanged?.Invoke();
    }

    public void Toggle(TKey key)
    {
        if (!_selection.Remove(key))
            _selection.Add(key);
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        if (_selection.Count == 0)
            return;
        _selection.Clear();
        SelectionChanged?.Invoke();
    }

    private void Rebuild()
    {
        _view.Clear();
        for (var i = 0; i < _source.Count; i++)
        {
            if (_filter == null || _filter(_source[i]))
                _view.Add(i);
        }
        if (_comparison != null)
            _view.Sort(CompareIndices);
        Revision++;
        Changed?.Invoke();
    }

    private int CompareIndices(int left, int right) => _comparison!(_source[left], _source[right]);

    private void RemoveMissingSelection()
    {
        if (_selection.Count == 0)
            return;
        var existing = new HashSet<TKey>();
        foreach (var item in _source)
            existing.Add(_keySelector(item));
        _selection.RemoveWhere(key => !existing.Contains(key));
    }
}
