// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.Meta;

/// <summary>Runs real durable APIs under the native server context while the test pumps its lifecycle.</summary>
internal static class NCMetaNativeTestTasks
{
    public static Task<bool> CloseWorld(RobustIntegrationTest.ServerIntegrationInstance server) => Run(server, async () =>
    {
        var clock = server.System<NCActiveWorldTimeSystem>();
        if (clock.IsReady)
            server.EntMan.EventBus.RaiseEvent(EventSource.Local,
                new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PreRoundLobby));
        await clock.WaitForIdleAsync();
        return true;
    });

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
            remaining = TimeSpan.FromSeconds(30) - elapsed.Elapsed;
            if (!completion.Task.IsCompleted && remaining > TimeSpan.Zero)
                await Task.WhenAny(completion.Task, Task.Delay(server.Timing.TickPeriod < remaining
                    ? server.Timing.TickPeriod : remaining));
        }
        Assert.That(completion.Task.IsCompleted, Is.True,
            $"Real persistence did not complete within 30 seconds/1800 native ticks: {start.Method.DeclaringType?.FullName}.{start.Method.Name}.");
        return await completion.Task;
    }
}
