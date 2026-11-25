// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.Meta.Devices;
using Content.Shared._NC.Meta.Devices;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.UnitTesting.Pool;

namespace Content.IntegrationTests.Tests._NC.Meta;

/// <summary>
/// Every console and vending machine carries a META board now, and each board registers itself with the device registry and
/// reads its record when it is created. A map holds hundreds of them, so this measures what a few hundred cost: how long they
/// take to be created and become ready, and what they add to every tick while they sit idle.
/// </summary>
[TestFixture]
public sealed class NCMetaHostLoadTest : GameTest
{
    private const int Devices = 320;
    private const int TicksPerSample = 200;

    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test]
    public async Task ThreeHundredIdleGatedDevicesAreCreatedQuicklyBecomeReadyAndAddLittleToATick()
    {
        var prototypes = Server.ResolveDependency<IPrototypeManager>();
        var components = Server.ResolveDependency<IComponentFactory>();
        var maps = Server.System<SharedMapSystem>();
        var registry = Server.System<NCMetaDeviceRegistrySystem>();
        var names = new List<string>();
        await Server.WaitPost(() => names.AddRange(prototypes.EnumeratePrototypes<EntityPrototype>().Where(value => !value.Abstract &&
                !value.ID.StartsWith("NCMetaIntegration", System.StringComparison.Ordinal) &&
                value.TryGetComponent<NCMetaGateComponent>(out _, components)).Select(value => value.ID).OrderBy(value => value).Take(40)));
        Assert.That(names, Has.Count.GreaterThan(10));

        // Devices restore their records only while the world is running, so the test starts a round first, as the game does.
        await Server.WaitPost(() => SEntMan.EventBus.RaiseEvent(EventSource.Local,
            new Content.Server.GameTicking.GameRunLevelChangedEvent(Content.Server.GameTicking.GameRunLevel.PreRoundLobby, Content.Server.GameTicking.GameRunLevel.InRound)));
        var clock = Server.System<Content.Server._NC.LivingCity.Time.NCActiveWorldTimeSystem>();
        for (var tick = 0; tick < 600 && !clock.IsReady; tick++) await Server.WaitRunTicks(1);
        Assert.That(clock.IsReady, Is.True, "The world clock never became ready.");
        MapId map = default;
        await Server.WaitPost(() => maps.CreateMap(out map));
        var baseline = await AverageTickMilliseconds();

        var created = new List<EntityUid>();
        var spawn = Stopwatch.StartNew();
        await Server.WaitPost(() =>
        {
            for (var index = 0; index < Devices; index++)
                created.Add(SEntMan.SpawnEntity(names[index % names.Count], new MapCoordinates(new System.Numerics.Vector2(index % 20 * 2, index / 20 * 2), map)));
        });
        spawn.Stop();

        var ready = Stopwatch.StartNew();
        var allReady = false;
        for (var tick = 0; tick < 1800 && !allReady; tick++)
        {
            await Server.WaitPost(() => allReady = created.All(device => registry.TryGetEntry(device, out var entry) && entry.Ready));
            if (!allReady) await Server.WaitRunTicks(1);
        }
        ready.Stop();
        var loaded = await AverageTickMilliseconds();

        TestContext.Out.WriteLine($"LOAD: {Devices} devices of {names.Count} kinds: spawn {spawn.ElapsedMilliseconds} ms, all ready after {ready.ElapsedMilliseconds} ms, " +
            $"tick without them {baseline:F2} ms, with them {loaded:F2} ms.");
        var notReady = 0;
        await Server.WaitPost(() => notReady = created.Count(device => !(registry.TryGetEntry(device, out var entry) && entry.Ready)));
        Assert.That(allReady, Is.True, $"{notReady} of {Devices} devices never became ready.");
        Assert.That(spawn.ElapsedMilliseconds, Is.LessThan(15000), "Creating the devices took too long.");
        Assert.That(ready.ElapsedMilliseconds, Is.LessThan(30000), "The devices took too long to become ready.");
        Assert.That(loaded, Is.LessThan(baseline + 5), "Idle devices add too much to every tick.");
    }

    private async Task<double> AverageTickMilliseconds()
    {
        var watch = Stopwatch.StartNew();
        await Server.WaitRunTicks(TicksPerSample);
        return watch.Elapsed.TotalMilliseconds / TicksPerSample;
    }
}
