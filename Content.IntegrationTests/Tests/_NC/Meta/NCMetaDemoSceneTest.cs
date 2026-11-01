// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.Atmos.EntitySystems;
using Content.Server._NC.Meta.Demo;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture, NonParallelizable]
public sealed class NCMetaDemoSceneTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test]
    public async Task DeviceSceneProvidesNativeCityAirToItsPhysicalActors()
    {
        NCMetaDemoSceneInstance? scene = null;
        try
        {
            await Server.WaitAssertion(() => Assert.That(
                Server.System<NCMetaDemoSceneSystem>().TryCreate("NCMetaDeviceScene", out scene), Is.True));
            await Server.WaitRunTicks(2);
            await Server.WaitAssertion(() =>
            {
                Assert.That(SEntMan.HasComponent<GridAtmosphereComponent>(scene!.Grid), Is.False);
                var air = Server.System<AtmosphereSystem>().GetContainingMixture(scene.Operator);
                Assert.That(air, Is.Not.Null);
                Assert.That(air!.Immutable, Is.True);
                Assert.That(air.Pressure, Is.GreaterThan(80f));
                Assert.That(air.GetMoles(Gas.Oxygen) / air.TotalMoles, Is.InRange(0.19f, 0.23f));
            });
        }
        finally
        {
            if (scene != null)
            {
                await Server.WaitPost(() => Server.System<NCMetaDemoSceneSystem>().TryDestroy(scene.Id));
                await Server.WaitRunTicks(2);
            }
        }
    }
}
