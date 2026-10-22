// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Persistence;
using Content.Server._NC.Medical.Physiology;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared.Body;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Emp;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Power.EntitySystems;
using Content.Shared.PowerCell;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
public sealed class NCCyberwarePowerTest : GameTest
{
    [SidedDependency(Side.Server)] private NCCyberwareSystem _cyberware = default!;
    [SidedDependency(Side.Server)] private NCMedicalPersistenceSystem _persistence = default!;
    [SidedDependency(Side.Server)] private SharedBatterySystem _battery = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _slots = default!;
    [SidedDependency(Side.Server)] private PowerCellSystem _cells = default!;
    [SidedDependency(Side.Server)] private SharedHandsSystem _hands = default!;
    [SidedDependency(Side.Server)] private HungerSystem _hunger = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;
    [SidedDependency(Side.Server)] private SharedEmpSystem _emp = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySchedulerSystem _scheduler = default!;

    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test]
    [RunOnSide(Side.Server)]
    public void ClosedChestBlocksVanillaAndServiceEjectionThenTheSameEmptyCellCanBeRefitted()
    {
        var (body, user, cell, holder) = CreateScene();
        Assert.Multiple(() =>
        {
            Assert.That(_cyberware.CanServicePowerBus(holder, user), Is.False);
            Assert.That(_cells.TryEjectBatteryFromSlot(holder, out _, user), Is.False);
            Assert.That(_cyberware.TryEjectPowerCell(holder, user, cell, false), Is.False);
        });
        var thorax = Organ(body, "Thorax");
        var site = SComp<NCSurgicalSiteComponent>(thorax);
        site.Incisions.Add(new NCSurgicalIncisionRecord
        {
            IncisionId = Guid.NewGuid(), TargetZone = "NCZoneChest", Depth = 0.8f,
            EdgeSeparation = 0.3f, BoneCasingOpen = false,
        });
        Assert.That(_cyberware.CanServicePowerBus(holder, user), Is.False);
        site.Incisions[0].BoneCasingOpen = true;
        Assert.That(_cyberware.CanServicePowerBus(holder, user), Is.True);
        _battery.SetCharge(cell, 0f);
        var identity = SComp<NCCyberwareCellComponent>(cell).StableInstanceId;
        Assert.That(_cyberware.TryEjectPowerCell(holder, user, cell, false), Is.True);
        Assert.That(_hands.IsHolding(user, cell), Is.True);
        Assert.That(_cyberware.TryEjectPowerCell(holder, user, cell, false), Is.False);
        Assert.That(_cyberware.TryInsertPowerCell(holder, user, cell, false), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(_battery.GetCharge(cell), Is.Zero);
            Assert.That(SComp<NCCyberwareCellComponent>(cell).StableInstanceId, Is.EqualTo(identity));
            Assert.That(_slots.TryGetSlot(holder, NCCyberwarePowerBusComponent.MainSlot, out var slot), Is.True);
            Assert.That(slot!.Locked, Is.True);
        });
        site.Incisions[0].Sutured = true;
        Assert.That(_cyberware.CanServicePowerBus(holder, user), Is.False);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TwoConsumersDebitOneBatteryOncePerBodyTime()
    {
        var (body, _, cell, _) = CreateScene();
        Install(body, "NCImplantNeuralLinkMk1", "Head", "NCSlotNeuralCore");
        Install(body, "NCImplantCirculatoryAssistMk1", "Thorax", "NCSlotThoracicSupport");
        var time = SGameTiming.CurTime + TimeSpan.FromSeconds(10);
        Assert.That(_cyberware.TryAdvanceBodyPower(body, time), Is.True);
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(985f).Within(0.001f));
        Assert.That(_cyberware.TryAdvanceBodyPower(body, time), Is.False);
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(985f).Within(0.001f));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void RealReservePowersOnlyTheLifeSupportConsumerAndIsNotAFreeTimer()
    {
        var (body, _, cell, holder) = CreateScene();
        var neural = Install(body, "NCImplantNeuralLinkMk1", "Head", "NCSlotNeuralCore");
        var pump = Install(body, "NCImplantCirculatoryAssistMk1", "Thorax", "NCSlotThoracicSupport");
        _battery.SetCharge(cell, 0f);
        var reserve = SEntMan.SpawnEntity("PowerCellMedium", SComp<TransformComponent>(body).Coordinates);
        _battery.SetCharge(reserve, 4f);
        NCCyberwareTestPower.Insert(_slots, holder, reserve, true);
        Assert.That(_cyberware.TryAdvanceBodyPower(body, SGameTiming.CurTime + TimeSpan.FromSeconds(2)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(_battery.GetCharge(reserve), Is.EqualTo(2f).Within(0.001f));
            Assert.That(SComp<NCCyberwareComponent>(pump).Operational, Is.True);
            Assert.That(SComp<NCCyberwareComponent>(neural).Operational, Is.False);
        });
        Assert.That(_cyberware.TryAdvanceBodyPower(body, SGameTiming.CurTime + TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(_battery.GetCharge(reserve), Is.Zero);
        Assert.That(SComp<NCCyberwareComponent>(pump).Operational, Is.False);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void LargeIntervalStopsHeatingWhenTheCellEmpties()
    {
        var (body, _, cell, _) = CreateScene();
        var neural = Install(body, "NCImplantNeuralLinkMk1", "Head", "NCSlotNeuralCore");
        Assert.That(_cyberware.TrySetLoad(neural, 1f), Is.True);
        _battery.SetCharge(cell, 2.5f);
        Assert.That(_cyberware.TryAdvanceBodyPower(body, SGameTiming.CurTime + TimeSpan.FromSeconds(100)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(_battery.GetCharge(cell), Is.Zero);
            Assert.That(SComp<NCCyberwareComponent>(neural).Heat, Is.Zero.Within(0.001f));
            Assert.That(SComp<NCOrganIntegrityComponent>(neural).CurrentIntegrity, Is.EqualTo(60f));
            Assert.That(SComp<NCCyberwareComponent>(neural).Operational, Is.False);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ConversionConsumesActualNutritionAndStopsAtProtectionOrADeadConverter()
    {
        var (body, _, cell, _) = CreateScene();
        var hunger = SComp<HungerComponent>(body);
        _hunger.SetHunger(body, 101f, hunger);
        _battery.SetCharge(cell, 0f);
        var time = SGameTiming.CurTime + TimeSpan.FromSeconds(2);
        Assert.That(_cyberware.TryAdvanceBodyPower(body, time), Is.True);
        Assert.That(_battery.GetCharge(cell), Is.Zero, "Eating without a physical converter cannot charge cells.");
        var converter = Install(body, "NCImplantBioconverterMk1", "Thorax", "NCSlotThoracicConverter");
        _cyberware.ResetPowerClock(body);
        Assert.That(_cyberware.TryAdvanceBodyPower(body, time), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(_battery.GetCharge(cell), Is.EqualTo(6f).Within(0.01f));
            Assert.That(101f - _hunger.GetHunger(hunger), Is.EqualTo(6f / 120f).Within(0.001f));
        });
        _hunger.SetHunger(body, 100f, hunger);
        _cyberware.TryAdvanceBodyPower(body, time + TimeSpan.FromSeconds(2));
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(6f).Within(0.01f));
        _hunger.SetHunger(body, 150f, hunger);
        SComp<NCOrganIntegrityComponent>(converter).CurrentIntegrity = 0f;
        _cyberware.TryAdvanceBodyPower(body, time + TimeSpan.FromSeconds(4));
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(6f).Within(0.01f));
    }

    [TestCase(false)]
    [TestCase(true)]
    [RunOnSide(Side.Server)]
    public void AreaAndBodyEmpNeverDebitTheSameCellTwice(bool cellFirst)
    {
        var (body, _, cell, _) = CreateScene();
        var pulse = Guid.NewGuid();
        if (cellFirst)
            _emp.DoEmpEffects(cell, 100f, TimeSpan.FromSeconds(1), pulseId: pulse);
        _emp.DoEmpEffects(body, 100f, TimeSpan.FromSeconds(1), pulseId: pulse);
        if (!cellFirst)
            _emp.DoEmpEffects(cell, 100f, TimeSpan.FromSeconds(1), pulseId: pulse);
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(900f).Within(0.001f));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void PowerLossReachesRealCardiacOutputWithoutAffectingAnOrganicHeart()
    {
        var (body, _, cell, _) = CreateScene();
        _battery.SetCharge(cell, 0f);
        _scheduler.ProcessNow(body, TimeSpan.Zero);
        Assert.That(SComp<NCCirculationComponent>(body).CardiacOutput, Is.GreaterThan(0f));
        var snapshot = _persistence.SerializeMedicalState(body)!;
        var heart = snapshot.Organs["Heart"];
        heart.EntityPrototype = "NCOrganHumanCyberHeart";
        heart.CyberwareIdentity = heart.EntityPrototype;
        heart.Integrity!.MaxIntegrity = heart.Integrity.CurrentIntegrity = 110f;
        heart.Integrity.Material = NCAnatomicalMaterialKind.Mechanical;
        heart.Cyberware = new NCCyberwareStateSnapshot
        {
            EntityPrototype = heart.EntityPrototype, Profile = "NCCyberheartMk1", StableInstanceId = Guid.NewGuid(),
            MaximumIntegrity = 110f, CurrentIntegrity = 110f, NeuralIntegrity = 1f,
            NeuralState = NCCyberwareNeuralState.NotApplicable, Calibrated = true,
        };
        Assert.That(_persistence.RestoreMedicalState(body, snapshot), Is.True);
        _scheduler.ProcessNow(body, TimeSpan.Zero);
        Assert.That(SComp<NCCirculationComponent>(body).CardiacOutput, Is.Zero.Within(0.001f));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void LegacyChargeMigratesOnceAndCurrentMissingCellsStayMissing()
    {
        var (body, _, _, holder) = CreateScene();
        Assert.That(_cyberware.TryRemove(new NCCyberwareRemoveRequest(NCActionResolutionId.New(), body, holder)).Accepted, Is.True);
        Install(body, "NCImplantNeuralLinkMk1", "Head", "NCSlotNeuralCore");
        var legacy = _persistence.SerializeMedicalState(body)!;
        legacy.Version = NCMedicalSnapshotVersions.ModularCyberware;
        legacy.CyberwareModules.Single().CurrentPower = 1234f;
        Assert.That(_persistence.TryMigrateSnapshot(legacy, out var first), Is.True);
        Assert.That(_persistence.TryMigrateSnapshot(legacy, out var repeated), Is.True);
        var bus = first.CyberwareModules.Single(value => value.PowerBus != null);
        Assert.Multiple(() =>
        {
            Assert.That(first.CyberwareModules.All(value => value.CurrentPower == 0f), Is.True);
            Assert.That(bus.PowerBus!.Main!.Charge, Is.EqualTo(1234f));
            Assert.That(bus.PowerBus.Main.StableInstanceId,
                Is.EqualTo(repeated.CyberwareModules.Single(value => value.PowerBus != null).PowerBus!.Main!.StableInstanceId));
        });
        bus.PowerBus!.Main = null;
        Assert.That(_persistence.TryMigrateSnapshot(first, out var current), Is.True);
        Assert.That(current.CyberwareModules.Single(value => value.PowerBus != null).PowerBus!.Main, Is.Null);
        Assert.That(legacy.CyberwareModules.Single().CurrentPower, Is.EqualTo(1234f));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void CurrentSnapshotRestoresRealChargeNutritionAndCellIdentityAndRejectsShadowEnergy()
    {
        var (body, _, cell, _) = CreateScene();
        _battery.SetCharge(cell, 512.5f);
        _hunger.SetHunger(body, 123.25f);
        var identity = SComp<NCCyberwareCellComponent>(cell).StableInstanceId;
        var saved = _persistence.SerializeMedicalState(body)!;
        _battery.SetCharge(cell, 0f);
        _hunger.SetHunger(body, 100f);
        Assert.That(_persistence.RestoreMedicalState(body, saved), Is.True);
        var holder = _cyberware.CollectCyberware(body).Single(uid => SEntMan.HasComponent<NCCyberwarePowerBusComponent>(uid));
        Assert.That(_cells.TryGetBatteryFromSlot(holder, out var restoredCell), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(_battery.GetCharge(restoredCell!.Value.AsNullable()), Is.EqualTo(512.5f));
            Assert.That(SComp<NCCyberwareCellComponent>(restoredCell.Value).StableInstanceId, Is.EqualTo(identity));
            Assert.That(_hunger.GetHunger(SComp<HungerComponent>(body)), Is.EqualTo(123.25f));
        });
        saved.CyberwareModules.Single(value => value.PowerBus != null).CurrentPower = 1f;
        Assert.That(_persistence.RestoreMedicalState(body, saved), Is.False);
        Assert.That(_battery.GetCharge(restoredCell!.Value.AsNullable()), Is.EqualTo(512.5f));
        var badHolder = saved.CyberwareModules.Single(value => value.PowerBus != null);
        badHolder.CurrentPower = 0f;
        badHolder.PowerBus!.Main!.Prototype = default;
        Assert.That(_persistence.RestoreMedicalState(body, saved), Is.False);
        Assert.That(_battery.GetCharge(restoredCell.Value.AsNullable()), Is.EqualTo(512.5f));
    }

    private (EntityUid Body, EntityUid User, EntityUid Cell, EntityUid Holder) CreateScene()
    {
        _maps.CreateMap(out var map);
        var body = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map));
        var user = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(new Vector2(0f, 1f), map));
        var cell = NCCyberwareTestPower.Install(SEntMan, _cyberware, _slots, _battery, body, 1000f);
        return (body, user, cell, _cyberware.CollectCyberware(body).Single(uid => SEntMan.HasComponent<NCCyberwarePowerBusComponent>(uid)));
    }

    private EntityUid Install(EntityUid body, EntProtoId prototype, ProtoId<OrganCategoryPrototype> host,
        ProtoId<NCCyberwareSlotPrototype> slot)
    {
        var module = SEntMan.SpawnEntity(prototype, SComp<TransformComponent>(body).Coordinates);
        Assert.That(_cyberware.TryInstall(new NCCyberwareInstallRequest(NCActionResolutionId.New(), body,
            Organ(body, host), module, slot, true, true)).Accepted, Is.True);
        return module;
    }

    private EntityUid Organ(EntityUid body, ProtoId<OrganCategoryPrototype> category)
    {
        return SComp<BodyComponent>(body).Organs!.ContainedEntities.Single(uid => SComp<OrganComponent>(uid).Category == category);
    }
}
