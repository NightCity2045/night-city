// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using Content.Meta.Diagnostics;

namespace Content.Meta.Runtime;

public sealed partial class MetaProcess
{
    private sealed record QueuedEvent(int WatchIndex, long Sequence, MetaValue[] Arguments, long Bytes, ulong AuthorityToken, ulong ContextToken,
        long ArrivalTimestamp, long SlotTimestamp);
    private readonly List<QueuedEvent> _events = [];
    private long _eventBytes;
    public int QueuedEvents => _events.Count;
    public long CoalescedEvents { get; private set; }
    public ulong CurrentEventAuthorityToken { get; private set; }
    public event Action<ulong>? EventAuthorityReleased;
    public ulong CurrentEventContextToken { get; private set; }
    public event Action<ulong>? EventContextReleased;

    /// <summary>Enqueue an already decoded, process-owned payload. Collecting processes apply backpressure before any copy.</summary>
    public bool TryEnqueueEvent(int watchIndex, IReadOnlyList<MetaValue> arguments, long sequence, ulong authorityToken = 0, ulong contextToken = 0)
    {
        var arrival = MetaMetrics.Timestamp(Metrics);
        bool Rejected() { MetaMetrics.Record(Metrics, MetaMetric.EventsRejected, context: watchIndex); return false; }
        if (_running || State is MetaProcessState.Stopped or MetaProcessState.Faulted or MetaProcessState.Quarantined or MetaProcessState.Collecting || watchIndex < 0 || watchIndex >= _module.Watches.Length || sequence < 0) return Rejected();
        try
        {
            var watch = _module.Watches[watchIndex]; var function = _module.Functions[watch.FunctionId];
            if (arguments.Count != function.ParameterTypes.Length) return Rejected();
            long bytes = 64L + arguments.Count * 16L;
            for (var index = 0; index < arguments.Count; index++)
            {
                ValidateValue(arguments[index], function.ParameterTypes[index]);
                bytes = checked(bytes + LogicalBytes(arguments[index], _limits.EventBytes - bytes, 0));
            }
            var existing = _events.FindIndex(queued => queued.WatchIndex == watchIndex);
            if (existing >= 0 && sequence <= _events[existing].Sequence) return Rejected();
            var replacedBytes = watch.Delivery == "Latest" && existing >= 0 ? _events[existing].Bytes : 0;
            var sameWatchCount = watch.Delivery == "Latest" && existing >= 0 ? 0 : _events.Count(queued => queued.WatchIndex == watchIndex);
            if (bytes > _limits.EventBytes - _eventBytes + replacedBytes || sameWatchCount >= watch.Capacity || _events.Count - (replacedBytes > 0 ? 1 : 0) >= _limits.Events || bytes > _limits.MemoryBytes - MemoryBytes + replacedBytes)
            {
                Stop(MetaStopReason.EventOverflow);
                return Rejected();
            }
            var slot = replacedBytes > 0 ? _events[existing].SlotTimestamp : arrival;
            if (replacedBytes > 0) { ReleaseEventAuthority(_events[existing].AuthorityToken); ReleaseEventContext(_events[existing].ContextToken); Array.Clear(_events[existing].Arguments); _events.RemoveAt(existing); _eventBytes -= replacedBytes; CoalescedEvents++; }
            _events.Add(new QueuedEvent(watchIndex, sequence, arguments.ToArray(), bytes, authorityToken, contextToken, arrival, slot)); _eventBytes += bytes;
            MetaMetrics.Record(Metrics, MetaMetric.EventsAccepted, context: watchIndex);
            if (replacedBytes > 0) MetaMetrics.Record(Metrics, MetaMetric.EventsCoalesced, context: watchIndex);
            return true;
        }
        catch (MetaTrap trap)
        {
            if (trap.Message == "Event payload limit.") Stop(MetaStopReason.EventOverflow);
            else Fail(trap);
            return Rejected();
        }
    }

    public bool TryStartNextEvent()
    {
        if (State != MetaProcessState.Idle || _events.Count == 0 || _running) return false;
        var queued = _events[0]; _events.RemoveAt(0); _eventBytes -= queued.Bytes;
        var started = TryStart(_module.Watches[queued.WatchIndex].FunctionId, queued.Arguments);
        if (started)
        {
            _activationIsWatch = true; CurrentEventAuthorityToken = queued.AuthorityToken; CurrentEventContextToken = queued.ContextToken;
            MetaMetrics.Record(Metrics, MetaMetric.EventsStarted, context: queued.WatchIndex);
            MetaMetrics.Latency(Metrics, MetaMetric.EventQueueLatency, queued.ArrivalTimestamp, queued.WatchIndex);
            MetaMetrics.Latency(Metrics, MetaMetric.EventSlotLatency, queued.SlotTimestamp, queued.WatchIndex);
        }
        else { ReleaseEventAuthority(queued.AuthorityToken); ReleaseEventContext(queued.ContextToken); }
        Array.Clear(queued.Arguments);
        return started;
    }

    private void ReleaseCurrentEventAuthority()
    {
        var token = CurrentEventAuthorityToken; CurrentEventAuthorityToken = 0; ReleaseEventAuthority(token);
        var context = CurrentEventContextToken; CurrentEventContextToken = 0; ReleaseEventContext(context);
    }
    private void ReleaseEventContext(ulong token)
    {
        if (token == 0) return;
        try { EventContextReleased?.Invoke(token); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { Fault = new MetaFault(MetaVmFaultCode.InternalFault, "Event context release failed."); State = MetaProcessState.Faulted; }
    }
    private void ReleaseEventAuthority(ulong token)
    {
        if (token == 0) return;
        try { EventAuthorityReleased?.Invoke(token); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { Fault = new MetaFault(MetaVmFaultCode.InternalFault, "Event authority release failed."); State = MetaProcessState.Faulted; }
    }

    private long LogicalBytes(MetaValue value, long remaining, int depth)
    {
        if (remaining < 16 || depth > 16) throw new MetaTrap(MetaVmFaultCode.HeapLimit, "Event payload limit.");
        if (value.Kind == MetaValueKind.String)
        {
            var bytes = checked(16L + Encoding.UTF8.GetByteCount(GetString(value)));
            if (bytes > remaining) throw new MetaTrap(MetaVmFaultCode.HeapLimit, "Event payload limit.");
            return bytes;
        }
        if (value.Kind is < MetaValueKind.Record or > MetaValueKind.Map) return 16;
        var obj = _heap.Get(value); long total = 32;
        if (obj.Entries != null)
        {
            foreach (var pair in obj.Entries)
            {
                total = checked(total + LogicalBytes(pair.Key, remaining - total, depth + 1));
                total = checked(total + LogicalBytes(pair.Value, remaining - total, depth + 1));
            }
        }
        else for (var index = 0; index < obj.Count; index++) total = checked(total + LogicalBytes(obj.Values[index], remaining - total, depth + 1));
        if (total > remaining) throw new MetaTrap(MetaVmFaultCode.HeapLimit, "Event payload limit.");
        return total;
    }
}
