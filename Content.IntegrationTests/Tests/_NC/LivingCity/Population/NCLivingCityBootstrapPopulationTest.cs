// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Population;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Population;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Population;

[TestFixture]
[TestOf(typeof(NCCityPopulationImportSystem))]
[NonParallelizable]
public sealed class NCLivingCityBootstrapPopulationTest : GameTest
{
    private static readonly ProtoId<NCCityPopulationProfilePrototype> Profile =
        new("NCLivingCityDistrictBootstrapPopulation");

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task ProductionDistrictPopulationImportsIdempotentlyAndRemainsAbstractWithoutChildZones()
    {
        var server = Pair.Server;
        var clock = server.System<NCActiveWorldTimeSystem>();
        var importer = server.System<NCCityPopulationImportSystem>();
        var runtime = server.System<NCCityActorRuntimeIndexSystem>();

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world clock did not start.");

        var first = await importer.ImportAsync(Profile, "integration.production-bootstrap");
        var second = await importer.ImportAsync(Profile, "integration.production-bootstrap");
        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(NCCityPopulationImportStatus.Completed));
            Assert.That(first.Requested, Is.EqualTo(256));
            Assert.That(first.Created, Is.EqualTo(256));
            Assert.That(first.Failed, Is.Zero);
            Assert.That(first.Failures, Is.Empty);
            Assert.That(second.Status, Is.EqualTo(NCCityPopulationImportStatus.Completed));
            Assert.That(second.Requested, Is.EqualTo(256));
            Assert.That(second.Existing, Is.EqualTo(256));
            Assert.That(second.Failed, Is.Zero);
            Assert.That(runtime.MaterializedActorCount, Is.Zero);
        });

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await WaitUntil(server, () => !clock.IsReady, "The active-world clock did not stop.");
    }

    private static async Task WaitUntil(
        RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate,
        string failure,
        int attempts = 360)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await server.WaitRunTicks(1);
            var complete = false;
            await server.WaitPost(() => complete = predicate());
            if (complete)
                return;
        }
        Assert.Fail(failure);
    }
}
