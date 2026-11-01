// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Threading.Tasks;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Meta.Domains;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Diagnostics.Detail;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Domains;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared.Conveyor;
using Content.Shared.DeviceLinking.Events;
using Content.Shared.Movement.Components;
using Content.Shared.PowerCell.Components;
using Content.Shared.PowerCell;
using Content.Shared.Power.EntitySystems;
using Content.Server._NC.Meta.Devices;
using Robust.Shared.Physics.Systems;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture]
[TestOf(typeof(NCMetaClinicalSystem))]
[TestOf(typeof(NCMetaEcuSystem))]
[TestOf(typeof(NCMetaDroneSystem))]
public sealed class NCMetaDomainHardwareTest : GameTest
{
    [SidedDependency(Side.Server)] private NCMetaClinicalSystem _clinical = default!;
    [SidedDependency(Side.Server)] private NCMetaEcuSystem _ecu = default!;
    [SidedDependency(Side.Server)] private NCMetaDroneSystem _drone = default!;
    [SidedDependency(Side.Server)] private NCMetaOpticsSystem _optics = default!;
    [SidedDependency(Side.Server)] private NCMetaTcuSystem _transmission = default!;
    [SidedDependency(Side.Server)] private NCMetaIndustrialSystem _industrial = default!;
    [SidedDependency(Side.Server)] private NCMetaDeviceRegistrySystem _devices = default!;
    [SidedDependency(Side.Server)] private PowerCellSystem _cells = default!;
    [SidedDependency(Side.Server)] private SharedBatterySystem _battery = default!;
    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test]
    public async Task CompatibleVariantsContainRealNativeHardwareAndStartWithSafeDefaults()
    {
        var map = await Pair.CreateTestMap();
        await Server.WaitAssertion(() => CheckCompatibleVariants(map.GridCoords));
    }

    private void CheckCompatibleVariants(Robust.Shared.Map.EntityCoordinates coordinates)
    {
        var vehicle = SSpawn("NCMetaSupraEcu");
        var drone = SSpawn("NCMetaPatrolDrone");
        var conveyor = SSpawnAtPosition("NCMetaConveyor", coordinates);
        var bedside = SSpawn("NCMetaClinicalController");
        var optics = SSpawn("NCMetaCyberOptics");
        Assert.Multiple(() =>
        {
            Assert.That(SEntMan.HasComponent<NCVehicleDriveComponent>(vehicle), Is.True);
            Assert.That(SEntMan.HasComponent<NCVehicleConditionComponent>(vehicle), Is.True);
            Assert.That(_ecu.CanBind(vehicle, "meta.vehicle.EngineController"), Is.True);
            Assert.That(SEntMan.HasComponent<NCMetaVehicleGovernorComponent>(vehicle), Is.False, "Guest torque control is not installed before a real command.");
            Assert.That(SEntMan.HasComponent<InputMoverComponent>(drone), Is.True);
            Assert.That(SEntMan.HasComponent<PowerCellSlotComponent>(drone), Is.True);
            Assert.That(_drone.CanBind(drone, "meta.drone.DroneController"), Is.True);
            Assert.That(SComp<NCMetaDroneComponent>(drone).Waypoints, Is.Empty);
            Assert.That(SEntMan.HasComponent<ConveyorComponent>(conveyor), Is.True);
            Assert.That(SComp<NCMetaConveyorComponent>(conveyor).Controller, Is.Null);
            Assert.That(SEntMan.HasComponent<NCMetaLoadSensorComponent>(conveyor), Is.True);
            Assert.That(SComp<NCMetaClinicalComponent>(bedside).Patient, Is.Null);
            Assert.That(SComp<NCMetaClinicalComponent>(bedside).AutomationEnabled, Is.False);
            Assert.That(SEntMan.HasComponent<NCDiagnosticCapabilityComponent>(bedside), Is.True);
            Assert.That(SEntMan.HasComponent<NCCyberwareComponent>(optics), Is.True);
            Assert.That(_optics.CanBind(optics, "meta.implants.Optics"), Is.False, "A detached organ has no installed native power or patient authority.");
            Assert.That(SEntMan.HasComponent<NCMetaTerminalComponent>(bedside), Is.True);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void UnownedClinicalHardwareCannotBindPatientOrEnableDosing()
    {
        var bedside = SSpawn("NCMetaClinicalController"); var actor = SSpawn("MobHuman"); var patient = SSpawn("MobHuman");
        var before = SComp<NCMetaClinicalComponent>(bedside).PatientEpoch;
        Assert.That(_clinical.TryBindPatient(bedside, actor, patient), Is.False);
        Assert.That(_clinical.TrySetAutomation(bedside, actor, true), Is.False);
        Assert.That(SComp<NCMetaClinicalComponent>(bedside).PatientEpoch, Is.EqualTo(before));
        Assert.That(SComp<NCMetaClinicalComponent>(bedside).Patient, Is.Null);
        Assert.That(SComp<NCMetaClinicalComponent>(bedside).AutomationEnabled, Is.False);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void MarkerAloneCannotInventDriveSteeringOrImplantCapabilities()
    {
        var item = SSpawn("NCMetaCommissioningTool");
        Assert.That(_ecu.CanBind(item, "meta.vehicle.EngineController"), Is.False);
        Assert.That(_drone.CanBind(item, "meta.drone.DroneController"), Is.False);
        Assert.That(_optics.CanBind(item, "meta.implants.Optics"), Is.False);
        Assert.That(_clinical.CanBind(item, "meta.medical.DosingController"), Is.False);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TcuDoesNotTurnMechanicalGearboxIntoAnElectronicActuator()
    {
        var vehicle = SSpawn("NCMetaSupraEcu"); var user = SSpawn("MobHuman");
        Assert.That(SComp<NCMetaTcuComponent>(vehicle).ElectronicActuator, Is.True);
        Assert.That(_transmission.CanBind(vehicle, "meta.vehicle.TransmissionController"), Is.False,
            "The actual native transmission is manual until converted through the existing workshop.");
        Assert.That(_transmission.TrySetManualControl(vehicle, user, false), Is.False,
            "A nearby actor does not manufacture the current physical driver authority.");
        Assert.That(SEntMan.HasComponent<NCMetaVehicleGovernorComponent>(vehicle), Is.False);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void PortableNativeBatteryGateRejectsEmptyCellEvenWithoutApcReceiver()
    {
        var drone = SSpawn("NCMetaPatrolDrone");
        Assert.That(_cells.TryGetBatteryFromSlot(drone, out var cell), Is.True);
        Assert.That(_devices.IsPowered(drone), Is.True);
        _battery.ChangeCharge(cell!.Value.Owner, -100000f);
        Assert.That(_devices.IsPowered(drone), Is.False,
            "The generic absence of an APC receiver cannot provide power to a physical battery-powered host.");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void DroneFirmwareRequiresMaintenanceAndAStationaryNativePhysicsBody()
    {
        var drone = SSpawn("NCMetaPatrolDrone");
        Assert.That(_drone.CanServiceFirmware(drone, out _), Is.False);
        SComp<NCMetaDroneComponent>(drone).Mode = NCMetaDroneMode.Maintenance;
        Assert.That(_drone.CanServiceFirmware(drone, out var epoch), Is.True);
        Server.System<SharedPhysicsSystem>().SetLinearVelocity(drone, Vector2.UnitX);
        Assert.That(_drone.CanServiceFirmware(drone, out _), Is.False);
        Server.System<SharedPhysicsSystem>().SetLinearVelocity(drone, Vector2.Zero);
        Assert.That(_drone.CanServiceFirmware(drone, out var current), Is.True);
        Assert.That(current, Is.EqualTo(epoch));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ClinicalFirmwareCannotBeServicedWithAutomationOrAnAttachedPatient()
    {
        var bedside = SSpawn("NCMetaClinicalController");
        var context = SComp<NCMetaClinicalComponent>(bedside);
        Assert.That(_clinical.CanServiceFirmware(bedside, out _), Is.True);
        context.AutomationEnabled = true;
        Assert.That(_clinical.CanServiceFirmware(bedside, out _), Is.False);
        context.AutomationEnabled = false;
        context.Patient = SSpawn("MobHuman");
        Assert.That(_clinical.CanServiceFirmware(bedside, out _), Is.False);
    }

    [Test]
    public async Task APhysicalConveyorSignalInvalidatesTheCapturedFirmwareServiceRevision()
    {
        var map = await Pair.CreateTestMap();
        await Server.WaitAssertion(() =>
        {
            var uid = SSpawnAtPosition("NCMetaConveyor", map.GridCoords);
            var belt = SComp<NCMetaConveyorComponent>(uid);
            var native = SComp<ConveyorComponent>(uid);
            belt.ManualOverride = true;
            Assert.That(_industrial.CanServiceFirmware(uid, out var previous), Is.True);
            var off = new SignalReceivedEvent(native.OffPort.Id);
            SEntMan.EventBus.RaiseLocalEvent(uid, ref off);
            Assert.That(_industrial.CanServiceFirmware(uid, out var current), Is.True);
            Assert.That(current, Is.GreaterThan(previous), "Even a same-state external signal invalidates an asynchronous service stamp.");
        });
    }
}
