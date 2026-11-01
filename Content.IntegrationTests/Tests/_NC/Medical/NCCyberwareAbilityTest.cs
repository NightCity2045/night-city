// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Combat.Targeting;
using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Cyberware.Abilities;
using Content.Server._NC.Medical.Persistence;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Abilities;
using Content.Shared.Body;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Hands.Components;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Tools.Components;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>Gate 6 contracts. Compile normally; execute only after the owner's explicit integration opt-in.</summary>
[TestFixture]
public sealed class NCCyberwareAbilityTest : GameTest
{
    [SidedDependency(Side.Server)] private NCCyberwareAbilitySystem _abilities = default!;
    [SidedDependency(Side.Server)] private NCCyberwareSystem _cyberware = default!;
    [SidedDependency(Side.Server)] private NCOrganDetachmentSystem _detachment = default!;
    [SidedDependency(Side.Server)] private NCMedicalPersistenceSystem _persistence = default!;
    [SidedDependency(Side.Server)] private SharedHandsSystem _hands = default!;
    [SidedDependency(Side.Server)] private SharedBatterySystem _battery = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _slots = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;
    [SidedDependency(Side.Server)] private NCCyberwareAbilityModifiersSystem _modifiers = default!;
    [SidedDependency(Side.Server)] private NCTargetingAccuracySystem _accuracy = default!;
    [SidedDependency(Side.Server)] private IGameTiming _timing = default!;

    [Test, RunOnSide(Side.Server)]
    public void UninstalledHardwareHasNoBodyCapability()
    {
        var (body, _) = Scene();
        var module = SEntMan.SpawnEntity("NCSandevistanImplant", SComp<TransformComponent>(body).Coordinates);
        Assert.That(_abilities.CanOperate(body, module), Is.False);
        Assert.That(SComp<NCCyberwareAbilityComponent>(module).Action, Is.Null);
    }

