// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.IntegrationTests.Tests._NC.Medical;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Cyberware.Abilities;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Domains;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Abilities;
using Content.Shared.Body;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture]
[TestOf(typeof(NCCyberwareAbilitySystem))]
public sealed class NCMetaFunctionalImplantTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };
    [SidedDependency(Side.Server)] private NCCyberwareAbilitySystem _abilities = default!;
    [SidedDependency(Side.Server)] private NCCyberwareSystem _cyberware = default!;
    [SidedDependency(Side.Server)] private SharedHandsSystem _hands = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _slots = default!;
    [SidedDependency(Side.Server)] private SharedBatterySystem _battery = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;
    [SidedDependency(Side.Server)] private IGameTiming _timing = default!;
    [SidedDependency(Side.Server)] private NCMetaFunctionalImplantSystem _functions = default!;
    [SidedDependency(Side.Server)] private NCMetaDeviceRegistrySystem _devices = default!;

    [Test, RunOnSide(Side.Server)]
    public void RepeatedDesiredOnDoesNotRestartTheBurstOrChargeStartupTwice()
    {
        var (body, cell) = Scene();
        var module = Install(body, "NCSandevistanImplant", "Head", "NCSlotMotorBurst");
        Assert.That(_abilities.TrySetEnabled(body, module, true), Is.True);
        var until = SComp<NCCyberwareAbilityComponent>(module).ActiveUntil;
        var charge = _battery.GetCharge(cell);
        Assert.That(_abilities.TrySetEnabled(body, module, true), Is.True);
        Assert.That(SComp<NCCyberwareAbilityComponent>(module).ActiveUntil, Is.EqualTo(until));
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(charge));
        Assert.That(_abilities.TrySetEnabled(body, module, false), Is.True);
        Assert.That(_abilities.TrySetEnabled(body, module, false), Is.True);
        Assert.That(_abilities.TrySetEnabled(body, module, true), Is.False, "The native cooldown survives explicit off.");
    }

    [Test, RunOnSide(Side.Server)]
    public void ForeignBodyAndRevokedGuardCannotEnableOrDisableNativeHardware()
    {
        var (body, cell) = Scene(); var (other, _) = Scene();
        var module = Install(body, "NCSandevistanImplant", "Head", "NCSlotMotorBurst");
        var charge = _battery.GetCharge(cell);
        Assert.That(_abilities.TrySetEnabled(other, module, true), Is.False);
        Assert.That(_abilities.TrySetEnabled(body, module, true, () => false), Is.False);
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(charge));
        Assert.That(_abilities.TrySetEnabled(body, module, true), Is.True);
        Assert.That(_abilities.TrySetEnabled(body, module, false, () => false), Is.False);
        Assert.That(SComp<NCCyberwareAbilityComponent>(module).Active, Is.True);
    }

    [Test, RunOnSide(Side.Server)]
    public void NearbyForeignActorAndDetachedModuleCannotPassTheNativeClaimBoundary()
    {
        var (body, _) = Scene();
        var other = SSpawnAtPosition("MobHuman", SComp<TransformComponent>(body).Coordinates);
        var module = Install(body, "NCSandevistanImplant", "Head", "NCSlotMotorBurst");
        SEntMan.AddComponent<NCMetaFunctionalImplantComponent>(module);
        Assert.That(_functions.CanClaimInstalledModule(module, body), Is.True);
        Assert.That(_functions.CanClaimInstalledModule(module, other), Is.False);
        Assert.That(_devices.CanClaim(module, other), Is.False);
        Assert.That(_devices.TryClaim(module, other), Is.False);
        Assert.That(_cyberware.TryRemove(new(NCActionResolutionId.New(), body, module)).Accepted, Is.True);
        Assert.That(_functions.CanClaimInstalledModule(module, body), Is.False);
        Assert.That(_devices.CanClaim(module, body), Is.False);
        Assert.That(_devices.TryClaim(module, body), Is.False);
    }

    [Test, RunOnSide(Side.Server)]
    public void NativeManualOffIsNotTurnedIntoOnByTheAutomationNotification()
    {
        var (body, _) = Scene();
        var module = Install(body, "NCKerenzikovImplant", "Head", "NCSlotMotorStabilizer");
        Assert.That(_abilities.TrySetEnabled(body, module, true), Is.True);
        var notified = 0;
        void OnManual(EntityUid uid)
        {
            if (uid != module) return;
            notified++;
            Assert.That(SComp<NCCyberwareAbilityComponent>(module).Active, Is.True);
        }
        _abilities.ManualControlRequested += OnManual;
        try
        {
            var action = new NCActivateCyberwareAbilityEvent { Performer = body };
            SEntMan.EventBus.RaiseLocalEvent(module, ref action);
            Assert.That(action.Handled, Is.True);
            Assert.That(notified, Is.EqualTo(1));
            Assert.That(SComp<NCCyberwareAbilityComponent>(module).Active, Is.False);
            Assert.That(SComp<NCCyberwareAbilityBodyComponent>(body).MovingPenaltyReduction, Is.Zero);
        }
        finally { _abilities.ManualControlRequested -= OnManual; }
    }

    [Test, RunOnSide(Side.Server)]
    public void ToolDeploymentKeepsThePhysicalHandAndPowerInterlocks()
    {
        var (body, cell) = Scene();
        var module = Install(body, "NCToolHandImplant", "ArmRight", "NCSlotArmUtilityRight");
        var occupied = SSpawnAtPosition("Screwdriver", SComp<TransformComponent>(body).Coordinates);
        var hand = SComp<HandsComponent>(body).Hands.Single(pair => pair.Value.Location == HandLocation.Right).Key;
        Assert.That(_hands.TryPickup(body, occupied, hand), Is.True);
        Assert.That(_abilities.TrySetEnabled(body, module, true), Is.False);
        Assert.That(_hands.IsHolding(body, occupied), Is.True);
        Assert.That(_hands.TryDrop(body, occupied), Is.True);
        Assert.That(_abilities.TrySetEnabled(body, module, true), Is.True);
        var tool = SComp<NCCyberwareAbilityComponent>(module).IntegralItem!.Value;
        Assert.That(_hands.IsHolding(body, tool), Is.True);
        _battery.SetCharge(cell, 0);
        Assert.That(_abilities.TrySetEnabled(body, module, true), Is.False);
        Assert.That(_hands.IsHolding(body, tool), Is.False);
    }

    [Test, RunOnSide(Side.Server)]
    public void RevocationDuringNativeToolDeploymentPreventsStartupDebitAndRetractsTheTool()
    {
        var (body, cell) = Scene();
        var module = Install(body, "NCToolHandImplant", "ArmRight", "NCSlotArmUtilityRight");
        var before = _battery.GetCharge(cell); var checks = 0;
        Assert.That(_abilities.TrySetEnabled(body, module, true, () => ++checks < 4), Is.False);
        var item = SComp<NCCyberwareAbilityComponent>(module).IntegralItem;
        Assert.That(item, Is.Not.Null, "The guard is revoked after native physical deployment, immediately before startup energy.");
        Assert.That(_hands.IsHolding(body, item!.Value), Is.False);
        Assert.That(SComp<NCCyberwareAbilityComponent>(module).Active, Is.False);
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(before));
    }

    [Test, RunOnSide(Side.Server)]
    public void ComputeDemandSurvivesNativeModeStopAndDebitsTheRealBodyBattery()
    {
        var (body, cell) = Scene();
        var module = Install(body, "NCSandevistanImplant", "Head", "NCSlotMotorBurst");
        Assert.That(_cyberware.TrySetComputeLoad(module, 0.4f), Is.True);
        Assert.That(_abilities.TrySetEnabled(body, module, true), Is.True);
        Assert.That(_abilities.TrySetEnabled(body, module, false), Is.True);
        Assert.That(SComp<NCCyberwareComponent>(module).Load, Is.Zero);
        Assert.That(SComp<NCCyberwareComponent>(module).ComputeLoad, Is.EqualTo(0.4f));
        var before = _battery.GetCharge(cell);
        Assert.That(_cyberware.TryAdvanceBodyPower(body, _timing.CurTime + TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(before - _battery.GetCharge(cell), Is.GreaterThan(1f));
        Assert.That(_cyberware.TrySetComputeLoad(module, float.NaN), Is.False);
        Assert.That(SComp<NCCyberwareComponent>(module).ComputeLoad, Is.EqualTo(0.4f));
    }

    [Test, RunOnSide(Side.Server)]
    public void BiomonitorPulseIsNativeOneShotAndConsumesOneReadingOfRealEnergy()
    {
        var (body, cell) = Scene();
        var module = Install(body, "NCBiomonitorImplant", "Thorax", "NCSlotSubdermalThorax");
        var before = _battery.GetCharge(cell);
        Assert.That(_abilities.TrySetEnabled(body, module, true), Is.False);
        Assert.That(_abilities.TryPulse(body, module, () => false), Is.False);
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(before));
        Assert.That(_abilities.TryPulse(body, module), Is.True);
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(before - 1f).Within(0.001));
        Assert.That(SComp<NCCyberwareAbilityComponent>(module).Active, Is.False);
    }

    private (EntityUid Body, EntityUid Cell) Scene()
    {
        _maps.CreateMap(out var map);
        var body = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map));
        var cell = NCCyberwareTestPower.Install(SEntMan, _cyberware, _slots, _battery, body, 1000f);
        return (body, cell);
    }
    private EntityUid Install(EntityUid body, string prototype, string category, string slot)
    {
        var module = SSpawnAtPosition(prototype, SComp<TransformComponent>(body).Coordinates);
        var organ = SComp<BodyComponent>(body).Organs!.ContainedEntities.Single(uid => SComp<OrganComponent>(uid).Category?.Id == category);
        Assert.That(_cyberware.TryInstall(new(NCActionResolutionId.New(), body, organ, module, slot, true, true)).Accepted, Is.True);
        Assert.That(_cyberware.TryCalibrateCyberware(module, NCActionResolutionId.New(), out _), Is.True);
        _abilities.RefreshBody(body);
        return module;
    }
}
