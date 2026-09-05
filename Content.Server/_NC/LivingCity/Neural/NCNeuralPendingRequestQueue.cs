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
/// Keeps one dialogue and one autonomous wake per physical ActorId. Repeated observations update their own lane
/// instead of consuming an unbounded global queue. A replacement must be at least as new and at least as urgent;
/// otherwise the retained snapshot may inherit the displaced request's more urgent priority.
/// </summary>
internal sealed class NCNeuralPendingRequestQueue
{
    private readonly PriorityQueue<PendingEntry, (byte Priority, long WakeRevision, long Sequence)> _queue = new();
    private readonly Dictionary<(NCActorId Actor, bool Dialogue), PendingEntry> _pending = [];
    private readonly List<PendingEntry> _temporarilyBlocked = [];
    private long _sequence;

    public int Count => _pending.Count;

    public bool Contains(NCActorId actorId) => CountFor(actorId) != 0;

    public int CountFor(NCActorId actorId) =>
        (_pending.ContainsKey((actorId, false)) ? 1 : 0) + (_pending.ContainsKey((actorId, true)) ? 1 : 0);

    // Speech must survive an unrelated autonomous wake. Execution remains serialized by ActorId.
    private static (NCActorId Actor, bool Dialogue) Key(NCNeuralRequestEnvelope request) =>
        (request.ActorId, request.Kind == NCNeuralRequestKind.DialogueContinuation);

    public NCNeuralPendingEnqueueOutcome Enqueue(
        NCNeuralRequestEnvelope request,
        out NCNeuralRequestEnvelope? coalesced,
        long notBefore = 0)
    {
        var key = Key(request);
        if (_pending.TryGetValue(key, out var existing))
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
            var replacement = NewEntry(merged, incomingDominates ? notBefore : existing.NotBefore);
            _pending[key] = replacement;
            _queue.Enqueue(replacement, Priority(replacement));
            coalesced = displaced;
            CompactIfNeeded();
            return incomingDominates
                ? NCNeuralPendingEnqueueOutcome.ReplacedExisting
                : NCNeuralPendingEnqueueOutcome.RetainedExisting;
        }

        var entry = NewEntry(request, notBefore);
        _pending.Add(key, entry);
        _queue.Enqueue(entry, Priority(entry));
        coalesced = null;
        return NCNeuralPendingEnqueueOutcome.Added;
    }

    public bool TryDequeue(IReadOnlySet<NCActorId> blockedActors, out NCNeuralRequestEnvelope request,
        NCNeuralPriority maximumPriority = NCNeuralPriority.P4, long now = long.MaxValue)
    {
        _temporarilyBlocked.Clear();
        var remaining = _queue.Count;
        while (remaining-- > 0 && _queue.TryDequeue(out var entry, out _))
        {
            if (!_pending.TryGetValue(Key(entry.Request), out var current) ||
                current.Sequence != entry.Sequence)
            {
                continue;
            }

            if (blockedActors.Contains(entry.Request.ActorId) || entry.Request.Priority > maximumPriority ||
                entry.NotBefore > now)
            {
                _temporarilyBlocked.Add(entry);
                continue;
            }

            _pending.Remove(Key(entry.Request));
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

    private PendingEntry NewEntry(NCNeuralRequestEnvelope request, long notBefore) =>
        new(request, ++_sequence, notBefore);

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

    private readonly record struct PendingEntry(NCNeuralRequestEnvelope Request, long Sequence, long NotBefore);
}
