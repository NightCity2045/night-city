// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Neural;

internal enum NCNeuralPendingEnqueueOutcome : byte
{
    Added,
    ReplacedExisting,
    RetainedExisting,
}

/// <summary>
/// Keeps at most one queued wake per physical ActorId. Repeated observations update that actor's pending context
/// instead of consuming an unbounded global queue. A replacement must be at least as new and at least as urgent;
/// otherwise the retained snapshot may inherit the displaced request's more urgent priority.
/// </summary>
internal sealed class NCNeuralPendingRequestQueue
{
    private readonly PriorityQueue<PendingEntry, (byte Priority, long WakeRevision, long Sequence)> _queue = new();
    private readonly Dictionary<NCActorId, PendingEntry> _pending = [];
    private readonly List<PendingEntry> _temporarilyBlocked = [];
    private long _sequence;

    public int Count => _pending.Count;

    public bool Contains(NCActorId actorId) => _pending.ContainsKey(actorId);

    public NCNeuralPendingEnqueueOutcome Enqueue(
        NCNeuralRequestEnvelope request,
        out NCNeuralRequestEnvelope? coalesced)
    {
        if (_pending.TryGetValue(request.ActorId, out var existing))
        {
            // Async context reads can complete out of order. A stale build or a lower-priority wake cannot displace
            // newer/more urgent work. A displaced urgent request may still accelerate the retained snapshot.
            var incomingDominates = request.WakeRevision >= existing.Request.WakeRevision &&
                                    request.Priority <= existing.Request.Priority;
            var retained = incomingDominates ? request : existing.Request;
            var displaced = incomingDominates ? existing.Request : request;
            var merged = retained.Priority <= (incomingDominates ? existing.Request.Priority : request.Priority)
                ? retained
                : retained with { Priority = incomingDominates ? existing.Request.Priority : request.Priority };
            var replacement = NewEntry(merged);
            _pending[request.ActorId] = replacement;
            _queue.Enqueue(replacement, Priority(replacement));
            coalesced = displaced;
            CompactIfNeeded();
            return incomingDominates
                ? NCNeuralPendingEnqueueOutcome.ReplacedExisting
                : NCNeuralPendingEnqueueOutcome.RetainedExisting;
        }

        var entry = NewEntry(request);
        _pending.Add(request.ActorId, entry);
        _queue.Enqueue(entry, Priority(entry));
        coalesced = null;
        return NCNeuralPendingEnqueueOutcome.Added;
    }

    public bool TryDequeue(IReadOnlySet<NCActorId> blockedActors, out NCNeuralRequestEnvelope request)
    {
        _temporarilyBlocked.Clear();
        var remaining = _queue.Count;
        while (remaining-- > 0 && _queue.TryDequeue(out var entry, out _))
        {
            if (!_pending.TryGetValue(entry.Request.ActorId, out var current) ||
                current.Sequence != entry.Sequence)
            {
                continue;
            }

            if (blockedActors.Contains(entry.Request.ActorId))
            {
                _temporarilyBlocked.Add(entry);
                continue;
            }

            _pending.Remove(entry.Request.ActorId);
            RestoreBlockedEntries();
            request = entry.Request;
            CompactIfNeeded();
            return true;
        }

        RestoreBlockedEntries();
        request = default!;
        CompactIfNeeded();
        return false;
    }

    public void Clear()
    {
        _queue.Clear();
        _pending.Clear();
        _temporarilyBlocked.Clear();
        _sequence = 0;
    }

    private PendingEntry NewEntry(NCNeuralRequestEnvelope request) => new(request, ++_sequence);

    private static (byte Priority, long WakeRevision, long Sequence) Priority(PendingEntry entry) =>
        ((byte) entry.Request.Priority, entry.Request.WakeRevision, entry.Sequence);

    private void RestoreBlockedEntries()
    {
        foreach (var entry in _temporarilyBlocked)
            _queue.Enqueue(entry, Priority(entry));
        _temporarilyBlocked.Clear();
    }

    private void CompactIfNeeded()
    {
        // Replaced heap nodes are lazily invalidated. Rebuild occasionally so a noisy actor cannot grow the heap.
        if (_queue.Count <= Math.Max(32, _pending.Count * 2))
            return;

        _queue.Clear();
        foreach (var entry in _pending.Values)
            _queue.Enqueue(entry, Priority(entry));
    }

    private readonly record struct PendingEntry(NCNeuralRequestEnvelope Request, long Sequence);
}
