// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Meta.Runtime;

public sealed partial class MetaProcess
{
    private readonly HostRootLease?[] _hostRoots = new HostRootLease?[4];
    private long _hostRootBytes;
    private int _hostRootSlots;

    /// <summary>Bounded process-owned roots for incremental native DTO construction between guest activations.</summary>
    public sealed class HostRootLease : IDisposable
    {
        private MetaProcess? _owner;
        private readonly int _slot;
        private readonly MetaValue[] _values;
        internal int Count;
        internal MetaValue GetValue(int index) => _values[index];
        internal long ChargedBytes => 128L + _values.Length * 16L;
        internal HostRootLease(MetaProcess owner, int slot, int capacity) { _owner = owner; _slot = slot; _values = new MetaValue[capacity]; }

        public bool TryAdd(MetaValue value)
        {
            if (_owner is not { } owner || owner._running || owner._heap.Collecting || Count >= _values.Length ||
                (uint) value.TypeId >= (uint) owner._module.Types.Length ||
                owner.State is MetaProcessState.Stopped or MetaProcessState.Faulted or MetaProcessState.Quarantined) return false;
            try { owner.ValidateValue(value, (int) value.TypeId); }
            catch (MetaTrap) { return false; }
            _values[Count++] = value; return true;
        }

        public void Dispose()
        {
            if (_owner is not { } owner) return;
            _owner = null; owner._hostRoots[_slot] = null;
            owner._hostRootBytes -= ChargedBytes; owner._hostRootSlots -= _values.Length;
            Count = 0; Array.Clear(_values);
        }
    }

    public bool TryCreateHostRoots(int capacity, out HostRootLease lease)
    {
        lease = null!;
        if (_running || _heap.Collecting || capacity is < 1 or > 4096 || capacity > 4096 - _hostRootSlots ||
            State is MetaProcessState.Stopped or MetaProcessState.Faulted or MetaProcessState.Quarantined) return false;
        var slot = Array.FindIndex(_hostRoots, value => value == null);
        var bytes = 128L + capacity * 16L;
        if (slot < 0 || bytes > _limits.MemoryBytes - MemoryBytes) return false;
        lease = new(this, slot, capacity); _hostRoots[slot] = lease;
        _hostRootBytes += bytes; _hostRootSlots += capacity; return true;
    }

    internal void FailHostConstruction(MetaTrap trap)
    {
        Fail(trap);
        foreach (var lease in _hostRoots) lease?.Dispose();
    }

    /// <summary>Build bounded trusted arguments and start atomically, preserving native allocation traps as VM faults.</summary>
    public bool TryStartHostInvocation(int functionId, Func<MetaProcess, IReadOnlyList<MetaValue>> arguments)
    {
        if (_running || State != MetaProcessState.Idle) return false;
        try { return TryStart(functionId, arguments(this)); }
        catch (MetaTrap trap) { FailHostConstruction(trap); return false; }
    }

    public int GetSequenceCount(MetaValue value)
    {
        if (value.Kind is not (MetaValueKind.Array or MetaValueKind.List)) throw new ArgumentException("Not a sequence.", nameof(value));
        return _heap.Get(value).Count;
    }
    public MetaValue GetSequenceElement(MetaValue value, int index)
    {
        var count = GetSequenceCount(value);
        if ((uint) index >= (uint) count) throw new ArgumentOutOfRangeException(nameof(index));
        return _heap.Get(value).Values[index];
    }
    public int GetMapCount(MetaValue value)
    {
        if (value.Kind != MetaValueKind.Map) throw new ArgumentException("Not a map.", nameof(value));
        return _heap.Get(value).Entries!.Count;
    }
    public KeyValuePair<MetaValue, MetaValue> GetMapEntry(MetaValue value, int index)
    {
        var count = GetMapCount(value);
        if ((uint) index >= (uint) count) throw new ArgumentOutOfRangeException(nameof(index));
        return _heap.Get(value).Entries![index];
    }
}
