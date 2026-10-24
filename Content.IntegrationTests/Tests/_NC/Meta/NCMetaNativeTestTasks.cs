// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Robust.Shared.Asynchronous;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.Meta;

/// <summary>Runs real durable APIs under the native server context while the test pumps its lifecycle.</summary>
internal static class NCMetaNativeTestTasks
{
    private sealed class ServerContext(ITaskManager tasks) : SynchronizationContext
    {
        public override SynchronizationContext CreateCopy() => this;
        public override void Post(SendOrPostCallback callback, object? state) => tasks.RunOnMainThread(() => Run(() => callback(state)));
        public void Run(Action callback)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { callback(); }
            finally { SetSynchronizationContext(previous); }
        }
    }

    public static async Task<T> Run<T>(RobustIntegrationTest.ServerIntegrationInstance server, Func<Task<T>> start)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new ServerContext(server.ResolveDependency<ITaskManager>());
        var elapsed = Stopwatch.StartNew();
        await server.WaitPost(() => context.Run(async () =>
        {
            try { completion.TrySetResult(await start()); }
            catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
        })).WaitAsync(TimeSpan.FromSeconds(30));
        for (var tick = 0; !completion.Task.IsCompleted && tick < 1800; tick++)
        {
            var remaining = TimeSpan.FromSeconds(30) - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await server.WaitRunTicks(1).WaitAsync(remaining);
        }
        Assert.That(completion.Task.IsCompleted, Is.True, "Real persistence did not complete within 30 seconds/1800 native ticks.");
        return await completion.Task;
    }
}
