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
