// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Meta.Runtime;

internal sealed class MetaHeapObject
{
    public required MetaValueKind Kind;
    public string? Text;
    public MetaValue[] Values = [];
    public List<KeyValuePair<MetaValue, MetaValue>>? Entries;
    public int Count;
    public int Tag;
    public long Version;
    public long Bytes;
    public long KeyStringBytes;
}

/// <summary>Process-owned index/generation object table, with incremental mark/sweep and no guest CLR references.</summary>
internal sealed class MetaHeap
{
    private sealed class Entry
    {
        public uint Generation;
        public MetaHeapObject? Object;
        public long MarkEpoch;
    }
    private readonly List<Entry> _entries = [];
    private readonly Stack<int> _free = [];
    private readonly Queue<int> _gray = [];
    private static long _nextGeneration;
    private readonly uint _initialGeneration = NextGeneration();
    private static uint NextGeneration()
    {
        var generation = Interlocked.Increment(ref _nextGeneration);
        if (generation is <= 0 or > uint.MaxValue) throw new MetaTrap(MetaVmFaultCode.InternalFault, "Heap identity space exhausted.");
        return (uint) generation;
    }
    private IEnumerator<MetaValue>? _roots;
    private bool _rootPhase;
    private int _scanIndex = -1;
    private int _scanChild;
    private int _sweep;
    private bool _sweeping;
    private long _markEpoch;
    public long Bytes { get; private set; }
    public long Churn { get; private set; }
    public bool Collecting { get; private set; }
    public int GcWork { get; private set; }

    public MetaValue Allocate(int typeId, MetaHeapObject value, long freeBytes, long churnLimit)
    {
        var newSlot = _free.Count == 0;
        var objectBytes = checked(value.Bytes + 64);
        // Table/GC/free-list capacity stays charged after an object is swept.
        var bytes = checked(objectBytes + (newSlot ? 64 : 0));
        if (bytes > freeBytes || bytes > churnLimit - Churn) throw new MetaTrap(MetaVmFaultCode.HeapLimit, "Allocation quota exceeded.");
        var index = _free.Count > 0 ? _free.Pop() : _entries.Count;
        Entry entry;
        if (index == _entries.Count) { entry = new Entry { Generation = _initialGeneration }; _entries.Add(entry); }
        else entry = _entries[index];
        entry.Object = value; entry.MarkEpoch = 0;
        value.Bytes = objectBytes;
        Bytes += bytes; Churn += bytes;
        return new MetaValue(value.Kind, typeId, ((ulong) entry.Generation << 32) | (uint) index);
    }

    public MetaHeapObject Get(MetaValue value)
    {
        var index = (uint) value.Payload;
        var generation = (uint) (value.Payload >> 32);
        if (index >= _entries.Count || _entries[(int) index].Generation != generation || _entries[(int) index].Object is not { } obj || obj.Kind != value.Kind)
            throw new MetaTrap(MetaVmFaultCode.InvalidBytecode, "Invalid process object handle.");
        return obj;
    }

    public void Grow(MetaHeapObject obj, int capacity, long freeBytes, long churnLimit)
    {
        var added = checked((long) capacity * 16 + 32);
        // Reserve old and new buffer simultaneously before allocating/copying.
        if (added > freeBytes || added > churnLimit - Churn) throw new MetaTrap(MetaVmFaultCode.HeapLimit, "Collection growth quota exceeded.");
        var replacement = new MetaValue[capacity];
        Array.Copy(obj.Values, replacement, obj.Count);
        var old = checked((long) obj.Values.Length * 16 + (obj.Values.Length == 0 ? 0 : 32));
        Array.Clear(obj.Values);
        obj.Values = replacement;
        obj.Bytes += added - old; Bytes += added - old; Churn += added;
    }

    public void ChargeMapGrowth(MetaHeapObject obj, long bytes, long freeBytes, long churnLimit)
    {
        if (bytes > freeBytes || bytes > churnLimit - Churn) throw new MetaTrap(MetaVmFaultCode.HeapLimit, "Map growth quota exceeded.");
        obj.Bytes += bytes; Bytes += bytes; Churn += bytes;
    }

    public void BeginCollection(IEnumerable<MetaValue> roots)
    {
        if (Collecting) return;
        Collecting = true; _markEpoch = checked(_markEpoch + 1); GcWork = 0; _sweep = 0; _sweeping = false; _rootPhase = true; _scanIndex = -1;
        _roots = roots.GetEnumerator();
    }

    public int CollectSlice(int work, int maximumWork)
    {
        var consumed = 0;
        while (Collecting && consumed < work)
        {
            if (++GcWork > maximumWork) throw new MetaTrap(MetaVmFaultCode.HeapLimit, "GcWorkExceeded");
            consumed++;
            if (_rootPhase)
            {
                if (_roots!.MoveNext()) Mark(_roots.Current);
                else { _roots.Dispose(); _roots = null; _rootPhase = false; }
                continue;
            }
            if (!_sweeping)
            {
                if (_scanIndex < 0)
                {
                    if (!_gray.TryDequeue(out _scanIndex)) { _sweeping = true; continue; }
                    _scanChild = 0;
                }
                var obj = _entries[_scanIndex].Object!;
                var children = obj.Entries != null ? obj.Entries.Count * 2 : obj.Values.Length;
                if (_scanChild < children)
                {
                    Mark(obj.Entries != null ? (_scanChild % 2 == 0 ? obj.Entries[_scanChild / 2].Key : obj.Entries[_scanChild / 2].Value) : obj.Values[_scanChild]);
                    _scanChild++;
                }
                else _scanIndex = -1;
                continue;
            }
            if (_sweep >= _entries.Count) { Collecting = false; break; }
            var entry = _entries[_sweep];
            if (entry.Object is { } dead && entry.MarkEpoch != _markEpoch)
            {
                // Clear large buffers incrementally too, before returning the slot to the free list.
                if (dead.Count > 0)
                {
                    dead.Count--;
                    if (dead.Entries != null) dead.Entries.RemoveAt(dead.Entries.Count - 1);
                    else if (dead.Count < dead.Values.Length) dead.Values[dead.Count] = default;
                    continue;
                }
                Bytes -= dead.Bytes; dead.Text = null; dead.Values = []; dead.Entries?.Clear();
                entry.Object = null;
                // Generations are allocated globally so a value from another process cannot alias this slot.
                entry.Generation = NextGeneration();
                _free.Push(_sweep);
            }
            _sweep++;
        }
        return consumed;
    }

    public void RestartCollection(IEnumerable<MetaValue> roots)
    {
        _roots?.Dispose(); _roots = null; _gray.Clear();
        Collecting = false;
        BeginCollection(roots);
    }

    private void Mark(MetaValue value)
    {
        if (value.Kind is < MetaValueKind.String or > MetaValueKind.Map) return;
        Get(value);
        var index = (int) (uint) value.Payload;
        var entry = _entries[index];
        if (entry.MarkEpoch == _markEpoch) return;
        entry.MarkEpoch = _markEpoch;
        _gray.Enqueue(index);
    }
}
