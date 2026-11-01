// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;
using NightCity.Diagnostics.Core;
using NUnit.Framework;

namespace NightCity.Diagnostics.Tests;

[TestFixture]
public sealed class RuntimeStartupTests
{
    private static readonly DiagnosticTarget Server = new("owned-server", "server", "unused", "unused");

    [Test]
    public async Task AColdBridgeCanBecomeReadyAfterThePreviousThreeMinuteLimit()
    {
        var clock = new AdvancingClock();
        var bridge = new StartupBridge(() => clock.Elapsed < TimeSpan.FromSeconds(210) ? throw Unavailable() : new JsonObject());
        await RuntimeStartupWaiter.WaitAsync(bridge, Server, () => false, CancellationToken.None, clock);
        Assert.That(clock.Elapsed, Is.EqualTo(TimeSpan.FromSeconds(210)));
    }

    [Test]
    public void AnUnreadyBridgeStillFailsAtTheBoundedSixMinuteDeadline()
    {
        var clock = new AdvancingClock();
        var bridge = new StartupBridge(() => throw Unavailable());
        var error = Assert.ThrowsAsync<DiagnosticException>(() =>
            RuntimeStartupWaiter.WaitAsync(bridge, Server, () => false, CancellationToken.None, clock));
        Assert.Multiple(() =>
        {
            Assert.That(error!.Code, Is.EqualTo("startup_timeout"));
            Assert.That(clock.Elapsed, Is.EqualTo(TimeSpan.FromMinutes(6)));
        });
    }

    [Test]
    public void AnOwnedProcessExitFailsBeforeAnotherBridgeProbe()
    {
        var clock = new AdvancingClock();
        var bridge = new StartupBridge(() => throw Unavailable());
        var error = Assert.ThrowsAsync<DiagnosticException>(() => RuntimeStartupWaiter.WaitAsync(bridge, Server,
            () => clock.Elapsed >= TimeSpan.FromSeconds(1), CancellationToken.None, clock));
        Assert.Multiple(() =>
        {
            Assert.That(error!.Code, Is.EqualTo("process_exited"));
            Assert.That(clock.Elapsed, Is.EqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(bridge.Calls, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task AClientBridgeDoesNotBecomeReadyUntilItsUserIsPresent()
    {
        var clock = new AdvancingClock();
        var bridge = new StartupBridge(() => clock.Elapsed < TimeSpan.FromSeconds(2)
            ? new JsonObject() : new JsonObject { ["userId"] = "connected-test-user" });
        await RuntimeStartupWaiter.WaitAsync(bridge, Server with { Kind = "client" }, () => false, CancellationToken.None, clock);
        Assert.That(clock.Elapsed, Is.EqualTo(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public void CallerCancellationStopsStartupBeforeAnyProbe()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var bridge = new StartupBridge(() => new JsonObject());
        Assert.ThrowsAsync<OperationCanceledException>(() =>
            RuntimeStartupWaiter.WaitAsync(bridge, Server, () => false, cancellation.Token, new AdvancingClock()));
        Assert.That(bridge.Calls, Is.Zero);
    }

    private static DiagnosticException Unavailable() => new("bridge_unavailable", "Not ready.");

    private sealed class StartupBridge(Func<JsonObject> read) : IDiagnosticBridge
    {
        public int Calls;
        public Task<DiagnosticResponse> ExecuteAsync(DiagnosticTarget target, DiagnosticRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new DiagnosticResponse
            {
                RequestId = request.RequestId, ServerInstanceId = target.Id, Source = target.Kind, Data = read(),
            });
        }
    }

    private sealed class AdvancingClock : TimeProvider
    {
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
        private TimeSpan _elapsed;
        public TimeSpan Elapsed => _elapsed;
        public override DateTimeOffset GetUtcNow() => _start + _elapsed;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _elapsed += dueTime;
            var timer = new CallbackTimer(callback, state);
            ThreadPool.QueueUserWorkItem(_ => timer.Fire());
            return timer;
        }
    }

    private sealed class CallbackTimer(TimerCallback callback, object? state) : ITimer
    {
        private int _state;
        public void Fire() { if (Interlocked.CompareExchange(ref _state, 1, 0) == 0) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;
        public void Dispose() => Interlocked.Exchange(ref _state, 2);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
