// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server._NC.LivingCity.Neural;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using NUnit.Framework;

namespace Content.Tests.Server._NC.LivingCity.Neural;

[TestFixture]
public sealed class NCNeuralPendingRequestQueueTest
{
    [Test]
    public void EmergencyDoesNotDiscardUnansweredDialogue()
    {
        var queue = new NCNeuralPendingRequestQueue();
        var actor = new NCActorId(Guid.NewGuid());
        var dialogue = Request(actor, NCNeuralPriority.P1, 1) with
        {
            Kind = NCNeuralRequestKind.DialogueContinuation,
        };
        var emergency = Request(actor, NCNeuralPriority.P0, 2);
        queue.Enqueue(dialogue, out _);
        Assert.That(queue.Enqueue(emergency, out var displaced), Is.EqualTo(NCNeuralPendingEnqueueOutcome.Added));
        Assert.That(displaced, Is.Null);
        Assert.That(queue.CountFor(actor), Is.EqualTo(2));
        Assert.That(queue.TryDequeue(new HashSet<NCActorId>(), out var first), Is.True);
        Assert.That(first.RequestId, Is.EqualTo(emergency.RequestId));
        Assert.That(queue.TryDequeue(new HashSet<NCActorId>(), out var second), Is.True);
        Assert.That(second.RequestId, Is.EqualTo(dialogue.RequestId));
        Assert.That(NCNeuralCognitionClientSystem.ShouldSupersedeInFlight(dialogue, emergency), Is.False);
    }

    [Test]
    public void ReservedSlotSkipsRoutineButAcceptsDialogue()
    {
        var queue = new NCNeuralPendingRequestQueue();
        queue.Enqueue(Request(new NCActorId(Guid.NewGuid()), NCNeuralPriority.P3, 1), out _);
        Assert.That(queue.TryDequeue(new HashSet<NCActorId>(), out _, NCNeuralPriority.P1), Is.False);
        var speech = Request(new NCActorId(Guid.NewGuid()), NCNeuralPriority.P1, 2);
        queue.Enqueue(speech, out _);
        Assert.That(queue.TryDequeue(new HashSet<NCActorId>(), out var actual, NCNeuralPriority.P1), Is.True);
        Assert.That(actual.RequestId, Is.EqualTo(speech.RequestId));
        Assert.That(queue.Count, Is.EqualTo(1));
    }

    [Test]
    public void DelayedRetryDoesNotBlockReadyActorAndNewSpeechRemovesDelay()
    {
        var queue = new NCNeuralPendingRequestQueue();
        var actor = new NCActorId(Guid.NewGuid());
        var retry = Request(actor, NCNeuralPriority.P1, 1);
        var other = Request(new NCActorId(Guid.NewGuid()), NCNeuralPriority.P3, 2);
        queue.Enqueue(retry, out _, 100);
        queue.Enqueue(other, out _);
        Assert.That(queue.TryDequeue(new HashSet<NCActorId>(), out var ready, now: 50), Is.True);
        Assert.That(ready.RequestId, Is.EqualTo(other.RequestId));
        Assert.That(queue.TryDequeue(new HashSet<NCActorId>(), out _, now: 50), Is.False);
        var newer = Request(actor, NCNeuralPriority.P1, 3);
        queue.Enqueue(newer, out _);
        Assert.That(queue.TryDequeue(new HashSet<NCActorId>(), out ready, now: 50), Is.True);
        Assert.That(ready.RequestId, Is.EqualTo(newer.RequestId));
    }

