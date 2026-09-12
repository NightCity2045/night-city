// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Vehicle;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;
using Robust.Shared;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Atmos.Components;
using Content.Shared._NC.Medical.Wounds;
using System.Linq;
using Content.Shared.Lock;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Content.Server.Stack;
using Robust.Shared.Player;

namespace Content.IntegrationTests.Tests._NC.Vehicle;

[TestFixture]
public sealed partial class NCVehicleLifecycleTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false, Dirty = true };

    [Test]
    [RunOnSide(Side.Server)]
    public void GaragePassageRejectsBlockedExitAndCarriesExistingOccupants()
    {
        var maps = Server.System<SharedMapSystem>();
        maps.CreateMap(out var street);
        maps.CreateMap(out var interior);
        var entrance = SEntMan.SpawnEntity("NCVehicleGarageEntrance", new MapCoordinates(Vector2.Zero, street));
        SEntMan.SpawnEntity("NCVehicleGarageExit", new MapCoordinates(Vector2.Zero, interior));
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(Vector2.Zero, street));
        var driver = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, street));
        var containers = Server.System<SharedContainerSystem>();
        Assert.That(containers.TryGetContainer(car, "nc-vehicle-driver", out var seat), Is.True);
        Assert.That(containers.Insert(driver, seat!), Is.True);
        var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
        drive.Driver = driver;
        var obstacle = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(new Vector2(0, -3), interior));
        var infrastructure = Server.System<NCVehicleInfrastructureSystem>();
        Assert.That(infrastructure.TryTravel(car, entrance, driver), Is.False);
        Assert.That(SEntMan.GetComponent<TransformComponent>(car).MapID, Is.EqualTo(street));
        SEntMan.DeleteEntity(obstacle);
        var authority = drive.Authority;
        Assert.That(infrastructure.TryTravel(car, entrance, driver), Is.True);
        Assert.That(SEntMan.GetComponent<TransformComponent>(car).MapID, Is.EqualTo(interior));
        Assert.That(SEntMan.GetComponent<TransformComponent>(driver).MapID, Is.EqualTo(interior));
        Assert.That(seat!.Contains(driver), Is.True, "The passenger entity and seat container must not be recreated.");
        Assert.That(drive.Authority, Is.GreaterThan(authority), "Packets authored before the map transition must become stale.");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void BatteryChargeTravelsWithDisconnectedHardware()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(Vector2.Zero, mapId));
        var assemblies = Server.System<NCVehicleAssemblySystem>();
        var graph = SEntMan.GetComponent<NCVehicleAssemblyComponent>(car);
        var condition = SEntMan.GetComponent<NCVehicleConditionComponent>(car);
        condition.BatteryCharge = 7;
        Assert.That(assemblies.TrySetPart(car, "battery", graph.Installed["battery"] with { Connected = false }), Is.True);
        Assert.That(condition.BatteryCharge, Is.Zero);
        Assert.That(graph.Installed["battery"].StoredCharge, Is.EqualTo(7));
        Assert.That(assemblies.TrySetPart(car, "battery", graph.Installed["battery"] with { Connected = true }), Is.True);
        Assert.That(condition.BatteryCharge, Is.EqualTo(7));
        Assert.That(assemblies.TrySetPart(car, "battery", null), Is.True);
        Assert.That(condition.BatteryCharge, Is.Zero);
        var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
        drive.EngineRunning = true;
        Server.System<NCVehicleConditionSystem>().Advance(car, 0, 1);
        Assert.That(condition.BatteryCharge, Is.Zero, "An alternator cannot charge a removed battery.");
        Assert.That(assemblies.TrySetPart(car, "battery", new NCVehicleInstalledPart("NCVehicleBattery", 1, StoredCharge: 3)), Is.True);
        Assert.That(condition.BatteryCharge, Is.EqualTo(3), "A replacement carries its own charge.");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void FlatbedCarriesActualMassAndDeletionReleasesTheSameCar()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var origin = new MapCoordinates(Vector2.Zero, mapId);
        var truck = SEntMan.SpawnEntity("NCVehicleRecoveryTruck", origin);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(new Vector2(0, -6.4f), mapId));
        var user = SEntMan.SpawnEntity("MobHuman", origin);
        var cable = SEntMan.SpawnEntity("NCVehicleWinchCable", origin);
        var hands = Server.System<SharedHandsSystem>();
        Assert.That(hands.TryPickup(user, cable), Is.True);
        var tow = Server.System<NCVehicleTowSystem>();
        Assert.That(tow.TryUseCable(truck, user, cable), Is.True);
        var transform = Server.System<SharedTransformSystem>();
        transform.SetCoordinates(user, SEntMan.GetComponent<TransformComponent>(car).Coordinates);
        Assert.That(tow.TryUseCable(car, user, cable), Is.True);
        transform.SetCoordinates(user, transform.ToCoordinates(new MapCoordinates(new Vector2(1.5f, -3.5f), mapId)));
        var recovery = Server.System<NCVehicleRecoverySystem>();
        Assert.That(recovery.TryToggleWinch(truck, user), Is.True);
        var joint = SEntMan.GetComponent<Robust.Shared.Physics.JointComponent>(truck).GetJoints.Values.Single();
        var distance = (Robust.Shared.Physics.Dynamics.Joints.DistanceJoint) joint;
        var before = distance.MaxLength;
        var pulse = new NCVehicleMaintenanceEvent(1);
        SEntMan.EventBus.RaiseLocalEvent(truck, ref pulse);
        Assert.That(distance.MaxLength, Is.LessThan(before));
        var mass = SEntMan.GetComponent<PhysicsComponent>(truck).Mass;
        var payload = SEntMan.GetComponent<PhysicsComponent>(car).Mass;
        Assert.That(recovery.TryComplete(truck, car, user, NCVehicleRecoveryAction.Load), Is.True);
        Assert.That(SEntMan.GetComponent<NCVehicleRecoveryComponent>(truck).Cargo, Is.EqualTo(car));
        Assert.That(Server.System<NCVehicleAccessSystem>().CanIgnite(truck, user), Is.False, "Loose cargo prevents departure.");
        Assert.That(SEntMan.GetComponent<PhysicsComponent>(truck).Mass, Is.EqualTo(mass + payload).Within(0.1));
        Assert.That(recovery.TryComplete(truck, car, user, NCVehicleRecoveryAction.Secure), Is.True);
        Assert.That(Server.System<NCVehicleAccessSystem>().CanIgnite(truck, user), Is.True);
        SEntMan.DeleteEntity(truck);
        Assert.That(SEntMan.EntityExists(car), Is.True);
        Assert.That(Server.System<SharedContainerSystem>().IsEntityInContainer(car), Is.False);
        Assert.That(SEntMan.GetComponent<TransformComponent>(car).MapID, Is.EqualTo(mapId));
        Assert.That(SEntMan.GetComponent<PhysicsComponent>(car).CanCollide, Is.True);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void PhysicalKeysCashSaleAndTrunkRejectStaleOrUnauthorizedActions()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var coordinates = new MapCoordinates(Vector2.Zero, mapId);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", coordinates);
        var seller = SEntMan.SpawnEntity("MobHuman", coordinates);
        var buyer = SEntMan.SpawnEntity("MobHuman", coordinates);
        SEntMan.AddComponent<ActorComponent>(seller);
        SEntMan.AddComponent<ActorComponent>(buyer);
        var ownership = Server.System<NCVehicleOwnershipSystem>();
        var access = Server.System<NCVehicleAccessSystem>();
        Assert.That(ownership.TryRegister(car, seller), Is.True);
        Assert.That(ownership.TryRegister(car, buyer), Is.False);
        Assert.That(access.HasKey(car, seller), Is.True);
        Assert.That(access.CanIgnite(car, buyer), Is.False);
        var locks = Server.System<LockSystem>();
        Assert.That(locks.CanToggleLock(car, seller), Is.True);
        // This server-only fixture has no live network channels for the stock lock popup.
        locks.Lock(car, null);
        Assert.That(locks.CanToggleLock(car, buyer), Is.False);
        var trunk = new StorageInteractAttemptEvent(true);
        SEntMan.EventBus.RaiseLocalEvent(car, ref trunk);
        Assert.That(trunk.Cancelled, Is.True);
        Assert.That(locks.CanToggleLock(car, seller), Is.True);
        locks.Unlock(car, null);
        trunk = new StorageInteractAttemptEvent(true);
        SEntMan.EventBus.RaiseLocalEvent(car, ref trunk);
        Assert.That(trunk.Cancelled, Is.False);
        Assert.That(ownership.TryOffer(car, seller, 100), Is.True);
        var state = SEntMan.GetComponent<NCVehicleAccessComponent>(car);
        var revision = state.SaleRevision;
        Assert.That(ownership.TryBuy(car, buyer, 100, revision), Is.False, "No physical money, no transfer.");
        var cash = Server.System<StackSystem>().SpawnAtPosition(150, "Credit", SEntMan.GetComponent<TransformComponent>(buyer).Coordinates);
        Assert.That(Server.System<SharedHandsSystem>().TryPickup(buyer, cash), Is.True);
        Assert.That(ownership.TryBuy(car, buyer, 101, revision), Is.False, "The buyer must accept the displayed price.");
        Assert.That(ownership.TryBuy(car, buyer, 100, revision), Is.True);
        Assert.That(SEntMan.GetComponent<StackComponent>(cash).Count, Is.EqualTo(50));
        Assert.That(state.Owner, Is.EqualTo(buyer));
        Assert.That(access.HasKey(car, seller), Is.False, "Every old key must be revoked by the handover.");
        Assert.That(access.HasKey(car, buyer), Is.True);
        Assert.That(ownership.TryBuy(car, buyer, 100, revision), Is.False);
        Assert.That(SEntMan.GetComponent<StackComponent>(cash).Count, Is.EqualTo(50), "Replayed acceptance cannot debit twice.");
        // Remove channel-less actor markers before the normal player/network update during teardown.
        SEntMan.RemoveComponent<ActorComponent>(seller);
        SEntMan.RemoveComponent<ActorComponent>(buyer);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void UnsecuredModuleCannotConnectOrProducePower()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var coordinates = new MapCoordinates(Vector2.Zero, mapId);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", coordinates);
        var mechanic = SEntMan.SpawnEntity("MobHuman", coordinates);
        var tool = SEntMan.SpawnEntity("NCVehicleMechanicTool", coordinates);
        var hands = Server.System<SharedHandsSystem>();
        Assert.That(hands.TryPickup(mechanic, tool), Is.True);
        var service = Server.System<NCVehicleServiceSystem>();
        Assert.That(service.TryStart(car, mechanic, "", NCVehicleServiceAction.Hood), Is.True);
        var assemblies = Server.System<NCVehicleAssemblySystem>();
        Assert.That(assemblies.TrySetPart(car, "coil", new NCVehicleInstalledPart("NCVehicleCoil", 1, false, false)), Is.True);
        Assert.That(service.CanService(car, mechanic, "coil", NCVehicleServiceAction.Connect, tool), Is.False);
        Assert.That(service.CanService(car, mechanic, "coil", NCVehicleServiceAction.Fasten, tool), Is.False);
        var bolts = SEntMan.SpawnEntity("NCVehicleFastenerPack", coordinates);
        Assert.That(hands.TryPickupAnyHand(mechanic, bolts), Is.True);
        Assert.That(service.CanService(car, mechanic, "coil", NCVehicleServiceAction.Fasten, tool), Is.True);
        var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
        Assert.That(NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Ignition), Is.Zero);
        Assert.That(assemblies.TrySetPart(car, "coil", new NCVehicleInstalledPart("NCVehicleCoil", 1)), Is.True);
        Assert.That(NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Ignition), Is.EqualTo(1));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void WasherConsumesFluidAndRolloverRequiresRecovery()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var coordinates = new MapCoordinates(Vector2.Zero, mapId);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", coordinates);
        var driver = SEntMan.SpawnEntity("MobHuman", coordinates);
        var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
        drive.Driver = driver;
        var accessories = SEntMan.GetComponent<NCVehicleAccessoriesComponent>(car);
        accessories.WindshieldDirt = 1;
        var solutions = Server.System<SharedSolutionContainerSystem>();
        Assert.That(solutions.TryGetSolution(car, "washer", out _, out var washer), Is.True);
        var before = washer!.Volume;
        Assert.That(Server.System<NCVehicleAccessorySystem>().TryUse(car, driver, NCVehicleDriverAction.Wash), Is.True);
        Assert.That(washer.Volume, Is.EqualTo(before - FixedPoint2.New(accessories.WasherVolume)));
        Assert.That(accessories.WindshieldDirt, Is.LessThan(1));
        var damage = Server.System<NCVehicleDamageSystem>();
        Assert.That(damage.TryOverturn(car), Is.True);
        Assert.That(drive.AssemblyReady, Is.False);
        Assert.That(Server.System<NCVehicleConditionSystem>().TryStart(car), Is.False);
        Assert.That(damage.TryRight(car), Is.True);
        Assert.That(drive.AssemblyReady, Is.True);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void WeaponPipelineDamagesBodyAndAirbagsAreSpentHardware()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(Vector2.Zero, mapId));
        var shell = SEntMan.GetComponent<NCVehicleBodyComponent>(car);
        var weaponDamage = new DamageSpecifier();
        weaponDamage.DamageDict["Piercing"] = FixedPoint2.New(30);
        Server.System<DamageableSystem>().ChangeDamage(car, weaponDamage);
        Assert.That(shell.Deformation, Is.Not.Empty);
        Server.System<NCVehicleDamageSystem>().TryImpact(car, Vector2.UnitY, Vector2.UnitY, 1, 10);
        Assert.That(shell.AirbagsDeployed, Is.True);
        var assembly = SEntMan.GetComponent<NCVehicleAssemblyComponent>(car);
        Assert.That(assembly.Installed["airbag"].Condition, Is.Zero);
        Assert.That(Server.System<NCVehicleAssemblySystem>().TrySetPart(car, "airbag", new NCVehicleInstalledPart("NCVehicleAirbag", 1)), Is.True);
        Assert.That(shell.AirbagsDeployed, Is.False);
        var condition = SEntMan.GetComponent<NCVehicleConditionComponent>(car);
        condition.EngineTemperature = shell.FireIgnitionTemperature;
        var pulse = new NCVehicleMaintenanceEvent(1);
        SEntMan.EventBus.RaiseLocalEvent(car, ref pulse);
        Assert.That(SEntMan.GetComponent<FlammableComponent>(car).OnFire, Is.True);
        Assert.That(Server.System<NCVehicleConditionSystem>().TryStart(car), Is.False);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void DiagnosticInstrumentsRequireTheirPhysicalServiceCategory()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var coordinates = new MapCoordinates(Vector2.Zero, mapId);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", coordinates);
        var mechanic = SEntMan.SpawnEntity("MobHuman", coordinates);
        var scanner = SEntMan.SpawnEntity("NCVehicleScanner", coordinates);
        var hands = Server.System<SharedHandsSystem>();
        Assert.That(hands.TryPickup(mechanic, scanner), Is.True);
        var service = Server.System<NCVehicleServiceSystem>();
        Assert.That(service.TryStart(car, mechanic, "", NCVehicleServiceAction.Hood), Is.True);
        Assert.That(service.CanService(car, mechanic, "coil", NCVehicleServiceAction.Inspect, scanner), Is.True);
        Assert.That(service.CanService(car, mechanic, "oil-filter", NCVehicleServiceAction.Inspect, scanner), Is.False);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void BrokenCoolantSealLeaksActualReservoirAndReplacementStopsLeak()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(Vector2.Zero, mapId));
        var solutions = Server.System<SharedSolutionContainerSystem>();
        Assert.That(solutions.TryGetSolution(car, "coolant", out _, out var coolant), Is.True);
        var before = coolant!.Volume;
        var assemblies = Server.System<NCVehicleAssemblySystem>();
        Assert.That(assemblies.TrySetPart(car, "coolant-hose", new NCVehicleInstalledPart("NCVehicleCoolantHose", 0)), Is.True);
        var pulse = new NCVehicleMaintenanceEvent(1);
        SEntMan.EventBus.RaiseLocalEvent(car, ref pulse);
        Assert.That(coolant.Volume, Is.LessThan(before));
        Assert.That(SEntMan.GetComponent<NCVehicleConditionComponent>(car).CoolantFraction, Is.LessThan(1));
        var after = coolant.Volume;
        Assert.That(assemblies.TrySetPart(car, "coolant-hose", new NCVehicleInstalledPart("NCVehicleCoolantHose", 1)), Is.True);
        SEntMan.EventBus.RaiseLocalEvent(car, ref pulse);
        Assert.That(coolant.Volume, Is.EqualTo(after));
        Assert.That(solutions.TryGetSolution(car, "steering", out _, out var steering), Is.True);
        Assert.That(steering!.Volume, Is.GreaterThan(FixedPoint2.Zero));
    }

    [TestCase("NCToyotaSupra1996", "NCSupraAutomaticRwd", "NCSupraAutomaticAwdConversion")]
    [TestCase("NCVehicleRecoveryTruck", "NCRecoveryTruckAutomaticHandling", "NCRecoveryTruckAutomaticAwdHandling")]
    [RunOnSide(Side.Server)]
    public void PhysicalTransmissionConversionRequiresItsHardwareAndCanBeReversed(string prototype, string automatic, string allWheel)
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var car = SEntMan.SpawnEntity(prototype, new MapCoordinates(Vector2.Zero, mapId));
        var system = Server.System<NCVehicleAssemblySystem>();
        var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
        Assert.That(system.TrySetPart(car, "transfer-case", new NCVehicleInstalledPart("NCVehicleTransferCase", 1)), Is.False);
        Assert.That(system.TrySetPart(car, "gearbox", new NCVehicleInstalledPart("NCVehicleAutomaticGearbox", 1)), Is.True);
        Assert.That(drive.Handling.Id, Is.EqualTo(automatic));
        Assert.That(system.TrySetPart(car, "front-differential", new NCVehicleInstalledPart("NCVehicleFrontDifferential", 1)), Is.True);
        Assert.That(system.TrySetPart(car, "front-left-shaft", new NCVehicleInstalledPart("NCVehicleFrontLeftShaft", 1)), Is.True);
        Assert.That(system.TrySetPart(car, "front-right-shaft", new NCVehicleInstalledPart("NCVehicleFrontRightShaft", 1)), Is.True);
        Assert.That(system.TrySetPart(car, "transfer-case", new NCVehicleInstalledPart("NCVehicleTransferCase", 1)), Is.True);
        Assert.That(drive.Handling.Id, Is.EqualTo(allWheel));
        Assert.That(drive.Gear, Is.Zero);
        Assert.That(system.TrySetPart(car, "front-differential", null), Is.False);
        Assert.That(system.TrySetPart(car, "transfer-case", null), Is.True);
        Assert.That(drive.Handling.Id, Is.EqualTo(automatic));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ModularCornersAndElectricalCircuitsFailIndependently()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(Vector2.Zero, mapId));
        var assembly = SEntMan.GetComponent<NCVehicleAssemblyComponent>(car);
        var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
        var system = Server.System<NCVehicleAssemblySystem>();
        Assert.That(assembly.Installed.Count, Is.InRange(40, 80));
        Assert.That(system.TrySetPart(car, "fl-tyre", null), Is.True);
        Assert.That(NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Grip, 0), Is.Zero);
        Assert.That(NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Grip, 1), Is.EqualTo(1));
        Assert.That(system.TrySetPart(car, "fr-hub", null), Is.False, "A hub cannot be removed through the installed wheel.");
        Assert.That(system.TrySetPart(car, "starter", null), Is.True);
        Assert.That(Server.System<NCVehicleConditionSystem>().TryStart(car), Is.False);
        Assert.That(system.TrySetPart(car, "starter", new NCVehicleInstalledPart("NCVehicleStarter", 1)), Is.True);
        Assert.That(Server.System<NCVehicleConditionSystem>().TryStart(car), Is.True);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void DisconnectedAlternatorDischargesBatteryAndWheelWearStaysAtItsCorner()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(Vector2.Zero, mapId));
        var assembly = SEntMan.GetComponent<NCVehicleAssemblyComponent>(car);
        var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
        var state = SEntMan.GetComponent<NCVehicleConditionComponent>(car);
        Assert.That(Server.System<NCVehicleAssemblySystem>().TrySetPart(car, "alternator",
            assembly.Installed["alternator"] with { Connected = false }), Is.True);
        drive.EngineRunning = true;
        var charge = state.BatteryCharge;
        Server.System<NCVehicleConditionSystem>().Advance(car, 0, state.Interval);
        Assert.That(state.BatteryCharge, Is.LessThan(charge));
        drive.WheelWear[0] = 0.1f;
        var wear = new NCVehicleWearEvent(0, 0);
        SEntMan.EventBus.RaiseLocalEvent(car, ref wear);
        Assert.That(assembly.Installed["fl-tyre"].Condition, Is.EqualTo(0.9f).Within(0.001));
        Assert.That(assembly.Installed["fr-tyre"].Condition, Is.EqualTo(1));
        Assert.That(assembly.Installed["fl-damper"].Condition, Is.LessThan(1));
        Assert.That(drive.WheelWear[0], Is.Zero, "Published wear must not be applied a second time.");
    }

    [Test]
    public async Task ClutchAndFirstGearPressedTogetherAllowAPlayerToLaunch()
    {
        EntityUid car = default;
        EntityUid driver = default;
        uint sequence = 0;
        await Server.WaitAssertion(() =>
        {
            Server.CfgMan.SetCVar(CVars.NetTickrate, 30);
            Server.System<SharedMapSystem>().CreateMap(out var mapId);
            var coordinates = new MapCoordinates(Vector2.Zero, mapId);
            car = SEntMan.SpawnEntity("NCToyotaSupra1996", coordinates);
            driver = SEntMan.SpawnEntity("MobHuman", coordinates);
            var containers = Server.System<SharedContainerSystem>();
            Assert.That(containers.TryGetContainer(car, "nc-vehicle-driver", out var seat), Is.True);
            Assert.That(containers.Insert(driver, seat!), Is.True);
            var occupant = SEntMan.AddComponent<NCVehicleOccupantComponent>(driver);
            occupant.Vehicle = car;
            occupant.Seat = "driver";
            var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
            drive.Driver = driver;
            var controls = Server.System<NCVehicleControlSystem>();
            Assert.That(controls.TryControl(car, driver, drive.Authority, NCVehicleDriverAction.Ignition), Is.True);
            Assert.That(drive.Cranking, Is.True, "Ignition must turn the physical starter before combustion begins.");
            Assert.That(drive.EngineRunning, Is.False);
            var input = new NCVehicleInputMessage(drive.Authority, ++sequence, 0, false, false, true, false) { Actor = driver };
            SEntMan.EventBus.RaiseLocalEvent(car, input);
            Assert.That(controls.TryControl(car, driver, drive.Authority, NCVehicleDriverAction.GearLeft), Is.True);
            Assert.That(controls.TryControl(car, driver, drive.Authority, NCVehicleDriverAction.GearUp), Is.True,
                "A shift requested with the clutch pedal must wait for its travel instead of being silently discarded.");
        });
        // An abandoned request must not engage later when the clutch is pressed for another reason.
        await PumpInput(12, false, false);
        await Server.WaitAssertion(() =>
        {
            var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
            Assert.That(drive.PendingShift, Is.Null);
            Assert.That(drive.Gear, Is.Zero);
            var controls = Server.System<NCVehicleControlSystem>();
            Assert.That(controls.TryControl(car, driver, drive.Authority, NCVehicleDriverAction.GearUp), Is.False);
            Assert.That(drive.ShiftStatus, Is.EqualTo(NCVehicleShiftStatus.ClutchRequired));
            var input = new NCVehicleInputMessage(drive.Authority, ++sequence, 0, false, false, true, false) { Actor = driver };
            SEntMan.EventBus.RaiseLocalEvent(car, input);
            Assert.That(controls.TryControl(car, driver, drive.Authority, NCVehicleDriverAction.GearUp), Is.True);
        });
        await PumpInput(12, false, true);
        await Server.WaitAssertion(() => Assert.That(SEntMan.GetComponent<NCVehicleDriveComponent>(car).Gear, Is.EqualTo(1)));
        await PumpInput(30, true, true);
        await PumpInput(60, true, false);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<NCVehicleDriveComponent>(car).EngineRunning, Is.True);
            Assert.That(SEntMan.GetComponent<PhysicsComponent>(car).LinearVelocity.Y, Is.GreaterThan(1));
        });

        async Task PumpInput(int ticks, bool gas, bool clutch)
        {
            for (var i = 0; i < ticks; i += 3)
            {
                await Server.WaitAssertion(() =>
                {
                    var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
                    var input = new NCVehicleInputMessage(drive.Authority, ++sequence, 0, gas, false, clutch, false) { Actor = driver };
                    SEntMan.EventBus.RaiseLocalEvent(car, input);
                });
                await Server.WaitRunTicks(3);
            }
        }
    }

    [Test]
    public async Task RealChassisContactProducesDamageOnBothCars()
    {
        EntityUid first = default;
        EntityUid second = default;
        await Server.WaitAssertion(() =>
        {
            Server.System<SharedMapSystem>().CreateMap(out var mapId);
            first = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(Vector2.Zero, mapId));
            second = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(new Vector2(0, 5), mapId));
            Server.System<SharedPhysicsSystem>().SetLinearVelocity(first, new Vector2(0, 8));
            var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(first);
            Array.Fill(drive.WheelSpeeds, 8 / 0.31f);
        });
        await Server.WaitRunTicks(30);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<NCVehicleBodyComponent>(first).Deformation.Values.Sum(), Is.GreaterThan(0),
                "Damage must be driven by a real physics contact, not only by calling the damage API directly.");
            Assert.That(SEntMan.GetComponent<NCVehicleBodyComponent>(second).Deformation.Values.Sum(), Is.GreaterThan(0));
        });
    }

    [Test]
    public async Task FuelAndPassengerMassChangesAreAppliedOnceToThePhysicalChassis()
    {
        EntityUid car = default;
        EntityUid passenger = default;
        float baseline = 0;
        float payload = 0;
        await Server.WaitAssertion(() =>
        {
            Server.System<SharedMapSystem>().CreateMap(out var mapId);
            var coordinates = new MapCoordinates(Vector2.Zero, mapId);
            car = SEntMan.SpawnEntity("NCToyotaSupra1996", coordinates);
            passenger = SEntMan.SpawnEntity("MobHuman", coordinates);
            var item = SEntMan.SpawnEntity("NCVehicleEngineItem", coordinates);
            Assert.That(Server.System<SharedHandsSystem>().TryPickupAnyHand(passenger, item), Is.True);
            payload = SEntMan.GetComponent<PhysicsComponent>(passenger).Mass + SEntMan.GetComponent<PhysicsComponent>(item).Mass;
            Assert.That(Server.System<NCVehicleAssemblySystem>().TryRebuild(car), Is.True);
            baseline = SEntMan.GetComponent<PhysicsComponent>(car).Mass;
            var assembly = SEntMan.GetComponent<NCVehicleAssemblyComponent>(car);
            Assert.That(baseline, Is.GreaterThan(assembly.Profile.Mass), "Filled reservoirs must add mass to the dry assembly.");
            var containers = Server.System<SharedContainerSystem>();
            Assert.That(containers.TryGetContainer(car, "nc-vehicle-passenger", out var seat), Is.True);
            Assert.That(containers.Insert(passenger, seat!), Is.True);
        });
        await Server.WaitRunTicks(30);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<PhysicsComponent>(car).Mass, Is.EqualTo(baseline + payload).Within(0.1));
            Assert.That(Server.System<NCVehicleAssemblySystem>().TryRebuild(car), Is.True);
            Assert.That(Server.System<NCVehicleAssemblySystem>().TryRebuild(car), Is.True);
            Assert.That(SEntMan.GetComponent<PhysicsComponent>(car).Mass, Is.EqualTo(baseline + payload).Within(0.1),
                "Repeated assembly compilation must not accumulate passenger or liquid mass.");
            var containers = Server.System<SharedContainerSystem>();
            Assert.That(containers.TryGetContainer(car, "nc-vehicle-passenger", out var seat), Is.True);
            Assert.That(containers.Remove(passenger, seat!, force: true), Is.True);
            var solutions = Server.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(car, "fuel", out var tank), Is.True);
            solutions.RemoveReagent(tank!.Value, "NCCHOOH2", FixedPoint2.New(1000));
        });
        await Server.WaitRunTicks(30);
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.GetComponent<PhysicsComponent>(car).Mass, Is.EqualTo(baseline - 0.75f).Within(0.1)));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TowingUsesAnEngineJointAndReturnsCableWhenVehicleIsDeleted()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var first = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(Vector2.Zero, mapId));
        var second = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(new Vector2(0, -6), mapId));
        var user = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(new Vector2(0, -1.5f), mapId));
        var cable = SEntMan.SpawnEntity("NCVehicleTowCable", new MapCoordinates(new Vector2(0, -1.5f), mapId));
        Assert.That(Server.System<SharedHandsSystem>().TryPickupAnyHand(user, cable), Is.True);
        var towing = Server.System<NCVehicleTowSystem>();
        Assert.That(towing.TryUseCable(first, user, cable), Is.True);
        Server.System<SharedTransformSystem>().SetCoordinates(user,
            new EntityCoordinates(Server.System<SharedMapSystem>().GetMap(mapId), new Vector2(0, -4.5f)));
        Assert.That(towing.TryUseCable(second, user, cable), Is.True);
        Assert.That(SEntMan.HasComponent<NCVehicleTowLinkComponent>(first), Is.True);
        Assert.That(SEntMan.HasComponent<NCVehicleTowLinkComponent>(second), Is.True);
        Assert.That(Server.System<SharedHandsSystem>().GetActiveItem(user), Is.Null);
        SEntMan.DeleteEntity(first);
        Assert.That(SEntMan.EntityExists(cable), Is.True);
        Assert.That(SEntMan.HasComponent<NCVehicleTowLinkComponent>(second), Is.False);
        Assert.That(SEntMan.GetComponent<TransformComponent>(cable).MapID, Is.EqualTo(mapId));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void SeatBeltReducesRealMedicalTraumaFromTheSameCrash()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var coordinates = new MapCoordinates(Vector2.Zero, mapId);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", coordinates);
        var belted = SEntMan.SpawnEntity("MobHuman", coordinates);
        var unbelted = SEntMan.SpawnEntity("MobHuman", coordinates);
        var containers = Server.System<SharedContainerSystem>();
        Assert.That(containers.TryGetContainer(car, "nc-vehicle-driver", out var driverSeat), Is.True);
        Assert.That(containers.TryGetContainer(car, "nc-vehicle-passenger", out var passengerSeat), Is.True);
        Assert.That(containers.Insert(belted, driverSeat!), Is.True);
        Assert.That(containers.Insert(unbelted, passengerSeat!), Is.True);
        var first = SEntMan.AddComponent<NCVehicleOccupantComponent>(belted);
        first.Vehicle = car; first.Seat = "driver"; first.Belted = true;
        var second = SEntMan.AddComponent<NCVehicleOccupantComponent>(unbelted);
        second.Vehicle = car; second.Seat = "passenger";
        Assert.That(Server.System<NCVehicleDamageSystem>().TryImpact(car, new Vector2(0, 1.75f), Vector2.UnitY, 100000, 20), Is.True);
        var protectedWounds = SEntMan.GetComponent<NCWoundContainerComponent>(belted).Wounds.Values;
        var exposedWounds = SEntMan.GetComponent<NCWoundContainerComponent>(unbelted).Wounds.Values;
        Assert.That(protectedWounds, Is.Not.Empty);
        Assert.That(exposedWounds.Sum(w => w.IntegrityDamage.Float()),
            Is.GreaterThan(protectedWounds.Sum(w => w.IntegrityDamage.Float())));
    }

    [Test]
    public async Task EngineAcceleratesChassisAndBrakesDissipateMotion()
    {
        EntityUid car = default;
        float accelerated = 0;
        await Server.WaitAssertion(() =>
        {
            Server.CfgMan.SetCVar(CVars.NetTickrate, 30);
            Server.System<SharedMapSystem>().CreateMap(out var mapId);
            car = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(Vector2.Zero, mapId));
            var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
            // Isolate force integration from player input routing, covered separately.
            drive.Driver = SEntMan.SpawnEntity(null, new MapCoordinates(Vector2.Zero, mapId));
            drive.EngineRunning = true;
            drive.EngineRpm = 6000;
            drive.Gear = 1;
            drive.Clutch = 1;
            drive.GasHeld = true;
            drive.Throttle = 1;
            drive.LastInputTime = Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromSeconds(10);
            Server.System<SharedPhysicsSystem>().WakeBody(car);
        });
        for (var phase = 0; phase < 3; phase++)
        {
            await Server.WaitRunTicks(30);
            string sample = string.Empty;
            await Server.WaitAssertion(() => sample = $"Motion: {SEntMan.GetComponent<PhysicsComponent>(car).LinearVelocity}; RPM {SEntMan.GetComponent<NCVehicleDriveComponent>(car).EngineRpm}; running {SEntMan.GetComponent<NCVehicleDriveComponent>(car).EngineRunning}");
            TestContext.Out.WriteLine(sample);
        }
        await Server.WaitAssertion(() =>
        {
            var body = SEntMan.GetComponent<PhysicsComponent>(car);
            accelerated = body.LinearVelocity.Y;
            Assert.That(accelerated, Is.GreaterThan(1), "Combustion must move the real body through tyre forces.");
            Assert.That(float.IsFinite(body.AngularVelocity), Is.True);
            var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
            drive.GasHeld = false;
            drive.ClutchHeld = true;
            drive.BrakeHeld = true;
        });
        await Server.WaitRunTicks(90);
        await Server.WaitAssertion(() =>
        {
            var body = SEntMan.GetComponent<PhysicsComponent>(car);
            Assert.That(body.LinearVelocity.Length(), Is.LessThan(accelerated * 0.2f));
            Assert.That(body.LinearVelocity.Y, Is.GreaterThan(-0.2f), "Brakes must not launch the chassis backwards.");
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void SupraInitializesPhysicalAssembly()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(Vector2.Zero, mapId));
        var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
        Assert.That(Server.System<NCVehicleAssemblySystem>().TryGetProfile(car, out var profile), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(drive.AssemblyReady, Is.True);
            Assert.That(drive.WheelSpeeds.Length, Is.EqualTo(4));
            Assert.That(SEntMan.GetComponent<PhysicsComponent>(car).Mass, Is.EqualTo(profile.Mass).Within(0.1));
            Assert.That(drive.Driver, Is.Null);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void CrumpleZoneAbsorbsSmallImpactBeforeInstalledEngineTakesDamage()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(Vector2.Zero, mapId));
        var damage = Server.System<NCVehicleDamageSystem>();
        var assembly = SEntMan.GetComponent<NCVehicleAssemblyComponent>(car);
        var mass = SEntMan.GetComponent<PhysicsComponent>(car).Mass;
        Assert.That(damage.TryImpact(car, new Vector2(0, 1.75f), Vector2.UnitY, 2000, 0), Is.True);
        Assert.That(assembly.Installed["engine"].Condition, Is.EqualTo(1));
        var shell = SEntMan.GetComponent<NCVehicleBodyComponent>(car);
        Assert.That(shell.Deformation["front-center"], Is.EqualTo(0.02f).Within(0.001));
        Assert.That(shell.Deformation.ContainsKey("rear-center"), Is.False);
        Assert.That(damage.TryImpact(car, new Vector2(0, 1.75f), Vector2.UnitY, 100000, 0), Is.True);
        Assert.That(assembly.Installed["engine"].Condition, Is.LessThan(1).And.GreaterThan(0.9f));
        Assert.That(SEntMan.GetComponent<NCVehicleDriveComponent>(car).AssemblyReady, Is.True);
        Assert.That(damage.TryImpact(car, new Vector2(0, 1.75f), Vector2.UnitY, 400000, 20), Is.True);
        Assert.That(assembly.Installed["engine"].Condition, Is.Zero);
        Assert.That(assembly.Installed["gearbox"].Condition, Is.EqualTo(1));
        Assert.That(SEntMan.GetComponent<PhysicsComponent>(car).Mass, Is.EqualTo(mass).Within(0.01));
        Assert.That(SEntMan.GetComponent<NCVehicleDriveComponent>(car).AssemblyReady, Is.False);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void DeletingCarEvacuatesOccupantBeforeContainerShutdown()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var coordinates = new MapCoordinates(Vector2.Zero, mapId);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", coordinates);
        var occupant = SEntMan.SpawnEntity(null, coordinates);
        var containers = Server.System<SharedContainerSystem>();
        Assert.That(containers.TryGetContainer(car, "nc-vehicle-driver", out var seat), Is.True);
        Assert.That(containers.Insert(occupant, seat!), Is.True);
        SEntMan.GetComponent<NCVehicleDriveComponent>(car).Driver = occupant;
        SEntMan.DeleteEntity(car);
        Assert.That(SEntMan.EntityExists(occupant), Is.True,
            "The engine must not cascade vehicle deletion to the living occupant.");
        Assert.That(SEntMan.GetComponent<TransformComponent>(occupant).MapID, Is.EqualTo(mapId));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void AssemblyRejectsDependentRemovalAndDisablesDisconnectedPowertrain()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(Vector2.Zero, mapId));
        var assemblies = Server.System<NCVehicleAssemblySystem>();
        var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
        var originalCount = SEntMan.GetComponent<NCVehicleAssemblyComponent>(car).Installed.Count;
        Assert.That(assemblies.TrySetPart(car, "engine", null), Is.False,
            "The gearbox still occupies a mount that depends on the engine.");
        Assert.That(assemblies.TrySetPart(car, "gearbox", new NCVehicleInstalledPart("NCVehicleDevelopmentGearbox", 0.42f, false)), Is.True);
        Assert.That(drive.AssemblyReady, Is.False);
        Assert.That(assemblies.TrySetPart(car, "gearbox", null), Is.True);
        Assert.That(assemblies.TrySetPart(car, "engine", null), Is.True);
        Assert.That(assemblies.TryGetProfile(car, out var profile), Is.True);
        Assert.That(profile.RequiredSlotsFilled, Is.False);
        Assert.That(assemblies.TryRebuild(car), Is.True);
        var remaining = SEntMan.GetComponent<NCVehicleAssemblyComponent>(car).Installed;
        Assert.That(remaining.ContainsKey("engine"), Is.False, "Rebuild must not recreate the removed engine.");
        Assert.That(remaining.ContainsKey("gearbox"), Is.False, "Rebuild must not recreate the removed gearbox.");
        Assert.That(remaining.Count, Is.EqualTo(originalCount - 2), "Unrelated installed hardware must remain intact.");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void FuelConsumptionAndDrainingUseTheSameChemicalReservoir()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(Vector2.Zero, mapId));
        var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
        var solutions = Server.System<SharedSolutionContainerSystem>();
        Assert.That(solutions.TryGetSolution(car, "fuel", out var tank), Is.True);
        Assert.That(drive.Fuel, Is.EqualTo(35).Within(0.001));
        var consumed = new NCVehicleFuelConsumedEvent(2);
        SEntMan.EventBus.RaiseLocalEvent(car, ref consumed);
        Assert.That(drive.Fuel, Is.EqualTo(33).Within(0.001));
        solutions.RemoveReagent(tank!.Value, "NCCHOOH2", FixedPoint2.New(1000));
        Assert.That(drive.Fuel, Is.EqualTo(32).Within(0.001));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void EmptyHydraulicsDisableBrakesAndDryEngineWearStaysWithTheModule()
    {
        Server.System<SharedMapSystem>().CreateMap(out var mapId);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(Vector2.Zero, mapId));
        var state = SEntMan.GetComponent<NCVehicleConditionComponent>(car);
        var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
        var solutions = Server.System<SharedSolutionContainerSystem>();
        Assert.That(state.BrakeFluidFraction, Is.EqualTo(1));
        Assert.That(solutions.TryGetSolution(car, "brakes", out var brakes), Is.True);
        solutions.RemoveReagent(brakes!.Value, "NCBrakeFluid", FixedPoint2.New(1000));
        Assert.That(solutions.TryGetSolution(car, "oil", out var oil), Is.True);
        solutions.RemoveReagent(oil!.Value, "NCEngineOil", FixedPoint2.New(5000));
        drive.EngineRunning = true;
        Server.System<NCVehicleConditionSystem>().Advance(car, 0, 1);
        Assert.That(state.BrakePower, Is.Zero);
        var assembly = SEntMan.GetComponent<NCVehicleAssemblyComponent>(car);
        Assert.That(assembly.Installed["engine"].Condition, Is.LessThan(0.998f));
        Assert.That(drive.AssemblyPower, Is.LessThan(1));
        Assert.That(assembly.Installed["gearbox"].Condition, Is.EqualTo(1), "Engine wear must not damage an unrelated gearbox.");
        var destructiveWear = new NCVehicleWearEvent(1, 0);
        SEntMan.EventBus.RaiseLocalEvent(car, ref destructiveWear);
        Assert.That(drive.AssemblyReady, Is.False, "A worn-out required part must revoke ignition permission immediately.");
        Assert.That(drive.EngineRunning, Is.False);
        var assemblies = Server.System<NCVehicleAssemblySystem>();
        Assert.That(assemblies.TrySetPart(car, "gearbox", null), Is.True);
        Assert.That(assemblies.TrySetPart(car, "engine", new NCVehicleInstalledPart("NCVehicleDevelopmentEngine", 1)), Is.True);
        Assert.That(drive.AssemblyPower, Is.EqualTo(1), "A new physical engine must not inherit the removed engine's wear.");
    }
}
