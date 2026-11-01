// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._NC.Meta.Commerce;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Network;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaNativeNetworkTest
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task CitiNetRequiresALivePhysicalGatewayAndDistinguishesMarketplaceService(bool softwareMarketplace)
    {
        var scene = await CreateScene();
        try
        {
            var host = scene.Physical.Objects["host"];
            EntityUid gateway = default;
            await Server.WaitPost(() =>
            {
                MoveTo(scene.Actor, host);
                gateway = SEntMan.SpawnEntity("NCMetaMarketplaceService", SComp<TransformComponent>(host).Coordinates.Offset(Vector2.UnitX));
                SComp<NCMetaCitiNetServiceComponent>(gateway).SoftwareMarketplace = softwareMarketplace;
                SComp<NCMetaMessagePortComponent>(gateway).GatewayNetworks.Add(SComp<NCMetaPortComponent>(host).NetworkId);
            });
            await Until(() => _devices.TryGetEntry(gateway, out var device) && device.Ready && _devices.IsPowered(gateway));
            await Server.WaitAssertion(() =>
            {
                var city = Server.System<NCMetaMarketplaceSystem>();
                var port = SComp<NCMetaPortComponent>(host);
                var original = port.NetworkId;
                port.NetworkId = "CITYNET";
                Assert.That(city.HasCitiNetRoute(host), Is.False, "An Internet-looking label cannot replace a physical connection.");
                port.NetworkId = original;

                var cable = SEntMan.SpawnEntity("NCMetaNetworkCable", SComp<TransformComponent>(host).Coordinates);
                Assert.That(_network.TryConnectCable(cable, scene.Actor, host), Is.True);
                Assert.That(city.HasCitiNetRoute(host), Is.False, "One cable end is not a route.");
                Assert.That(_network.TryConnectCable(cable, scene.Actor, gateway), Is.True);
                Assert.That(city.HasCitiNetRoute(host), Is.True, "A local host reaches CitiNet through its native gateway.");
                Assert.That(city.HasMarketplaceRoute(host), Is.EqualTo(softwareMarketplace));

                Assert.That(_network.TryDisconnectCable(cable, scene.Actor), Is.True);
                Assert.That(city.HasCitiNetRoute(host), Is.False);
                Assert.That(_network.TryConnectCable(cable, scene.Actor, host), Is.True);
                Assert.That(_network.TryConnectCable(cable, scene.Actor, gateway), Is.True);
                Assert.That(city.HasCitiNetRoute(host), Is.True);
                SEntMan.DeleteEntity(gateway);
                Assert.That(city.HasCitiNetRoute(host), Is.False, "A removed gateway must immediately stop authorizing public sessions.");
            });
        }
        finally { await Destroy(scene); }
    }
}
