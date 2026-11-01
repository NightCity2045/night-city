// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Cyberware.Borg;
using Content.Server._NC.Medical.Persistence;
using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Physiology;
using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Server._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Borg;
using Content.Shared._NC.Medical.Function;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.Body;
using Content.Shared.Body.Components;
using Content.Shared.ActionBlocker;
using Content.Shared.Buckle;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Mind;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>Gate 4 T30–T35/T42 contracts. Prepared and compiled; execution requires the owner's opt-in.</summary>
[TestFixture]
public sealed partial class NCBorgCoreTest : GameTest
{
    [SidedDependency(Side.Server)] private NCBorgSystem _borg = default!;
    [SidedDependency(Side.Server)] private NCMedicalPersistenceSystem _persistence = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySchedulerSystem _scheduler = default!;
    [SidedDependency(Side.Server)] private NCPharmacologySystem _drugs = default!;
    [SidedDependency(Side.Server)] private NCCyberwareSystem _cyberware = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;
    [SidedDependency(Side.Server)] private SharedBuckleSystem _buckle = default!;
    [SidedDependency(Side.Server)] private SharedHandsSystem _hands = default!;
    [SidedDependency(Side.Server)] private SharedMindSystem _mind = default!;
    [SidedDependency(Side.Server)] private SharedBatterySystem _battery = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _slots = default!;
    [SidedDependency(Side.Server)] private SharedSolutionContainerSystem _solutions = default!;
    [SidedDependency(Side.Server)] private HungerSystem _hunger = default!;
    [SidedDependency(Side.Server)] private ActionBlockerSystem _blocker = default!;
    [SidedDependency(Side.Server)] private NCOrganDetachmentSystem _detachment = default!;
    [SidedDependency(Side.Server)] private NCSystemicSystem _systemic = default!;
    [SidedDependency(Side.Server)] private Content.Server._NC.Medical.Humanity.NCHumanitySystem _humanity = default!;

    [Test, RunOnSide(Side.Server)]
    public void EmptyFactoryCarrierDoesNotCreateABrainOrFreeFood()
    {
        _maps.CreateMap(out var map);
        var core = SEntMan.SpawnEntity("NCBorgCore", new MapCoordinates(Vector2.Zero, map));
        Assert.That(Organs(core).Any(organ => SEntMan.HasComponent<BrainComponent>(organ)), Is.False);
        Assert.That(_hunger.GetHunger(SComp<HungerComponent>(core)), Is.Zero);
        Assert.That(_persistence.SerializeMedicalState(core)!.BorgCore!.PowerBus.Main, Is.Null);
        Assert.That(SComp<NCPhysiologyComponent>(core).Irreversible, Is.False);
        Assert.That(_blocker.CanAttack(core), Is.False);
    }

    [TestCase(false), TestCase(true), RunOnSide(Side.Server)]
    public void T30T31ConversionMovesTheExactBrainAndMindWithoutFalseBrainDeath(bool includeComponent)
    {
        var (patient, user, core, _) = Prepared();
        Assert.That(SEntMan.HasComponent<NCPharmacologyComponent>(core), Is.False,
            "Conversion must accept a fresh biopod without an existing drug exposure.");
        var brain = Organ(patient, "Brain");
        var mind = _mind.CreateMind(null, "Gate 4 patient");
        _mind.TransferTo(mind, patient);
        var integrity = SComp<NCOrganIntegrityComponent>(brain);
        integrity.CurrentIntegrity = 63f;
        Assert.That(_borg.TryConvert(includeComponent ? core : new Entity<NCBorgCoreComponent?>(core.Owner, null), user, patient), Is.True);
        Assert.That(Organ(core, "Brain"), Is.EqualTo(brain));
        Assert.That(Organs(patient).Any(organ => SEntMan.HasComponent<BrainComponent>(organ)), Is.False);
        Assert.That(_mind.TryGetMind(core, out var moved, out _), Is.True);
        Assert.That(moved, Is.EqualTo(mind.Owner));
        Assert.That(integrity.CurrentIntegrity, Is.EqualTo(63f));
        Assert.That(_humanity.TryGetStatus(core, out var humanity), Is.True);
        Assert.That(humanity.Current, Is.EqualTo(75));
        Assert.That(humanity.Burden, Is.EqualTo(20));
        Assert.That(SComp<NCPhysiologyComponent>(core).Irreversible, Is.False);
        Assert.That(SComp<NCPhysiologyComponent>(patient).Irreversible, Is.True);
    }