    [Test, RunOnSide(Side.Server)]
    public void BiomonitorReadsOnlyItsOwnerAndConsumesRealEnergyPerReading()
    {
        var (body, cell) = Scene();
        var (other, _) = Scene();
        var module = Install(body, "NCBiomonitorImplant", "Thorax", "NCSlotSubdermalThorax");
        var before = _battery.GetCharge(cell);
        Assert.That(_abilities.TryToggle(other, module), Is.False);
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(before));
        Assert.That(_abilities.TryToggle(body, module), Is.True);
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(before - 1f).Within(0.001));
        Assert.That(SComp<NCCyberwareAbilityComponent>(module).Active, Is.False);
        Assert.That(SComp<NCCyberwareComponent>(module).Load, Is.Zero);
    }

    [Test, RunOnSide(Side.Server)]
    public void T23BurstChangesOnlyItsOwnerAndNeverTheServerClock()
    {
        var (body, _) = Scene();
        var module = Install(body, "NCSandevistanImplant", "Head", "NCSlotMotorBurst");
        var (other, _) = Scene();
        var before = _timing.CurTime;
        Assert.That(_abilities.TryToggle(body, module), Is.True);
        Assert.That(SComp<NCCyberwareAbilityBodyComponent>(body).MotorBonus, Is.EqualTo(0.25f));
        Assert.That(SComp<NCCyberwareAbilityBodyComponent>(other).MotorBonus, Is.Zero);
        Assert.That(_timing.CurTime, Is.EqualTo(before));
    }

    [Test, RunOnSide(Side.Server)]
    public void T24OnlyPermittedManualToolOperationsReceiveTheShorterDelay()
    {
        var (body, _) = Scene();
        var module = Install(body, "NCSandevistanImplant", "Head", "NCSlotMotorBurst");
        _abilities.TryToggle(body, module);
        Assert.That(_modifiers.GetManualToolDelay(body, ["Screwing"], 0, TimeSpan.FromSeconds(10)),
            Is.EqualTo(TimeSpan.FromSeconds(8)).Within(TimeSpan.FromTicks(1)));
        Assert.That(_modifiers.GetManualToolDelay(body, ["Welding"], 1, TimeSpan.FromSeconds(10)), Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That(_modifiers.GetManualToolDelay(body, ["MachineCycle"], 0, TimeSpan.FromSeconds(10)), Is.EqualTo(TimeSpan.FromSeconds(10)));
    }

    [Test, RunOnSide(Side.Server)]
    public void ManualStopPreservesThePhysicalControllersCooldown()
    {
        var (body, _) = Scene();
        var module = Install(body, "NCSandevistanImplant", "Head", "NCSlotMotorBurst");
        Assert.That(_abilities.TryToggle(body, module), Is.True);
        Assert.That(_abilities.TryToggle(body, module), Is.True);
        Assert.That(SComp<NCCyberwareComponent>(module).Load, Is.Zero);
        Assert.That(_abilities.TryToggle(body, module), Is.False);
        Assert.That(SComp<NCCyberwareAbilityComponent>(module).CooldownRemainingSeconds, Is.GreaterThanOrEqualTo(30));
    }

    [Test, RunOnSide(Side.Server)]
    public void ReinstallationRetainsCooldownAndDoesNotRetainActiveMode()
    {
        var (body, _) = Scene();
        var module = Install(body, "NCSandevistanImplant", "Head", "NCSlotMotorBurst");
        _abilities.TryToggle(body, module);
        Assert.That(_cyberware.TryRemove(new(NCActionResolutionId.New(), body, module)).Accepted, Is.True);
        Assert.That(SComp<NCCyberwareAbilityComponent>(module).Active, Is.False);
        Assert.That(SComp<NCCyberwareAbilityBodyComponent>(body).MotorBonus, Is.Zero);
        Assert.That(_cyberware.TryInstall(new(NCActionResolutionId.New(), body, Organ(body, "Head"), module, "NCSlotMotorBurst", true, true)).Accepted, Is.True);
        _cyberware.TryCalibrateCyberware(module, NCActionResolutionId.New(), out _);
        _abilities.RefreshBody(body);
        Assert.That(_abilities.TryToggle(body, module), Is.False);
    }

    [Test, RunOnSide(Side.Server)]
    public void PowerLossClearsTheMotorChannel()
    {
        var (body, cell) = Scene();
        var module = Install(body, "NCSandevistanImplant", "Head", "NCSlotMotorBurst");
        _abilities.TryToggle(body, module);
        _battery.SetCharge(cell, 0);
        Assert.That(SComp<NCCyberwareAbilityComponent>(module).Active, Is.False);
        Assert.That(SComp<NCCyberwareAbilityBodyComponent>(body).MotorBonus, Is.Zero);
    }

    [Test, RunOnSide(Side.Server)]
    public void OccupiedHandIsNotEmptiedOrDeletedToDeployATool()
    {
        var (body, _) = Scene();
        var module = Install(body, "NCToolHandImplant", "ArmRight", "NCSlotArmUtilityRight");
        var occupied = SEntMan.SpawnEntity("Screwdriver", SComp<TransformComponent>(body).Coordinates);
        var hand = SComp<HandsComponent>(body).Hands.Single(pair => pair.Value.Location == HandLocation.Right).Key;
        Assert.That(_hands.TryPickup(body, occupied, hand), Is.True);
        Assert.That(_abilities.TryToggle(body, module), Is.False);
        Assert.That(_hands.IsHolding(body, occupied), Is.True);
        Assert.That(SEntMan.EntityExists(occupied), Is.True);
    }

    [Test, RunOnSide(Side.Server)]
    public void IntegralToolUsesOrdinaryToolQualityAndCannotOperateAfterPowerLoss()
    {
        var (body, cell) = Scene();
        var module = Install(body, "NCToolHandImplant", "ArmRight", "NCSlotArmUtilityRight");
        Assert.That(_abilities.TryToggle(body, module), Is.True);
        var tool = SComp<NCCyberwareAbilityComponent>(module).IntegralItem!.Value;
        Assert.That(SComp<ToolComponent>(tool).Qualities, Does.Contain("Screwing"));
        var use = new ToolUseAttemptEvent(body, 0);
        SEntMan.EventBus.RaiseLocalEvent(tool, use);
        Assert.That(use.Cancelled, Is.False);
        _battery.SetCharge(cell, 0);
        var stopped = new ToolUseAttemptEvent(body, 0);
        SEntMan.EventBus.RaiseLocalEvent(tool, stopped);
        Assert.That(stopped.Cancelled, Is.True);
    }

    [Test, RunOnSide(Side.Server)]
    public void T04DetachedArmLosesItsBladeAndAttackPermission()
    {
        var (body, _) = Scene();
        var module = Install(body, "NCMantisBladeImplant", "ArmRight", "NCSlotArmUtilityRight");
        Assert.That(_abilities.TryToggle(body, module), Is.True);
        var blade = SComp<NCCyberwareAbilityComponent>(module).IntegralItem!.Value;
        var before = new AttemptMeleeEvent(body);
        SEntMan.EventBus.RaiseLocalEvent(blade, ref before);
        Assert.That(before.Cancelled, Is.False);
        Assert.That(_detachment.TryDetachOrgan(body, "ArmRight", out _), Is.True);
        Assert.That(_hands.IsHolding(body, blade), Is.False);
        var after = new AttemptMeleeEvent(body);
        SEntMan.EventBus.RaiseLocalEvent(blade, ref after);
        Assert.That(after.Cancelled, Is.True);
    }

    [Test, RunOnSide(Side.Server)]
    public void T25MotorModesAreExclusiveWhileBothControllersRemainInstalled()
    {
        var (body, _) = Scene();
        var burst = Install(body, "NCSandevistanImplant", "Head", "NCSlotMotorBurst");
        var stabilizer = Install(body, "NCKerenzikovImplant", "Head", "NCSlotMotorStabilizer");
        Assert.That(_abilities.TryToggle(body, stabilizer), Is.True);
        Assert.That(_abilities.TryToggle(body, burst), Is.False);
        Assert.That(_cyberware.TryGetOwningBody(burst, out var owner), Is.True);
        Assert.That(owner, Is.EqualTo(body));
        Assert.That(_abilities.TryToggle(body, stabilizer), Is.True);
        Assert.That(_abilities.TryToggle(body, burst), Is.True);
        Assert.That(SComp<NCCyberwareAbilityBodyComponent>(body).MovingPenaltyReduction, Is.Zero);
    }

    [Test, RunOnSide(Side.Server)]
    public void StabilizationReducesExistingCalledShotMovementPenaltyOnly()
    {
        var (body, _) = Scene();
        var module = Install(body, "NCKerenzikovImplant", "Head", "NCSlotMotorStabilizer");
        _abilities.TryToggle(body, module);
        var captured = _accuracy.CaptureMovementPenaltyReduction(body);
        var baseline = _accuracy.ResolveCalledShotHitChance(1, true);
        Assert.That(_accuracy.ResolveCalledShotHitChance(1, true, captured), Is.EqualTo(baseline + (1 - baseline) * 0.25f).Within(0.00001));
        Assert.That(_accuracy.ResolveCalledShotHitChance(1, false, captured), Is.EqualTo(1));
        _abilities.TryToggle(body, module);
        Assert.That(_accuracy.CaptureMovementPenaltyReduction(body), Is.Zero);
    }

    [Test, RunOnSide(Side.Server)]
    public void RestoredControllerIsOffButRetainsCooldownAndRealCharge()
    {
        var (body, cell) = Scene();
        var module = Install(body, "NCSandevistanImplant", "Head", "NCSlotMotorBurst");
        _abilities.TryToggle(body, module);
        var saved = _persistence.SerializeMedicalState(body)!;
        var remaining = saved.CyberwareModules.Single(s => s.Profile.Id == "NCSandevistan").AbilityCooldownSeconds;
        var charge = _battery.GetCharge(cell);
        Assert.That(_persistence.RestoreMedicalState(body, saved), Is.True);
        var restored = _cyberware.CollectCyberware(body).Single(uid => SEntMan.HasComponent<NCCyberwareAbilityComponent>(uid));
        Assert.That(SComp<NCCyberwareAbilityComponent>(restored).Active, Is.False);
        Assert.That(SComp<NCCyberwareAbilityComponent>(restored).CooldownRemainingSeconds, Is.EqualTo(remaining));
        Assert.That(_cyberware.GetSupplyCharge(restored), Is.EqualTo(charge).Within(0.001));
        Assert.That(_abilities.TryToggle(body, restored), Is.False);
    }

    [Test, RunOnSide(Side.Server)]
    public void MalformedCooldownIsRejectedBeforeLiveMutation()
    {
        var (body, _) = Scene();
        var module = Install(body, "NCSandevistanImplant", "Head", "NCSlotMotorBurst");
        _abilities.TryToggle(body, module);
        var saved = _persistence.SerializeMedicalState(body)!;
        saved.CyberwareModules.Single(s => s.Profile.Id == "NCSandevistan").AbilityCooldownSeconds = float.NaN;
        Assert.That(_persistence.RestoreMedicalState(body, saved), Is.False);
        Assert.That(SComp<NCCyberwareAbilityComponent>(module).Active, Is.True);
    }

    [Test, RunOnSide(Side.Server)]
    public void ActivePowerDemandEndsAtSixSecondsEvenAcrossALargeObservedInterval()
    {
        var (body, cell) = Scene();
        var module = Install(body, "NCSandevistanImplant", "Head", "NCSlotMotorBurst");
        _abilities.TryToggle(body, module);
        var before = _battery.GetCharge(cell);
        _cyberware.TryAdvanceBodyPower(body, _timing.CurTime + TimeSpan.FromSeconds(10));
        var spent = before - _battery.GetCharge(cell);
        Assert.That(spent, Is.GreaterThan(100).And.LessThan(180));
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
        var module = SEntMan.SpawnEntity(prototype, SComp<TransformComponent>(body).Coordinates);
        Assert.That(_cyberware.TryInstall(new(NCActionResolutionId.New(), body, Organ(body, category), module, slot, true, true)).Accepted, Is.True);
        Assert.That(_cyberware.TryCalibrateCyberware(module, NCActionResolutionId.New(), out _), Is.True);
        _abilities.RefreshBody(body);
        return module;
    }
    private EntityUid Organ(EntityUid body, string category)
        => SComp<BodyComponent>(body).Organs!.ContainedEntities.Single(uid => SComp<OrganComponent>(uid).Category?.Id == category);
}