    [Test]
    public void EqualPriorityWakeKeepsLatestContextForActor()
    {
        var queue = new NCNeuralPendingRequestQueue();
        var actor = new NCActorId(Guid.NewGuid());
        var older = Request(actor, NCNeuralPriority.P1, 1);
        var newer = Request(actor, NCNeuralPriority.P1, 2);

        Assert.That(queue.Enqueue(older, out var firstCoalesced),
            Is.EqualTo(NCNeuralPendingEnqueueOutcome.Added));
        Assert.That(firstCoalesced, Is.Null);
        Assert.That(queue.Enqueue(newer, out var secondCoalesced),
            Is.EqualTo(NCNeuralPendingEnqueueOutcome.ReplacedExisting));
        Assert.That(secondCoalesced?.RequestId, Is.EqualTo(older.RequestId));
        Assert.That(queue.Count, Is.EqualTo(1));

        Assert.That(queue.TryDequeue(new HashSet<NCActorId>(), out var dequeued), Is.True);
        Assert.That(dequeued.RequestId, Is.EqualTo(newer.RequestId));
        Assert.That(queue.Count, Is.Zero);
    }

    [Test]
    public void LowerPriorityWakeCannotReplacePendingEmergency()
    {
        var queue = new NCNeuralPendingRequestQueue();
        var actor = new NCActorId(Guid.NewGuid());
        var emergency = Request(actor, NCNeuralPriority.P0, 1);
        var routine = Request(actor, NCNeuralPriority.P3, 2);

        queue.Enqueue(emergency, out _);
        Assert.That(queue.Enqueue(routine, out var coalesced),
            Is.EqualTo(NCNeuralPendingEnqueueOutcome.RetainedExisting));
        Assert.That(coalesced?.RequestId, Is.EqualTo(routine.RequestId));

        Assert.That(queue.TryDequeue(new HashSet<NCActorId>(), out var dequeued), Is.True);
        Assert.That(dequeued.RequestId, Is.EqualTo(emergency.RequestId));
        Assert.That(dequeued.Priority, Is.EqualTo(NCNeuralPriority.P0));
    }