    [Test, RunOnSide(Side.Server)]
    public void T32ExhaustedSupportProducesActualBrainInsufficiency()
    {
        var (patient, user, core, cell) = Prepared();
        Assert.That(_borg.TryConvert(core, user, patient), Is.True);
        _battery.SetCharge(cell, 0f);
        Assert.That(_scheduler.ProcessNow(core, TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(SComp<NCCirculationComponent>(core).Perfusion, Is.Zero);
        Assert.That(SComp<NCPhysiologyComponent>(core).TimeBelowCriticalPerfusion, Is.GreaterThan(TimeSpan.Zero));
        var brain = Organ(core, "Brain");
        _battery.SetCharge(cell, 100f);
        _borg.RefreshSupport((core, SComp<NCBorgCoreComponent>(core)));
        Assert.That(_scheduler.ProcessNow(core, TimeSpan.Zero), Is.True);
        Assert.That(SComp<NCCirculationComponent>(core).Perfusion, Is.GreaterThan(0f));
        Assert.That(Organ(core, "Brain"), Is.EqualTo(brain));
        Assert.That(SComp<NCPhysiologyComponent>(core).Irreversible, Is.False);
    }

    [Test, RunOnSide(Side.Server)]
    public void T33T34FrameEvacuationPreservesItsDamageWhileDrugsAndBrainStayOnTheCore()
    {
        var (patient, user, core, _) = Prepared();
        Assert.That(_borg.TryConvert(core, user, patient), Is.True);
        Assert.That(_drugs.ApplyDose(core, "NCDrugSedative", 2f).Accepted, Is.True);
        var brain = Organ(core, "Brain");
        var frame = SpawnAt("NCBorgStandardShell", user);
        _humanity.TryGetStatus(core, out var originalHumanity);
        var frameCell = InstallCell(Organ(frame, "Thorax"), 1000f);
        _cyberware.ResetPowerClock(frame);
        Assert.That(_borg.TryDock(core, user, frame), Is.True);
        var arm = Organ(core, "ArmLeft");
        SComp<NCOrganIntegrityComponent>(arm).CurrentIntegrity = 37f;
        var tool = SpawnAt("NCCyberwareServiceTool", user);
        Assert.That(_hands.TryPickupAnyHand(user, tool), Is.True);
        Assert.That(_borg.TryUndock(core, user), Is.True);
        Assert.That(Organ(core, "Brain"), Is.EqualTo(brain));
        Assert.That(_humanity.TryGetStatus(core, out var evacuatedHumanity), Is.True);
        Assert.That(evacuatedHumanity.Current, Is.EqualTo(originalHumanity.Current));
        Assert.That(evacuatedHumanity.Burden, Is.EqualTo(originalHumanity.Burden));
        Assert.That(SComp<NCPharmacologyComponent>(core).ActiveDrugs, Contains.Key((ProtoId<NCDrugProfilePrototype>) "NCDrugSedative"));
        Assert.That(SComp<NCOrganIntegrityComponent>(arm).CurrentIntegrity, Is.EqualTo(37f));
        Assert.That(SComp<OrganComponent>(arm).Body, Is.Not.EqualTo(core.Owner));
        Assert.That(_battery.GetCharge(frameCell), Is.LessThanOrEqualTo(999f));
        Assert.That(SComp<NCBodyFunctionComponent>(core).Vision, Is.Zero);
        Assert.That(SComp<NCBodyFunctionComponent>(core).Speech, Is.Zero);
        Assert.That(SComp<NCBodyFunctionComponent>(core).Movement, Is.Zero);
        Assert.That(SComp<NCPhysiologyComponent>(core).Irreversible, Is.False);
    }

    [Test, RunOnSide(Side.Server)]
    public void T35AnIrreversiblyDeadPatientCannotBeConverted()
    {
        var (patient, user, core, _) = Prepared();
        SComp<NCPhysiologyComponent>(patient).Irreversible = true;
        var brain = Organ(patient, "Brain");
        Assert.That(_borg.TryConvert(core, user, patient), Is.False);
        Assert.That(Organ(patient, "Brain"), Is.EqualTo(brain));
        Assert.That(SComp<NCBorgCoreComponent>(core).Occupied, Is.False);
    }

    [Test, RunOnSide(Side.Server)]
    public void T42ArtificialFiltersClearExistingDrugExposureAndPowerLossReducesClearance()
    {
        var (patient, user, core, cell) = Prepared();
        Assert.That(_borg.TryConvert(core, user, patient), Is.True);
        Assert.That(_drugs.ApplyDose(core, "NCDrugSedative", 5f).Accepted, Is.True);
        var state = SComp<NCPharmacologyComponent>(core);
        var exposure = state.ActiveDrugs[(ProtoId<NCDrugProfilePrototype>) "NCDrugSedative"];
        _scheduler.ProcessNow(core, TimeSpan.FromSeconds(1));
        var suppliedClearance = 5f - exposure.Concentration;
        Assert.That(suppliedClearance, Is.GreaterThan(0f));
        var before = exposure.Concentration;
        _battery.SetCharge(cell, 0f);
        _scheduler.ProcessNow(core, TimeSpan.FromSeconds(1));
        Assert.That(before - exposure.Concentration, Is.LessThan(suppliedClearance));
    }

    [Test, RunOnSide(Side.Server)]
    public void CoreSnapshotRestoresFiniteSuppliesAndDamageWithoutManufacturingAFullFrame()
    {
        var (patient, user, core, cell) = Prepared();
        Assert.That(_borg.TryConvert(core, user, patient), Is.True);
        _scheduler.ProcessNow(core, TimeSpan.FromSeconds(3));
        var saved = _persistence.SerializeMedicalState(core)!;
        Assert.That(saved, Is.Not.Null);
        Assert.That(_persistence.RestoreMedicalState(core, saved), Is.True);
        Assert.That(SComp<NCBorgCoreComponent>(core).CoreId, Is.EqualTo(saved.BorgCore!.CoreId));
        Assert.That(_persistence.SerializeMedicalState(core)!.BorgCore!.PowerBus.Main!.Charge,
            Is.EqualTo(saved.BorgCore.PowerBus.Main!.Charge));
        Assert.That(Organs(core).Count(organ => SEntMan.HasComponent<BrainComponent>(organ)), Is.EqualTo(1));
        Assert.That(Organs(core).Any(organ => SComp<OrganComponent>(organ).Category == "Torso"), Is.False);
    }

    [Test, RunOnSide(Side.Server)]
    public void SnapshotCannotCopyALiveCoreIntoASecondCarrier()
    {
        var (patient, user, core, _) = Prepared();
        Assert.That(_borg.TryConvert(core, user, patient), Is.True);
        var saved = _persistence.SerializeMedicalState(core)!;
        var copy = SpawnAt("NCBorgCore", user);
        Assert.That(_persistence.RestoreMedicalState(copy, saved), Is.False);
        Assert.That(Organs(copy).Any(organ => SEntMan.HasComponent<BrainComponent>(organ)), Is.False);
        Assert.That(Organs(core).Count(organ => SEntMan.HasComponent<BrainComponent>(organ)), Is.EqualTo(1));
    }

    [Test, RunOnSide(Side.Server)]
    public void PendingInjectedMedicineMovesWithoutBeingCopiedOrDiscarded()
    {
        var (patient, user, core, _) = Prepared();
        Assert.That(_solutions.TryGetSolution(patient, "bloodstream", out var source, out var oldBlood), Is.True);
        Assert.That(_solutions.TryAddReagent(source!.Value, "NCSedative", 5), Is.True);
        Assert.That(_borg.TryConvert(core, user, patient), Is.True);
        Assert.That(_solutions.TryGetSolution(core.Owner, "bloodstream", out _, out var newBlood), Is.True);
        Assert.That(oldBlood!.GetTotalPrototypeQuantity("NCSedative").Float(), Is.Zero);
        Assert.That(newBlood!.GetTotalPrototypeQuantity("NCSedative").Float(), Is.EqualTo(5f));
    }

    [Test, RunOnSide(Side.Server)]
    public void FramePowerCannotSpendThePodsIsolatedLifeSupportCell()
    {
        var (patient, user, core, coreCell) = Prepared();
        Assert.That(_borg.TryConvert(core, user, patient), Is.True);
        var shell = SpawnAt("NCBorgStandardShell", user);
        Assert.That(SEntMan.HasComponent<HungerComponent>(shell), Is.False);
        var cell = InstallCell(Organ(shell, "Thorax"), 1000f);
        _cyberware.ResetPowerClock(shell);
        Assert.That(_borg.TryDock(core, user, shell), Is.True);
        var isolated = _battery.GetCharge(coreCell);
        var charge = _battery.GetCharge(cell);
        var cursor = SComp<NCCyberwarePowerStateComponent>(core).LastAdvancedAt;
        Assert.That(_cyberware.TryAdvanceBodyPower(core, cursor + TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(_battery.GetCharge(cell), Is.LessThan(charge));
        Assert.That(_battery.GetCharge(coreCell), Is.EqualTo(isolated));
    }

    [Test, RunOnSide(Side.Server)]
    public void MissingPhysicalFilterCannotClearWasteDespiteFullMediumAndPower()
    {
        var (patient, user, core, _) = Prepared();
        Assert.That(_borg.TryConvert(core, user, patient), Is.True);
        Assert.That(_detachment.TryDetachOrgan(core, "Kidneys", out _), Is.True);
        Assert.That(SComp<NCBorgCoreComponent>(core).Clearance, Is.Zero);
        _scheduler.ProcessNow(core, TimeSpan.FromSeconds(10));
        Assert.That(_systemic.GetBurden(core, NCSystemicChannel.Toxin), Is.GreaterThan(0f));
    }

    [Test, RunOnSide(Side.Server)]
    public void T42RestorativeFollowsTheBrainAndUsesTheArtificialClearanceRoute()
    {
        var (patient, user, core, _) = Prepared();
        var brain = Organ(patient, "Brain");
        Assert.That(_drugs.ApplyDose(patient, "NCDrugNeuralRestorative", 8).Accepted, Is.True);
        Assert.That(_borg.TryConvert(core, user, patient), Is.True);
        Assert.That(_humanity.TryGetStatus(core, out var before), Is.True);
        _scheduler.ProcessNow(core, TimeSpan.FromSeconds(10));
        Assert.That(_humanity.TryGetStatus(core, out var after), Is.True);
        Assert.That(after.Current, Is.GreaterThan(before.Current).And.LessThanOrEqualTo(after.Maximum));
        Assert.That(Organ(core, "Brain"), Is.EqualTo(brain));
        Assert.That(SComp<NCPharmacologyComponent>(core).ActiveDrugs[
            (ProtoId<NCDrugProfilePrototype>) "NCDrugNeuralRestorative"].Concentration, Is.LessThan(8));
    }

    private (EntityUid Patient, EntityUid User, Entity<NCBorgCoreComponent?> Core, EntityUid Cell) Prepared()
    {
        _maps.CreateMap(out var map);
        var patient = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map));
        var user = SpawnAt("MobHuman", patient);
        var table = SpawnAt("OperatingTable", patient);
        Assert.That(_buckle.TryBuckle(patient, user, table), Is.True);
        var core = SpawnAt("NCBorgCore", user);
        var cell = InstallCell(core, 1000f);
        Assert.That(_solutions.TryGetSolution(core, NCBorgSystem.SupportSolution, out var solution, out _), Is.True);
        foreach (var reagent in new[] { NCBorgSystem.Perfusate, NCBorgSystem.Oxygen, NCBorgSystem.Dialysate, NCBorgSystem.Coolant })
            _solutions.TryAddReagent(solution!.Value, reagent.Id, 20);
        _hunger.SetHunger(core, 100f);
        _borg.RefreshSupport((core, SComp<NCBorgCoreComponent>(core)));
        var site = SComp<NCSurgicalSiteComponent>(Organ(patient, "Head"));
        site.AccessDepth = NCSurgicalAccessDepth.OrganExposed;
        site.TissuesRetracted = true;
        site.BoneCasingOpen = true;
        Assert.That(_drugs.ApplyDose(patient, "NCDrugSurgicalAnesthetic", 6f).Accepted, Is.True);
        Assert.That(_scheduler.ProcessNow(patient, TimeSpan.Zero), Is.True);
        Assert.That(SComp<NCPhysiologyComponent>(patient).Consciousness, Is.GreaterThanOrEqualTo(NCConsciousnessState.Unconscious));
        Assert.That(_hands.TryPickupAnyHand(user, core), Is.True);
        Assert.That(_borg.CanConvert(core, user, patient, out _), Is.True);
        Assert.That(_persistence.SerializeMedicalState(patient), Is.Not.Null, "The prepared living donor must have a valid medical snapshot.");
        Assert.That(_detachment.CanDetachOrgan(patient, "Brain", out _), Is.True);
        return (patient, user, (core, SComp<NCBorgCoreComponent>(core)), cell);
    }

    private EntityUid InstallCell(EntityUid holder, float charge)
    {
        var cell = SpawnAt("NCCyberwarePowerCell", holder);
        _battery.SetCharge(cell, charge);
        NCCyberwareTestPower.Insert(_slots, holder, cell, false);
        return cell;
    }

    private EntityUid SpawnAt(EntProtoId prototype, EntityUid anchor)
        => SEntMan.SpawnEntity(prototype, SComp<TransformComponent>(anchor).Coordinates);
    private IReadOnlyList<EntityUid> Organs(EntityUid body) => SComp<BodyComponent>(body).Organs!.ContainedEntities;
    private EntityUid Organ(EntityUid body, ProtoId<OrganCategoryPrototype> category)
        => Organs(body).Single(organ => SComp<OrganComponent>(organ).Category == category);
}
