// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Ambient;
using Content.Server._NC.LivingCity.Neural;
using Content.Server._NC.LivingCity.Observability;
using Content.Server._NC.LivingCity.Simulation;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Hardening;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(NCAmbientCrowdSystem))]
[TestOf(typeof(NCLivingCityObservabilitySystem))]
public sealed class NCLivingCityHardeningRuntimeTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test]
    public async Task RoundLifecycleOwnsAmbientObservabilityAndTransientNeuralRecovery()
    {
        var server = Pair.Server;
        var ambient = server.System<NCAmbientCrowdSystem>();
        var observability = server.System<NCLivingCityObservabilitySystem>();
        var neural = server.System<NCNeuralCognitionClientSystem>();

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await server.WaitRunTicks(10);

        Assert.Multiple(() =>
        {
            Assert.That(ambient.GetCounters().Active, Is.True);
            Assert.That(ambient.GetCounters().InteractiveEntities, Is.Zero,
                "A disconnected test round has no player relevance cluster and must not spawn ambient entities.");
            Assert.That(observability.GetSnapshot().Active, Is.True);
            Assert.That(observability.GetSnapshot().ConfigurationError, Is.Null);
            Assert.That(neural.ResetCircuitBreaker(), Is.True);
        });

        var before = NCPersistenceTelemetry.Snapshot();
        NCPersistenceTelemetry.Record(false, TimeSpan.FromMilliseconds(5), null, false);
        var after = NCPersistenceTelemetry.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(after.Reads, Is.GreaterThanOrEqualTo(before.Reads + 1));
            Assert.That(after.LatencySamples, Is.GreaterThanOrEqualTo(1));
            Assert.That(after.LatencyP99Milliseconds, Is.GreaterThanOrEqualTo(0));
        });

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await server.WaitRunTicks(2);
        Assert.Multiple(() =>
        {
            Assert.That(ambient.GetCounters().Active, Is.False);
            Assert.That(observability.GetSnapshot().Active, Is.False);
            Assert.That(neural.ResetCircuitBreaker(), Is.False);
        });
    }
}
