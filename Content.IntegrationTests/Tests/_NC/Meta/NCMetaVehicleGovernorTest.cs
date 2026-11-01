// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Meta.Domains;
using Content.Server._NC.Vehicle;
using Content.Shared._NC.Meta.Domains;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture]
[TestOf(typeof(NCMetaTcuSystem))]
[TestOf(typeof(NCVehicleDriveSystem))]
public sealed class NCMetaVehicleGovernorTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };
    [SidedDependency(Side.Server)] private NCMetaTcuSystem _tcu = default!;
    [SidedDependency(Side.Server)] private NCVehicleAssemblySystem _assembly = default!;
    [SidedDependency(Side.Server)] private IGameTiming _timing = default!;

    private (EntityUid Vehicle, EntityUid Driver) Scene(bool automatic)
    {
        Server.System<SharedMapSystem>().CreateMap(out var map);
        var coordinates = new MapCoordinates(Vector2.Zero, map);
        var vehicle = SEntMan.SpawnEntity("NCMetaSupraEcu", coordinates);
        var driver = SEntMan.SpawnEntity("MobHuman", coordinates);
        if (automatic) Assert.That(_assembly.TrySetPart(vehicle, "gearbox", new NCVehicleInstalledPart("NCVehicleAutomaticGearbox", 1)), Is.True);
        var drive = SComp<NCVehicleDriveComponent>(vehicle);
        drive.Driver = driver;
        drive.Authority = 42;
        drive.Selector = NCVehicleSelector.Drive;
        ResetDrive(vehicle);
        return (vehicle, driver);
    }

    private void ResetDrive(EntityUid vehicle)
    {
        var drive = SComp<NCVehicleDriveComponent>(vehicle);
        var tuning = SProtoMan.Index(drive.Handling);
        drive.Gear = 2;
        drive.NextAutomaticShift = TimeSpan.Zero;
        drive.EngineRpm = (tuning.AutomaticDownshiftRpm + tuning.AutomaticUpshiftRpm) / 2;
    }

    private NCMetaVehicleGovernorComponent Lease(EntityUid vehicle)
    {
        // Arrange an already admitted short lease. These tests exercise the actual native drive
        // consumer and player takeover event; they do not replace runtime grant/admission tests.
        var governor = SEntMan.AddComponent<NCMetaVehicleGovernorComponent>(vehicle);
        governor.TransmissionUntil = _timing.CurTime + TimeSpan.FromSeconds(1);
        governor.TransmissionAuthority = SComp<NCVehicleDriveComponent>(vehicle).Authority;
        governor.RequestedForwardGear = 3;
        governor.MaximumForwardGear = SProtoMan.Index(SComp<NCVehicleDriveComponent>(vehicle).Handling).GearRatios.Length - 2;
        return governor;
    }

    private void Step()
    {
        var step = new PhysicsUpdateBeforeSolveEvent(false, 1f / 60);
        SEntMan.EventBus.RaiseEvent(EventSource.Local, ref step);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ARealGearboxConversionEnablesTheActuatorAndAnAuthorityBoundLeaseSelectsItsGear()
    {
        var (vehicle, _) = Scene(true);
        Assert.That(_tcu.CanBind(vehicle, "meta.vehicle.TransmissionController"), Is.True);
        Lease(vehicle);
        Step();
        Assert.That(SComp<NCVehicleDriveComponent>(vehicle).Gear, Is.EqualTo(3));
    }

    [TestCase(false)]
    [TestCase(true)]
    [RunOnSide(Side.Server)]
    public void ExpiryOrAChangedDriverAuthorityMakesTheNativeGearboxIgnoreTheOldRequest(bool changedAuthority)
    {
        var (vehicle, _) = Scene(true);
        var governor = Lease(vehicle);
        if (changedAuthority) governor.TransmissionAuthority--;
        else governor.TransmissionUntil = _timing.CurTime - TimeSpan.FromMilliseconds(1);
        Step();
        Assert.That(SComp<NCVehicleDriveComponent>(vehicle).Gear, Is.EqualTo(2));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ALeaseCannotOperateTheOriginalMechanicalGearbox()
    {
        var (vehicle, _) = Scene(false);
        Assert.That(_tcu.CanBind(vehicle, "meta.vehicle.TransmissionController"), Is.False);
        Lease(vehicle);
        Step();
        Assert.That(SComp<NCVehicleDriveComponent>(vehicle).Gear, Is.EqualTo(2));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void OnlyTheCurrentPhysicalDriverCanTakeOverAnExistingTransmissionLease()
    {
        var (vehicle, driver) = Scene(true);
        var governor = Lease(vehicle);
        var other = SSpawn("MobHuman");
        SEntMan.EventBus.RaiseLocalEvent(vehicle, new NCVehicleDriverActionMessage(42, NCVehicleDriverAction.Neutral) { Actor = other });
        SEntMan.EventBus.RaiseLocalEvent(vehicle, new NCVehicleDriverActionMessage(41, NCVehicleDriverAction.Neutral) { Actor = driver });
        Assert.That(governor.TransmissionUntil, Is.GreaterThan(_timing.CurTime));
        Assert.That(SComp<NCMetaTcuComponent>(vehicle).ManualOverride, Is.False);
        SEntMan.EventBus.RaiseLocalEvent(vehicle, new NCVehicleDriverActionMessage(42, NCVehicleDriverAction.Neutral) { Actor = driver });
        Assert.That(governor.TransmissionUntil, Is.EqualTo(TimeSpan.Zero));
        Assert.That(governor.RequestedForwardGear, Is.Zero);
        Assert.That(SComp<NCMetaTcuComponent>(vehicle).ManualOverride, Is.True);
    }
}
