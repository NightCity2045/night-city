// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;
using NightCity.Diagnostics.Core;
using NUnit.Framework;

namespace NightCity.Diagnostics.Tests;

[TestFixture]
public sealed class OperationTests
{
    [Test]
    public async Task DuplicateOperationExecutesOnlyOnceAndConflictingArgumentsFail()
    {
        using var manager = new OperationManager();
        var complete = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<JsonNode?> Execute(CancellationToken _) { Interlocked.Increment(ref calls); return complete.Task; }
        manager.Start("spawn", "one", "same", true, Execute);
        manager.Start("spawn", "one", "same", true, Execute);
        Assert.Throws<DiagnosticException>(() => manager.Start("spawn", "one", "different", true, Execute));
        complete.SetResult(new JsonObject { ["created"] = true });
        await WaitForFinished(manager, "one");
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(manager.Get("one").State, Is.EqualTo("succeeded"));
        });
    }

    [Test]
    public async Task LostMutationResponseIsIndeterminateAndNeverRetried()
    {
        using var manager = new OperationManager();
        manager.Start("interact", "lost", "same", true,
            _ => throw new DiagnosticException("timeout", "Lost response."));
        await WaitForFinished(manager, "lost");
        Assert.That(manager.Get("lost").State, Is.EqualTo("indeterminate"));
    }

    [Test]
    public async Task CancellingReadStopsTheOperation()
    {
        using var manager = new OperationManager();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Start("wait", "read", "same", false, async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        manager.Cancel("read");
        await WaitForFinished(manager, "read");
        Assert.That(manager.Get("read").State, Is.EqualTo("cancelled"));
    }

    private static async Task WaitForFinished(OperationManager manager, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (manager.Get(id).FinishedAt == null)
            await Task.Delay(10, timeout.Token);
    }
}
