// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Meta.Demo;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Domains;
using Content.Server._NC.Meta.Network;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Demo;
using Content.Shared._NC.Meta.Domains;
using Content.Shared._NC.Meta.Network;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared.Construction.Components;
using Content.Shared.Power.Components;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Storage.Components;
using Content.Shared.VendingMachines;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture]
[TestOf(typeof(NCMetaDemoSceneSystem))]
public sealed class NCMetaDemoSceneTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };
    [SidedDependency(Side.Server)] private NCMetaDemoSceneSystem _scenes = default!;
    [SidedDependency(Side.Server)] private NCMetaDeviceRegistrySystem _devices = default!;
    [SidedDependency(Side.Server)] private NCMetaNetworkSystem _network = default!;
    [SidedDependency(Side.Server)] private SharedBatterySystem _batteries = default!;

    [TestCase("NCMetaDoorScene")]
    [TestCase("NCMetaGarageScene")]
    [TestCase("NCMetaNetScene")]
    [TestCase("NCMetaPatrolDeliveryScene")]
    [TestCase("NCMetaConveyorScene")]
    [TestCase("NCMetaBedsideLabScene")]
    public async Task ShippedSceneUsesTheActualPhysicalEquipmentAndFiniteNativePower(string sceneId)
    {
        NCMetaDemoSceneInstance scene = null;
        await Server.WaitAssertion(() => Assert.That(_scenes.TryCreate(sceneId, out scene), Is.True));
        try
        {
            await Pair.RunTicksSync(120);
            await Server.WaitAssertion(() =>
            {
                var definition = SProtoMan.Index<NCMetaDemoScenePrototype>(sceneId);
                Assert.That(scene.Objects.Count, Is.EqualTo(definition.Objects.Count));
                Assert.That(scene.Cables, Has.Length.EqualTo(definition.Cables.Count));
                Assert.That(_devices.IsPowered(scene.Objects["host"]), Is.True, "Power must come from the charged APC and real LV network.");
                Assert.That(_batteries.GetCharge(scene.PowerSupply), Is.GreaterThan(0).And.LessThan(SComp<BatteryComponent>(scene.PowerSupply).MaxCharge));
                foreach (var cable in scene.Cables)
                {
                    var ends = SComp<NCMetaNetworkCableComponent>(cable);
                    Assert.That(ends.PortA, Is.Not.Null); Assert.That(ends.PortB, Is.Not.Null);
                    Assert.That(ends.DeviceAId, Is.Not.Empty); Assert.That(ends.DeviceBId, Is.Not.Empty);
                    Assert.That(_network.CanRoute(ends.PortA!.Value, ends.PortB!.Value), Is.True);
                }
                foreach (var item in scene.Objects.Values)
                    if (_devices.TryGetEntry(item, out var entry)) Assert.That(entry.Owner, Is.Empty, "A fixture does not issue ownership or service grants.");
                if (scene.Objects.TryGetValue("drone", out var drone))
                    Assert.That(SComp<NCMetaDroneComponent>(drone).Waypoints, Is.Empty, "Physical markers still require explicit native commissioning.");
                if (scene.Objects.TryGetValue("bedside", out var bedside))
                {
                    Assert.That(SComp<NCMetaClinicalComponent>(bedside).Patient, Is.Null);
                    Assert.That(SComp<NCMetaClinicalComponent>(bedside).AutomationEnabled, Is.False);
                }
                if (scene.Objects.TryGetValue("vehicle", out var vehicle))
                    Assert.That(Server.System<NCMetaEcuSystem>().CanBind(vehicle, "meta.vehicle.EngineController"), Is.True);
                if (scene.Objects.TryGetValue("grinder", out var grinder))
                    Assert.That(Server.System<NCMetaMachineSystem>().CanBind(grinder, "meta.lab.Grinder"), Is.True);
            });
        }
        finally { await Server.WaitPost(() => _scenes.TryDestroy(scene.Id)); }
    }

    [Test]
    public async Task ActualPowerAndCableLossBreakTheSceneRouteWithoutChangingAnyVm()
    {
        NCMetaDemoSceneInstance scene = null;
        await Server.WaitAssertion(() => Assert.That(_scenes.TryCreate("NCMetaDoorScene", out scene), Is.True));
        try
        {
            await Pair.RunTicksSync(120);
            await Server.WaitAssertion(() =>
            {
                var host = scene.Objects["host"]; var sensor = scene.Objects["sensor"];
                Assert.That(_network.CanRoute(host, sensor), Is.True);
                var cable = scene.Cables.Single(uid =>
                {
                    var ends = SComp<NCMetaNetworkCableComponent>(uid);
                    return ends.PortA == sensor || ends.PortB == sensor;
                });
                Server.System<SharedTransformSystem>().SetCoordinates(scene.Operator, SComp<TransformComponent>(cable).Coordinates);
                Assert.That(_network.TryDisconnectCable(cable, scene.Operator), Is.True);
                Assert.That(_network.CanRoute(host, sensor), Is.False);
                _batteries.SetCharge(scene.PowerSupply, 0);
            });
            await Pair.RunTicksSync(120);
            await Server.WaitAssertion(() => Assert.That(_devices.IsPowered(scene.Objects["host"]), Is.False));
        }
        finally { await Server.WaitPost(() => _scenes.TryDestroy(scene.Id)); }
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void MappedVendomatStockSuppliesTheActualFiniteCommissioningKitWithoutGrantingControl()
    {
        var stock = SProtoMan.Index<VendingMachineInventoryPrototype>("VendomatInventory");
        Assert.That(stock.StartingInventory["NCMetaCommissioningKit"], Is.EqualTo(2));
        var maps = Server.System<SharedMapSystem>();
        var map = maps.CreateMap();
        try
        {
            var kit = SEntMan.SpawnEntity("NCMetaCommissioningKit", new EntityCoordinates(map, Vector2.Zero));
            var contents = SComp<EntityStorageComponent>(kit).Contents.ContainedEntities;
            string Prototype(EntityUid uid) => SComp<MetaDataComponent>(uid).EntityPrototype!.ID;
            Assert.That(contents, Has.Count.EqualTo(14));
            Assert.That(contents.Any(uid => Prototype(uid) == "Multitool"), Is.True, "Flatpacks require the native pulsing tool quality.");
            Assert.That(contents.Count(uid => Prototype(uid) == "NCMetaNetworkCable"), Is.EqualTo(4));
            var cells = contents.Where(uid => Prototype(uid) == "PowerCellMedium").ToArray();
            Assert.That(cells, Has.Length.EqualTo(2));
            foreach (var cell in cells) Assert.That(_batteries.GetCharge(cell), Is.GreaterThan(0));
            var tablet = contents.Single(uid => Prototype(uid) == "NCMetaServiceTablet");
            Assert.That(_devices.TryGetEntry(tablet, out var host), Is.True);
            Assert.That(host.Owner, Is.Empty);
            foreach (var result in new[] { "NCMetaTemperatureSensor", "NCMetaAlarm", "NCMetaDoorPanel" })
                Assert.That(contents.Any(uid => SEntMan.TryGetComponent<FlatpackComponent>(uid, out var flatpack) &&
                    flatpack.Entity?.Id == result), Is.True);
        }
        finally { SEntMan.QueueDeleteEntity(map); }
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ADeadPhysicalVehicleBatteryCannotRemainAnOnlineNetworkEndpoint()
    {
        var mapEntity = Server.System<SharedMapSystem>().CreateMap(out var map);
        try
        {
            var vehicle = SEntMan.SpawnEntity("NCMetaSupraEcu", new MapCoordinates(Vector2.Zero, map));
            Assert.That(_devices.IsPowered(vehicle), Is.True);
            Assert.That(_network.CanRoute(vehicle, vehicle), Is.True);
            SComp<NCVehicleConditionComponent>(vehicle).BatteryCharge = 0;
            Assert.That(_devices.IsPowered(vehicle), Is.False);
            Assert.That(_network.CanRoute(vehicle, vehicle), Is.False);
        }
        finally { SEntMan.QueueDeleteEntity(mapEntity); }
    }
}
