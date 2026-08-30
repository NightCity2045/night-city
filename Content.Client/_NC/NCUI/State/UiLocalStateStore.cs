// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Client._NC.NCUI.State;

/// <summary>
/// Typed presenter state that is intentionally never replicated: tabs, zoom, filters and local selections.
/// </summary>
public sealed class UiLocalStateStore<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _values = [];

    public int Count => _values.Count;
    public event Action<TKey>? Changed;

    public bool TryGet(TKey key, out TValue value) => _values.TryGetValue(key, out value!);

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        if (_values.TryGetValue(key, out var value))
            return value;
        value = factory(key);
        _values.Add(key, value);
        Changed?.Invoke(key);
        return value;
    }

    public void Set(TKey key, TValue value)
    {
        _values[key] = value;
        Changed?.Invoke(key);
    }

    public bool Remove(TKey key)
    {
        if (!_values.Remove(key))
            return false;
        Changed?.Invoke(key);
        return true;
    }

    public void Clear() => _values.Clear();
}
