// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Linq;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Vehicle;
using Content.Server.Atmos.EntitySystems;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Atmos.Components;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._NC.Vehicle;

public sealed partial class NCVehicleLifecycleTest
{
    [Test]
    public async Task LowGripSurfaceProducesALongerPhysicalBrakingDistance()
    {
        var cars = new EntityUid[2];
        await Server.WaitAssertion(() =>
        {
            Server.CfgMan.SetCVar(Robust.Shared.CVars.NetTickrate, 30);
            for (var lane = 0; lane < cars.Length; lane++)
            {
                var mapUid = Server.System<SharedMapSystem>().CreateMap(out var map);
                var surface = SEntMan.AddComponent<Content.Shared.Friction.TileFrictionModifierComponent>(mapUid);
                Server.System<Content.Shared.Friction.TileFrictionController>().SetModifier(mapUid, lane == 0 ? 1 : 0.15f, surface);
                var car = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(Vector2.Zero, map));
                cars[lane] = car;
                var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
                drive.Driver = SEntMan.SpawnEntity(null, new MapCoordinates(Vector2.Zero, map));
                drive.BrakeHeld = true;
                drive.Brake = 1;
                drive.ClutchHeld = true;
                drive.LastInputTime = Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromSeconds(10);
                Array.Fill(drive.WheelSpeeds, 12 / 0.31f);
                Server.System<SharedPhysicsSystem>().SetLinearVelocity(car, new Vector2(0, 12));
            }
        });
        await Server.WaitRunTicks(60);
        await Server.WaitAssertion(() =>
        {
            var transform = Server.System<SharedTransformSystem>();
            Assert.That(transform.GetWorldPosition(cars[1]).Y, Is.GreaterThan(transform.GetWorldPosition(cars[0]).Y + 1),
                "Surface friction must affect chassis stopping distance, not only a dashboard value.");
            Assert.That(SEntMan.GetComponent<PhysicsComponent>(cars[1]).LinearVelocity.Y, Is.GreaterThan(-0.1f));
        });
    }

    [Test]
    public async Task LateCommandsCannotRestoreThrottleAfterDriverAuthorityChanges()
    {
        EntityUid car = default;
        EntityUid driver = default;
        uint authority = 0;
        await Server.WaitAssertion(() =>
        {
            Server.System<SharedMapSystem>().CreateMap(out var map);
            car = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(Vector2.Zero, map));
            driver = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map));
            var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
            drive.Driver = driver;
            authority = drive.Authority;
            var newest = new NCVehicleInputMessage(authority, 20, 0.5f, true, false, false, false) { Actor = driver };
            SEntMan.EventBus.RaiseLocalEvent(car, newest);
            var delayed = new NCVehicleInputMessage(authority, 19, -1, false, true, false, false) { Actor = driver };
            SEntMan.EventBus.RaiseLocalEvent(car, delayed);
            Assert.That(drive.GasHeld, Is.True);
            Assert.That(drive.SteeringTarget, Is.EqualTo(0.5f));
            drive.Authority++;
            drive.GasHeld = false;
            var oldDriver = new NCVehicleInputMessage(authority, 21, 1, true, false, false, false) { Actor = driver };
            SEntMan.EventBus.RaiseLocalEvent(car, oldDriver);
            Assert.That(drive.GasHeld, Is.False);
            var current = new NCVehicleInputMessage(drive.Authority, 22, 0, true, false, false, false) { Actor = driver };
            SEntMan.EventBus.RaiseLocalEvent(car, current);
            Assert.That(drive.GasHeld, Is.True);
            drive.LastInputTime = Server.ResolveDependency<IGameTiming>().CurTime - TimeSpan.FromSeconds(10);
        });
        await Server.WaitRunTicks(2);
        await Server.WaitAssertion(() => Assert.That(SEntMan.GetComponent<NCVehicleDriveComponent>(car).GasHeld, Is.False,
            "Loss of input must release pedals instead of leaving a runaway throttle."));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void CabinPenetrationExtinguishingAndDeadBatterySwitchesUseRealSystems()
    {
        Server.System<SharedMapSystem>().CreateMap(out var map);
        var car = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(Vector2.Zero, map));
        var driver = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map));
        var containers = Server.System<SharedContainerSystem>();
        Assert.That(containers.TryGetContainer(car, "nc-vehicle-driver", out var seat), Is.True);
        Assert.That(containers.Insert(driver, seat!), Is.True);
        SEntMan.GetComponent<NCVehicleDriveComponent>(car).Driver = driver;
        var hit = new DamageSpecifier();
        hit.DamageDict["Piercing"] = FixedPoint2.New(30);
        Server.System<DamageableSystem>().ChangeDamage(car, hit);
        Assert.That(SEntMan.GetComponent<NCWoundContainerComponent>(driver).Wounds, Is.Not.Empty,
            "Penetrating cabin damage must reach the existing medical wound system.");
        var accessories = Server.System<NCVehicleAccessorySystem>();
        Assert.That(accessories.TryUse(car, driver, NCVehicleDriverAction.Lights), Is.True);
        var condition = SEntMan.GetComponent<NCVehicleConditionComponent>(car);
        condition.BatteryCharge = 0;
        Assert.That(accessories.TryUse(car, driver, NCVehicleDriverAction.Lights), Is.True);
        Assert.That(accessories.TryUse(car, driver, NCVehicleDriverAction.Horn), Is.False);
        Assert.That(SEntMan.GetComponent<NCVehicleAccessoriesComponent>(car).Headlights, Is.False);
        var body = SEntMan.GetComponent<NCVehicleBodyComponent>(car);
        condition.EngineTemperature = body.FireIgnitionTemperature;
        var pulse = new NCVehicleMaintenanceEvent(1);
        SEntMan.EventBus.RaiseLocalEvent(car, ref pulse);
        Assert.That(SEntMan.GetComponent<FlammableComponent>(car).OnFire, Is.True);
        condition.EngineTemperature = condition.AmbientTemperature;
        Server.System<FlammableSystem>().Extinguish(car);
        SEntMan.EventBus.RaiseLocalEvent(car, ref pulse);
        Assert.That(body.Burning, Is.False);
    }

    [Test]
    public async Task SimultaneousPhysicalCrashesAndPedestrianContactRemainFinite()
    {
        var cars = new System.Collections.Generic.List<EntityUid>();
        EntityUid pedestrian = default;
        await Server.WaitAssertion(() =>
        {
            Server.System<SharedMapSystem>().CreateMap(out var map);
            var physics = Server.System<SharedPhysicsSystem>();
            // Independent lanes create simultaneous actual contacts without artificial damage calls.
            for (var lane = 0; lane < 12; lane++)
            {
                var moving = SEntMan.SpawnEntity("NCToyotaSupra1996", new MapCoordinates(new Vector2(lane * 8, 0), map));
                var target = SEntMan.SpawnEntity(lane == 0 ? "MobHuman" : "NCToyotaSupra1996",
                    new MapCoordinates(new Vector2(lane * 8, 5), map));
                if (lane == 0)
                    pedestrian = target;
                else
                    cars.Add(target);
                cars.Add(moving);
                physics.SetLinearVelocity(moving, new Vector2(0, 10));
                Array.Fill(SEntMan.GetComponent<NCVehicleDriveComponent>(moving).WheelSpeeds, 10 / 0.31f);
            }
        });
        await Server.WaitRunTicks(90);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<NCWoundContainerComponent>(pedestrian).Wounds, Is.Not.Empty);
            foreach (var car in cars)
            {
                var body = SEntMan.GetComponent<PhysicsComponent>(car);
                Assert.That(float.IsFinite(body.LinearVelocity.X) && float.IsFinite(body.LinearVelocity.Y) &&
                    float.IsFinite(body.AngularVelocity), Is.True);
                Assert.That(SEntMan.GetComponent<NCVehicleBodyComponent>(car).Deformation.Values.Sum(), Is.GreaterThan(0));
            }
        });
    }
}