    [Test]
    public void OlderAsyncCompletionCannotReplaceNewerContext()
    {
        var queue = new NCNeuralPendingRequestQueue();
        var actor = new NCActorId(Guid.NewGuid());
        var newer = Request(actor, NCNeuralPriority.P1, 2);
        var olderEmergency = Request(actor, NCNeuralPriority.P0, 1);

        queue.Enqueue(newer, out _);
        Assert.That(queue.Enqueue(olderEmergency, out var displaced),
            Is.EqualTo(NCNeuralPendingEnqueueOutcome.RetainedExisting));
        Assert.That(displaced?.RequestId, Is.EqualTo(olderEmergency.RequestId));

        Assert.That(queue.TryDequeue(new HashSet<NCActorId>(), out var dequeued), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(dequeued.RequestId, Is.EqualTo(newer.RequestId));
            Assert.That(dequeued.WakeRevision, Is.EqualTo(2));
            Assert.That(dequeued.Priority, Is.EqualTo(NCNeuralPriority.P0));
        });
    }

    [Test]
    public void InFlightActorDoesNotBlockAnotherActorsRequest()
    {
        var queue = new NCNeuralPendingRequestQueue();
        var blockedActor = new NCActorId(Guid.NewGuid());
        var availableActor = new NCActorId(Guid.NewGuid());
        queue.Enqueue(Request(blockedActor, NCNeuralPriority.P0, 1), out _);
        var available = Request(availableActor, NCNeuralPriority.P1, 1);
        queue.Enqueue(available, out _);

        Assert.That(queue.TryDequeue(new HashSet<NCActorId> { blockedActor }, out var dequeued), Is.True);
        Assert.That(dequeued.RequestId, Is.EqualTo(available.RequestId));
        Assert.That(queue.Count, Is.EqualTo(1));
    }

    [Test]
    public void RepeatedWakeFloodRemainsOnePendingRequestPerActor()
    {
        var queue = new NCNeuralPendingRequestQueue();
        var actor = new NCActorId(Guid.NewGuid());
        NCNeuralRequestEnvelope latest = null;
        for (var contextVersion = 1; contextVersion <= 1000; contextVersion++)
        {
            latest = Request(actor, NCNeuralPriority.P2, contextVersion);
            queue.Enqueue(latest, out _);
        }

        Assert.That(queue.Count, Is.EqualTo(1));
        Assert.That(queue.TryDequeue(new HashSet<NCActorId>(), out var dequeued), Is.True);
        Assert.That(dequeued.RequestId, Is.EqualTo(latest.RequestId));
    }

    [Test]
    public void ThirtyActorBurstDequeuesEqualPriorityInStableFifoOrder()
    {
        var queue = new NCNeuralPendingRequestQueue();
        var requests = Enumerable.Range(0, 30)
            .Select(index => Request(new NCActorId(Guid.NewGuid()), NCNeuralPriority.P3, index + 1))
            .ToArray();
        // Async builders may finish in reverse order; wake revision keeps equal-priority service deterministic.
        foreach (var request in requests.Reverse())
            queue.Enqueue(request, out _);

        var actual = new List<Guid>(requests.Length);
        while (queue.TryDequeue(new HashSet<NCActorId>(), out var request))
            actual.Add(request.RequestId);

        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(requests.Select(request => request.RequestId)));
            Assert.That(queue.Count, Is.Zero);
        });
    }

    [Test]
    public void FreshEmergencySupersedesInFlightRoutineForSameActor()
    {
        var actor = new NCActorId(Guid.NewGuid());

        Assert.That(NCNeuralCognitionClientSystem.ShouldSupersedeInFlight(
            Request(actor, NCNeuralPriority.P3, 1),
            Request(actor, NCNeuralPriority.P0, 2)), Is.True);
    }

    [Test]
    public void LowerPriorityWakeDoesNotSupersedeInFlightEmergency()
    {
        var actor = new NCActorId(Guid.NewGuid());

        Assert.That(NCNeuralCognitionClientSystem.ShouldSupersedeInFlight(
            Request(actor, NCNeuralPriority.P0, 1),
            Request(actor, NCNeuralPriority.P3, 2)), Is.False);
    }

    [Test]
    public void OlderEmergencyCompletionDoesNotSupersedeNewerInFlightContext()
    {
        var actor = new NCActorId(Guid.NewGuid());

        Assert.That(NCNeuralCognitionClientSystem.ShouldSupersedeInFlight(
            Request(actor, NCNeuralPriority.P3, 2),
            Request(actor, NCNeuralPriority.P0, 1)), Is.False);
    }

    [Test]
    public void AnotherActorsWakeCannotSupersedeInFlightRequest()
    {
        Assert.That(NCNeuralCognitionClientSystem.ShouldSupersedeInFlight(
            Request(new NCActorId(Guid.NewGuid()), NCNeuralPriority.P3, 1),
            Request(new NCActorId(Guid.NewGuid()), NCNeuralPriority.P0, 2)), Is.False);
    }

    [Test]
    public void ThirtyActorsDispatchIndependentlyButNeverTwicePerActor()
    {
        var queue = new NCNeuralPendingRequestQueue();
        var inFlight = new HashSet<NCActorId>();
        for (var index = 0; index < 30; index++)
            queue.Enqueue(Request(NCActorId.New(), NCNeuralPriority.P3, 1), out _);

        while (queue.TryDequeue(inFlight, out var request))
        {
            Assert.That(inFlight.Add(request.ActorId), Is.True);
            queue.Enqueue(Request(request.ActorId, NCNeuralPriority.P3, 2), out _);
        }

        Assert.That(inFlight.Count, Is.EqualTo(30));
        Assert.That(queue.Count, Is.EqualTo(30), "Each body's next request waits for its own completion.");
        Assert.That(queue.TryDequeue(inFlight, out _), Is.False);
    }

    private static NCNeuralRequestEnvelope Request(NCActorId actorId, NCNeuralPriority priority, long version) =>
        new(
            Guid.NewGuid(),
            NCNeuralRequestKind.StrategicDecision,
            priority,
            actorId,
            NCSessionEpoch.New(),
            version,
            new NCActiveWorldTime(TimeSpan.FromMinutes(5).Ticks),
            "NCLivingCityDefault",
            "living-city-default",
            3,
            "physical-gpt-alife-v3",
            [],
            ["physical.wait"],
            [],
            "en-US")
        {
            WakeRevision = version,
        };
}
