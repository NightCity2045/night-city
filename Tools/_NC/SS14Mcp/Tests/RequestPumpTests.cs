// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;
using NUnit.Framework;

namespace NightCity.Diagnostics.Tests;

[TestFixture]
public sealed class RequestPumpTests
{
    [Test]
    public async Task StaleAndUnpinnedMutationsNeverReachGameplay()
    {
        var pump = new DiagnosticRequestPump("test");
        var called = 0;
        var unpinned = pump.EnqueueAsync(new DiagnosticRequest { Command = "mutate" }, CancellationToken.None);
        Drain(pump, _ => { called++; return null; });
        Assert.That((await unpinned).Error!.Code, Is.EqualTo("world_reference_required"));
        var oldGeneration = pump.WorldGeneration;
        pump.ResetWorld();
        var stale = pump.EnqueueAsync(new DiagnosticRequest { Command = "mutate", ServerInstanceId = pump.InstanceId,
            WorldGeneration = oldGeneration }, CancellationToken.None);
        Drain(pump, _ => { called++; return null; });
        Assert.Multiple(() =>
        {
            Assert.That(stale.Result.Error!.Code, Is.EqualTo("stale_world"));
            Assert.That(called, Is.Zero);
        });
    }

    [Test]
    public async Task DuplicateMutationReturnsRetainedResultAndRejectsConflictingPayload()
    {
        var pump = new DiagnosticRequestPump("test");
        var request = new DiagnosticRequest { Command = "mutate", ServerInstanceId = pump.InstanceId,
            WorldGeneration = pump.WorldGeneration, Arguments = new JsonObject { ["value"] = 1 } };
        var lease = pump.EnqueueAsync(new DiagnosticRequest { Command = "lease_acquire", Arguments = new JsonObject { ["owner"] = "test" } }, CancellationToken.None);
        Drain(pump, _ => null);
        request = request with { LeaseOwner = "test", LeaseToken = lease.Result.Data!["token"]!.GetValue<string>() };
        var calls = 0;
        JsonNode Mutate(DiagnosticRequest _) => new JsonObject { ["calls"] = ++calls };
        var first = pump.EnqueueAsync(request, CancellationToken.None);
        Drain(pump, Mutate);
        var second = pump.EnqueueAsync(request, CancellationToken.None);
        Drain(pump, Mutate);
        var conflict = pump.EnqueueAsync(request with { Arguments = new JsonObject { ["value"] = 2 } }, CancellationToken.None);
        Drain(pump, Mutate);
        Assert.Multiple(() =>
        {
            Assert.That(first.Result.Data!.ToJsonString(), Is.EqualTo(second.Result.Data!.ToJsonString()));
            Assert.That(conflict.Result.Error!.Code, Is.EqualTo("idempotency_conflict"));
            Assert.That(calls, Is.EqualTo(1));
        });
        await Task.WhenAll(first, second, conflict);
    }

    [Test]
    public async Task QueueRejectsOverloadAndStopCompletesEveryAdmittedRequest()
    {
        var pump = new DiagnosticRequestPump("test");
        var admitted = Enumerable.Range(0, DiagnosticProtocol.MaximumQueueLength)
            .Select(_ => pump.EnqueueAsync(new DiagnosticRequest { Command = "read" }, CancellationToken.None)).ToArray();
        var extra = await pump.EnqueueAsync(new DiagnosticRequest { Command = "read" }, CancellationToken.None);
        Assert.That(extra.Error!.Code, Is.EqualTo("busy"));
        pump.Stop();
        var responses = await Task.WhenAll(admitted);
        Assert.Multiple(() =>
        {
            Assert.That(responses.All(x => x.Error!.Code == "bridge_unavailable"), Is.True);
            Assert.That(pump.QueueLength, Is.Zero);
        });
    }

    [Test]
    public async Task ExpiredRequestIsRejectedBeforeExecution()
    {
        var pump = new DiagnosticRequestPump("test");
        var result = pump.EnqueueAsync(new DiagnosticRequest { Command = "read", DeadlineUnixMilliseconds = 1 }, CancellationToken.None);
        var calls = 0;
        Drain(pump, _ => { calls++; return null; });
        Assert.That((await result).Error!.Code, Is.EqualTo("timeout"));
        Assert.That(calls, Is.Zero);
    }

    private static void Drain(DiagnosticRequestPump pump, Func<DiagnosticRequest, JsonNode?> dispatch)
    {
        for (var attempt = 0; pump.QueueLength != 0 && attempt < 100; attempt++)
            pump.Process(42, dispatch, command => command == "mutate");
        Assert.That(pump.QueueLength, Is.Zero);
    }

    [Test]
    public async Task CancellationBeforeDispatchDoesNotMutateTheWorld()
    {
        var pump = new DiagnosticRequestPump("test");
        using var cancellation = new CancellationTokenSource();
        var result = pump.EnqueueAsync(new DiagnosticRequest { Command = "mutate", ServerInstanceId = pump.InstanceId,
            WorldGeneration = pump.WorldGeneration }, cancellation.Token);
        cancellation.Cancel();
        var calls = 0;
        Drain(pump, _ => { calls++; return null; });
        try { await result; } catch (OperationCanceledException) { }
        Assert.That(calls, Is.Zero);
    }
}
