// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Meta.Verification;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Meta.Domains;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Lock;
using Content.Shared.PowerCell;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Projectiles;
using Content.Shared.Stealth;
using Content.Shared.Stealth.Components;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.SurveillanceCamera;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture]
[TestOf(typeof(NCMetaDroneOperationsSystem))]
public sealed class NCMetaDroneOperationsTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;
    [SidedDependency(Side.Server)] private NCMetaDroneOperationsSystem _operations = default!;
    [SidedDependency(Side.Server)] private SharedHandsSystem _hands = default!;
    [SidedDependency(Side.Server)] private SharedStorageSystem _storage = default!;
    [SidedDependency(Side.Server)] private SharedGunSystem _guns = default!;
    [SidedDependency(Side.Server)] private SharedStealthSystem _stealth = default!;
    [SidedDependency(Side.Server)] private SharedSurveillanceCameraSystem _cameras = default!;
    [SidedDependency(Side.Server)] private PowerCellSystem _cells = default!;
    [SidedDependency(Side.Server)] private SharedBatterySystem _battery = default!;
    [TestPrototypes]
    private const string Prototypes = """
        - type: entity
          parent: ClothingBackpack
          id: NCMetaTestTinyCargoStorage
          components:
          - type: Storage
            grid:
            - 0,0,0,0
        """;

    private EntityUid Drone(bool equipped = false, bool light = true)
    {
        var mapEntity = _maps.CreateMap(out var map);
        SEntMan.EnsureComponent<MapLightComponent>(mapEntity).AmbientLightColor = light ? Color.White : Color.Black;
        return SEntMan.SpawnEntity(equipped ? "NCMetaEquippedDrone" : "NCMetaCargoDrone", new MapCoordinates(Vector2.Zero, map));
    }
    private EntityUid At(EntityUid drone, string prototype, float x, float y = 0)
    {
        var map = SComp<TransformComponent>(drone).MapID;
        return SEntMan.SpawnEntity(prototype, new MapCoordinates(new Vector2(x, y), map));
    }

    [Test, RunOnSide(Side.Server)]
    public void SensorReturnsOnlyFrozenRelativeVisibleObservationsAndHasARealRateLimit()
    {
        var drone = Drone(); var item = At(drone, "Wirecutter", 1); At(drone, "Wirecutter", -1); At(drone, "Wirecutter", 7);
        Assert.That(_operations.TryObserve(drone, out var observations), Is.True);
        Assert.That(observations.Select(value => value.Target), Is.EquivalentTo(new[] { item }));
        Assert.That(observations[0].RangeMeters, Is.EqualTo(1).Within(0.01));
        Assert.That(observations[0].BearingDegrees, Is.Zero.Within(0.01));
        Assert.That(_operations.TryObserve(drone, out _), Is.False, "The physical camera cannot be sampled twice in one interval.");
    }
    [Test, RunOnSide(Side.Server)]
    public void DarknessCloakingContainersAndAnotherMapDoNotProduceContacts()
    {
        var dark = Drone(light: false); At(dark, "Wirecutter", 1);
        Assert.That(_operations.TryObserve(dark, out var darkReadings), Is.True); Assert.That(darkReadings, Is.Empty);
        var drone = Drone(); var hidden = At(drone, "Wirecutter", 1);
        SEntMan.AddComponent<StealthComponent>(hidden); _stealth.SetVisibility(hidden, -1);
        var cargo = At(drone, "Wirecutter", 1.5f); var storage = At(drone, "ClothingBackpack", 2);
        Assert.That(_storage.Insert(storage, cargo, out _), Is.True);
        var otherMap = Drone(); At(otherMap, "Wirecutter", 1);
        Assert.That(_operations.TryObserve(drone, out var readings), Is.True);
        Assert.That(readings.Any(value => value.Target == hidden || value.Target == cargo), Is.False);
        Assert.That(readings.Count, Is.EqualTo(1), "Only the visible closed backpack is observed, never its contents.");
    }
    [Test, RunOnSide(Side.Server)]
    public void CameraDisableAndEmptyNativeCellFailClosed()
    {
        var drone = Drone(); At(drone, "Wirecutter", 1);
        _cameras.SetActive(drone, false);
        Assert.That(_operations.TryObserve(drone, out _), Is.False);
        _cameras.SetActive(drone, true);
        Assert.That(_cells.TryGetBatteryFromSlot(drone, out var cell), Is.True);
        _battery.SetCharge(cell!.Value.Owner, 0);
        Assert.That(_operations.TryObserve(drone, out _), Is.False);
    }
    [Test]
    public async Task AnOpaqueWallBlocksTheNativeCameraRayOnARealGrid()
    {
        var map = await Pair.CreateTestMap();
        await Server.WaitAssertion(() =>
        {
            var drone = SSpawnAtPosition("NCMetaCargoDrone", map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            SEntMan.EnsureComponent<MapLightComponent>(SComp<TransformComponent>(drone).MapUid!.Value).AmbientLightColor = Color.White;
            SSpawnAtPosition("WallSolid", map.GridCoords.Offset(new Vector2(1.5f, 0.5f)));
            var item = SSpawnAtPosition("Wirecutter", map.GridCoords.Offset(new Vector2(2.5f, 0.5f)));
            Assert.That(_operations.TryObserve(drone, out var observations), Is.True);
            Assert.That(observations.Any(observation => observation.Target == item), Is.False);
        });
    }
    [Test, RunOnSide(Side.Server)]
    public void RealCargoUsesOneGripperAndCannotPickUpAcrossPhysicalRange()
    {
        var drone = Drone(); var item = At(drone, "Wirecutter", 0.5f); var second = At(drone, "Wirecutter", 0.5f); var far = At(drone, "Wirecutter", 5);
        Assert.That(_operations.TryPickUp(drone, far, () => true), Is.False);
        Assert.That(_operations.TryPickUp(drone, item, () => true), Is.True);
        Assert.That(_operations.TryPickUp(drone, second, () => true), Is.False);
        Assert.That(_hands.GetHeldItem(drone, "gripper"), Is.EqualTo(item));
        Assert.That(_operations.TryDropAt(drone, item, far, () => true), Is.False);
        var point = At(drone, "NCMetaWaypointA", 0.5f);
        Assert.That(_operations.TryDropAt(drone, item, point, () => true), Is.True);
        Assert.That(_hands.IsHolding(drone, item), Is.False);
        Assert.That(SComp<TransformComponent>(item).Coordinates.TryDistance(SEntMan, SComp<TransformComponent>(point).Coordinates, out var distance), Is.True);
        Assert.That(distance, Is.LessThan(0.1));
    }
    [Test, RunOnSide(Side.Server)]
    public void ManualTakeoverAndARevokedFinalGuardDoNotMoveThePhysicalCargo()
    {
        var drone = Drone(); var item = At(drone, "Wirecutter", 0.5f); var checks = 0;
        Assert.That(_operations.TryPickUp(drone, item, () => ++checks == 1), Is.False);
        Assert.That(checks, Is.GreaterThanOrEqualTo(2)); Assert.That(_hands.IsHolding(drone, item), Is.False);
        SComp<NCMetaDroneComponent>(drone).ManualOverride = true;
        Assert.That(_operations.TryPickUp(drone, item, () => true), Is.False);
    }
    [Test, RunOnSide(Side.Server)]
    public void StoreInMovesTheSameItemThroughNativeStorageAndRevocationLeavesItInTheHand()
    {
        var drone = Drone(); var item = At(drone, "Wirecutter", 0.5f); var target = At(drone, "ClothingBackpack", 1);
        Assert.That(_operations.TryPickUp(drone, item, () => true), Is.True);
        Assert.That(_operations.TryStoreIn(drone, item, target, () => false), Is.False);
        Assert.That(_hands.IsHolding(drone, item), Is.True);
        Assert.That(_operations.TryStoreIn(drone, item, target, () => true), Is.True);
        Assert.That(_hands.IsHolding(drone, item), Is.False);
        Assert.That(SComp<Content.Shared.Storage.StorageComponent>(target).Container.Contains(item), Is.True);
    }
    [Test, RunOnSide(Side.Server)]
    public void LockedAndPhysicallyFullStorageLeaveCargoInTheActualGripper()
    {
        var drone = Drone(); var item = At(drone, "Wirecutter", 0.5f); var locked = At(drone, "ClothingBackpack", 1);
        SEntMan.AddComponent<LockComponent>(locked); SEntMan.AddComponent<LockedStorageComponent>(locked); Server.System<LockSystem>().Lock(locked, null);
        var tiny = At(drone, "NCMetaTestTinyCargoStorage", 1);
        Assert.That(_operations.TryPickUp(drone, item, () => true), Is.True);
        Assert.That(_operations.TryStoreIn(drone, item, locked, () => true), Is.False);
        Assert.That(_operations.TryStoreIn(drone, item, tiny, () => true), Is.False);
        Assert.That(_hands.GetHeldItem(drone, "gripper"), Is.EqualTo(item));
    }
    [Test, RunOnSide(Side.Server)]
    public void ControlledNativeTriggerDebitsOneCartridgeAndEmitsARealProjectileWithNoBackgroundTarget()
    {
        var drone = Drone(equipped: true); var target = At(drone, "MobHuman", 3);
        var count = _guns.GetAmmoCount(drone); Assert.That(count, Is.EqualTo(6));
        var before = Projectiles();
        Assert.That(_guns.TryFireControlled(drone, drone, SComp<TransformComponent>(target).Coordinates, target, () => true), Is.True);
        Assert.That(_guns.GetAmmoCount(drone), Is.EqualTo(count - 1));
        Assert.That(Projectiles(), Is.GreaterThan(before));
        Assert.That(_guns.HasPendingControlledShot(drone), Is.False);
        Assert.That(_guns.TryFireControlled(drone, drone, SComp<TransformComponent>(target).Coordinates, target, () => true), Is.False);
        Assert.That(_guns.GetAmmoCount(drone), Is.EqualTo(count - 1), "Native cooldown prevents a second trigger.");
    }
    [Test, RunOnSide(Side.Server)]
    public void NativeFinalShotGuardRunsBeforeTheAmmoDebitAndClearsTargetEvenOnRejection()
    {
        var drone = Drone(equipped: true); var target = At(drone, "MobHuman", 3); var count = _guns.GetAmmoCount(drone); var checks = 0;
        Assert.That(_guns.TryFireControlled(drone, drone, SComp<TransformComponent>(target).Coordinates, target, () => ++checks == 1), Is.False);
        Assert.That(checks, Is.GreaterThanOrEqualTo(2));
        Assert.That(_guns.GetAmmoCount(drone), Is.EqualTo(count));
        Assert.That(_guns.HasPendingControlledShot(drone), Is.False);
    }
    [Test, RunOnSide(Side.Server)]
    public void AnEmptyPhysicalAmmoProviderCannotProduceAControlledShot()
    {
        var drone = Drone(equipped: true); var target = At(drone, "MobHuman", 3);
        _guns.SetBallisticUnspawned((drone, SComp<BallisticAmmoProviderComponent>(drone)), 0);
        var before = Projectiles();
        Assert.That(_guns.TryFireControlled(drone, drone, SComp<TransformComponent>(target).Coordinates, target, () => true), Is.False);
        Assert.That(Projectiles(), Is.EqualTo(before)); Assert.That(_guns.GetAmmoCount(drone), Is.Zero);
        Assert.That(_guns.HasPendingControlledShot(drone), Is.False);
    }
    [Test, RunOnSide(Side.Server)]
    public void RevocationAfterAmmoAdmissionPreventsProjectileOutputWithoutInventingAnAmmoRefund()
    {
        var drone = Drone(equipped: true); var target = At(drone, "MobHuman", 3); var count = _guns.GetAmmoCount(drone); var before = Projectiles(); var checks = 0;
        _guns.TryFireControlled(drone, drone, SComp<TransformComponent>(target).Coordinates, target, () => ++checks < 3);
        Assert.That(checks, Is.GreaterThanOrEqualTo(3)); Assert.That(_guns.GetAmmoCount(drone), Is.EqualTo(count - 1));
        Assert.That(Projectiles(), Is.EqualTo(before), "Ammo debit alone is not proof of a native projectile output.");
        Assert.That(_guns.HasPendingControlledShot(drone), Is.False);
    }
    [Test, RunOnSide(Side.Server)]
    public void OpaqueObservationRejectsForeignProcessRuntimeDeviceAndChangedNativeEpoch()
    {
        var drone = Drone(); var item = At(drone, "Wirecutter", 1); var process = Process(); var generation = Guid.NewGuid();
        Assert.That(_operations.TryCaptureObservationReferences(drone, generation, process, out var values, out var error), Is.True, error);
        Assert.That(values, Has.Length.EqualTo(1)); var reference = process.GetField(values[0], 0);
        Assert.That(_operations.TryResolveObservation(drone, generation, process, reference, out var target), Is.True);
        Assert.That(target, Is.EqualTo(item));
        Assert.That(_operations.TryResolveObservation(drone, Guid.NewGuid(), process, reference, out _), Is.False);
        Assert.That(_operations.TryResolveObservation(Drone(), generation, process, reference, out _), Is.False);
        var foreign = Process(); Assert.That(process.TryGetOpaqueToken(reference, out var token), Is.True);
        var forged = foreign.CreateOpaque(foreign.GetTypeId("meta.drone.ContactRef"), token);
        Assert.That(_operations.TryResolveObservation(drone, generation, foreign, forged, out _), Is.False);
        SComp<NCMetaDroneComponent>(drone).ControlEpoch++;
        Assert.That(_operations.TryResolveObservation(drone, generation, process, reference, out _), Is.False);
    }
    [Test]
    public async Task ObservationCoordinatesStayFrozenAndTheOpaqueReferenceExpiresInsteadOfTrackingAFuturePosition()
    {
        EntityUid drone = default; MetaProcess process = default!; MetaValue reference = default; Guid generation = Guid.NewGuid();
        await Server.WaitAssertion(() =>
        {
            drone = Drone(); var item = At(drone, "Wirecutter", 1); process = Process();
            Assert.That(_operations.TryCaptureObservationReferences(drone, generation, process, out var values, out var error), Is.True, error);
            reference = process.GetField(values.Single(), 0); var measuredDistance = process.GetField(values[0], 3).AsFloat();
            var map = SComp<TransformComponent>(drone).MapUid!.Value;
            Server.System<SharedTransformSystem>().SetCoordinates(item, new EntityCoordinates(map, new Vector2(2, 0)));
            Assert.That(_operations.TryResolveObservation(drone, generation, process, reference, out var target), Is.True);
            Assert.That(target, Is.EqualTo(item));
            Assert.That(process.GetField(values[0], 3).AsFloat(), Is.EqualTo(measuredDistance));
            Assert.That(measuredDistance, Is.EqualTo(1).Within(0.01));
        });
        await Server.WaitRunTicks(180);
        await Server.WaitAssertion(() => Assert.That(_operations.TryResolveObservation(drone, generation, process, reference, out _), Is.False));
    }
    private static MetaProcess Process()
    {
        var compiled = new MetaCompiler().Compile(new([new("contact.meta", "module test.contacts;import meta.drone;fn main()->void{}")], "test.contacts"));
        Assert.That(compiled.Success, Is.True);
        return new(MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!)));
    }
    private int Projectiles()
    {
        var count = 0; var query = SEntMan.AllEntityQueryEnumerator<ProjectileComponent>();
        while (query.MoveNext(out _, out _)) count++;
        return count;
    }
}
