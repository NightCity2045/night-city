// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Client._NC.NCUI.Core;

/// <summary>
/// Dense node storage with stable external IDs and swap-back removal. Hot iteration touches one contiguous array.
/// </summary>
public sealed class UiNodeStore
{
    private const int InitialCapacity = 256;

    private readonly Dictionary<UiNodeId, int> _indices = [];
    private UiSurfaceNode[] _nodes = new UiSurfaceNode[InitialCapacity];
    private uint _nextId = 1;

    public int Count { get; private set; }

    public ReadOnlySpan<UiSurfaceNode> Span => _nodes.AsSpan(0, Count);

    public UiNodeId Add(UiSurfaceNode node)
    {
        EnsureCapacity(Count + 1);
        var id = AllocateId();
        node.Id = id;
        _nodes[Count] = node;
        _indices.Add(id, Count);
        Count++;
        return id;
    }

    public bool Contains(UiNodeId id) => _indices.ContainsKey(id);

    public bool TryGet(UiNodeId id, out UiSurfaceNode node)
    {
        if (!_indices.TryGetValue(id, out var index))
        {
            node = default;
            return false;
        }

        node = _nodes[index];
        return true;
    }

    public bool TryGetIndex(UiNodeId id, out int index) => _indices.TryGetValue(id, out index);

    public ref readonly UiSurfaceNode GetAt(int index) => ref _nodes[index];

    public void Set(UiNodeId id, in UiSurfaceNode node)
    {
        if (!_indices.TryGetValue(id, out var index))
            throw new KeyNotFoundException($"Unknown NCUI node {id}.");

        var replacement = node;
        replacement.Id = id;
        _nodes[index] = replacement;
    }

    public bool Remove(UiNodeId id, out UiSurfaceNode removed)
    {
        if (!_indices.Remove(id, out var index))
        {
            removed = default;
            return false;
        }

        removed = _nodes[index];
        Count--;
        if (index != Count)
        {
            var moved = _nodes[Count];
            _nodes[index] = moved;
            _indices[moved.Id] = index;
        }

        _nodes[Count] = default;
        return true;
    }

    public void Clear()
    {
        Array.Clear(_nodes, 0, Count);
        _indices.Clear();
        Count = 0;
        _nextId = 1;
    }

    private UiNodeId AllocateId()
    {
        // A wrapped ID is skipped if a very long-running document still owns it.
        while (_nextId == 0 || _indices.ContainsKey(new UiNodeId(_nextId)))
            _nextId = unchecked(_nextId + 1);

        return new UiNodeId(_nextId++);
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _nodes.Length)
            return;

        Array.Resize(ref _nodes, Math.Max(required, _nodes.Length * 2));
    }
}
