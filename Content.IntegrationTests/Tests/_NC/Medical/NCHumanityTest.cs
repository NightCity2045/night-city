// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Humanity;
using Content.Server._NC.Medical.Persistence;
using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Physiology;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Function;
using Content.Shared._NC.Medical.Humanity;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.Mind;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>Gate 5 contracts T01/T36–T43. Execution is explicitly opt-in; compilation is safe by default.</summary>
[TestFixture]
public sealed class NCHumanityTest : GameTest
{
    [SidedDependency(Side.Server)] private NCHumanitySystem _humanity = default!;
    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private NCCyberwareSystem _cyberware = default!;
    [SidedDependency(Side.Server)] private NCPharmacologySystem _pharmacology = default!;
    [SidedDependency(Side.Server)] private NCMedicalPersistenceSystem _persistence = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySchedulerSystem _scheduler = default!;
    [SidedDependency(Side.Server)] private NCOrganDetachmentSystem _detachment = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;
    [SidedDependency(Side.Server)] private SharedMindSystem _mind = default!;

    [Test, RunOnSide(Side.Server)]
    public void T01NewCharacterOwnsOneHundredOnItsActualBrain()
    {
        var body = Human();
        Assert.That(Status(body).Current, Is.EqualTo(100));
        Assert.That(Status(body).Burden, Is.Zero);
        Assert.That(SEntMan.HasComponent<NCHumanityComponent>(Organ(body, "Brain")), Is.True);
        Assert.That(SEntMan.HasComponent<NCHumanityComponent>(body), Is.False);
    }

    [Test, RunOnSide(Side.Server)]
    public void T36AnUnadministeredSyringeCannotRestoreHumanity()
    {
        var body = Human();
        State(body).Current = 80;
        SEntMan.SpawnEntity("NCSyringeNeuralRestorative", SComp<TransformComponent>(body).Coordinates);
        _scheduler.ProcessNow(body, TimeSpan.FromSeconds(30));
        Assert.That(Status(body).Current, Is.EqualTo(80));
        Assert.That(_pharmacology.GetModifiers(body).HumanityRecoveryPerSecond, Is.Zero);
    }

    [Test, RunOnSide(Side.Server)]
    public void T39RemovalAndReinstallationDoNotHealOrBillTheSameAdaptationAgain()
    {
        var body = Human();
        var module = InstallInterface(body);
        Assert.That(Status(body).Current, Is.EqualTo(98));
        Assert.That(Status(body).Maximum, Is.EqualTo(99));
        Assert.That(_cyberware.TryRemove(new(NCActionResolutionId.New(), body, module)).Accepted, Is.True);
        Assert.That(Status(body).Current, Is.EqualTo(98));
        Assert.That(Status(body).Maximum, Is.EqualTo(100));
        Assert.That(_cyberware.TryInstall(new(NCActionResolutionId.New(), body, Organ(body, "Head"),
            module, "NCSlotNeuralCore")).Accepted, Is.True);
        Assert.That(Status(body).Current, Is.EqualTo(98));
        Assert.That(_cyberware.TrySetLoad(module, 0), Is.True);
        Assert.That(Status(body).Burden, Is.EqualTo(1));
    }

    [Test, RunOnSide(Side.Server)]
    public void UsedInterfaceStillRequiresTheNewRecipientsOwnAdaptation()
    {
        var donor = Human();
        var module = InstallInterface(donor);
        Assert.That(_cyberware.TryRemove(new(NCActionResolutionId.New(), donor, module)).Accepted, Is.True);
        var recipient = Human();
        Assert.That(_cyberware.TryInstall(new(NCActionResolutionId.New(), recipient, Organ(recipient, "Head"),
            module, "NCSlotNeuralCore")).Accepted, Is.True);
        Assert.That(Status(recipient).Current, Is.EqualTo(98));
    }

    [Test, RunOnSide(Side.Server)]
    public void T37DifferentRestorativesShareOneAggregateCapAndToxicExposureLosesBenefit()
    {
        var body = Human();
        Assert.That(_pharmacology.ApplyDose(body, "NCDrugNeuralRestorative", 8).Accepted, Is.True);
        Assert.That(_pharmacology.ApplyDose(body, "NCDrugCombinedNeuralSupport", 8).Accepted, Is.True);
        Assert.That(_pharmacology.GetModifiers(body).HumanityRecoveryPerSecond,
            Is.EqualTo(2f / 60f).Within(0.00001));
        _pharmacology.ApplyDose(body, "NCDrugNeuralRestorative", 100);
        _pharmacology.ApplyDose(body, "NCDrugCombinedNeuralSupport", 100);
        Assert.That(_pharmacology.GetModifiers(body).HumanityRecoveryPerSecond, Is.Zero);
    }

    [Test, RunOnSide(Side.Server)]
    public void T38MedicationCannotBankPointsAboveTheInstalledLimit()
    {
        var body = Human();
        InstallInterface(body);
        State(body).Current = 98.9f;
        _pharmacology.ApplyDose(body, "NCDrugNeuralRestorative", 8);
        _scheduler.ProcessNow(body, TimeSpan.FromSeconds(30));
        Assert.That(Status(body).Current, Is.EqualTo(99));
    }

    [Test, RunOnSide(Side.Server)]
    public void T40ZeroHumanityKeepsTheLivingCharacterAndMind()
    {
        var body = Human();
        InstallInterface(body);
        var mind = _mind.CreateMind(null, "Gate 5 patient");
        _mind.TransferTo(mind, body);
        State(body).Current = 0;
        State(body).Stress = 100;
        _humanity.TryAdvanceBody(body, TimeSpan.Zero);
        Assert.That(Status(body).Stage, Is.EqualTo(NCCyberCrisisStage.Acute));
        Assert.That(SComp<NCPhysiologyComponent>(body).Irreversible, Is.False);
        Assert.That(SComp<NCBodyFunctionComponent>(body).Movement, Is.GreaterThan(0));
        Assert.That(_mind.TryGetMind(body, out var retained, out _), Is.True);
        Assert.That(retained, Is.EqualTo(mind.Owner));
    }

    [Test, RunOnSide(Side.Server)]
    public void T41StabilizationSuppressesStrainWithoutChangingHumanityOrItsMaximum()
    {
        var body = Human();
        InstallInterface(body);
        State(body).Current = 10;
        State(body).Stress = 90;
        _humanity.TryAdvanceBody(body, TimeSpan.Zero);
        _pharmacology.ApplyDose(body, "NCDrugNeuralStabilizer", 6);
        _scheduler.ProcessNow(body, TimeSpan.FromSeconds(10));
        Assert.That(Status(body).Stress, Is.LessThan(90));
        Assert.That(Status(body).Current, Is.EqualTo(10));
        Assert.That(Status(body).Maximum, Is.EqualTo(99));
    }

    [Test, RunOnSide(Side.Server)]
    public void T43AcuteModeRestrictionExistsWithoutAnyVisualEffect()
    {
        var body = Human();
        Assert.That(_detachment.TryDetachOrgan(body, "ArmRight", out _), Is.True);
        var arm = SEntMan.SpawnEntity("NCOrganHumanCyberArmRight", SComp<TransformComponent>(body).Coordinates);
        Assert.That(_detachment.TryAttachOrgan(body, "Torso", arm), Is.True);
        Assert.That(_cyberware.TrySetLoad(arm, 1), Is.True);
        State(body).Current = 0;
        State(body).Stress = 100;
        _humanity.TryAdvanceBody(body, TimeSpan.Zero);
        Assert.That(SComp<NCCyberwareComponent>(arm).CrisisBlocksActiveModes, Is.True);
        Assert.That(_cyberware.TrySetLoad(arm, 1), Is.False);
        Assert.That(_cyberware.TrySetLoad(arm, 0), Is.True);
        Assert.That(SComp<NCPhysiologyComponent>(body).Irreversible, Is.False);
    }

    [Test, RunOnSide(Side.Server)]
    public void InvalidHumanitySnapshotIsRejectedBeforeMutatingTheLiveBrain()
    {
        var body = Human();
        State(body).Current = 80;
        var saved = _persistence.SerializeMedicalState(body)!;
        saved.Humanity!.Current = float.NaN;
        Assert.That(_persistence.RestoreMedicalState(body, saved), Is.False);
        Assert.That(Status(body).Current, Is.EqualTo(80));
    }

    [Test, RunOnSide(Side.Server)]
    public void RestorePreservesHistoryAndDoesNotAdvanceRemainingCooldownsOffline()
    {
        var body = Human();
        InstallInterface(body);
        State(body).Current = 80;
        Assert.That(_humanity.TryApplyStimulus(body, "NCHumanityNeuralTrauma", Guid.NewGuid()), Is.True);
        var saved = _persistence.SerializeMedicalState(body)!;
        Assert.That(_persistence.TryRebaseSnapshotForRuntime(saved, saved.CapturedAt + TimeSpan.FromHours(24), out var rebased), Is.True);
        Assert.That(rebased.Humanity!.RecentImpulses[0].RemainingSeconds,
            Is.EqualTo(saved.Humanity!.RecentImpulses[0].RemainingSeconds));
        Assert.That(_persistence.RestoreMedicalState(body, saved), Is.True);
        Assert.That(Status(body).Current, Is.EqualTo(80));
        Assert.That(State(body).Adaptation.Count, Is.EqualTo(saved.Humanity.Adaptation.Count));
    }

    [Test, RunOnSide(Side.Server)]
    public void LegacyMedicalSaveCannotResetAnAlreadyKnownCharactersHumanity()
    {
        var body = Human();
        State(body).Current = 80;
        var saved = _persistence.SerializeMedicalState(body)!;
        saved.Version = NCMedicalSnapshotVersions.BorgCore;
        saved.Humanity = null;
        Assert.That(_persistence.RestoreMedicalState(body, saved), Is.True);
        Assert.That(Status(body).Current, Is.EqualTo(80));
    }

    private EntityUid Human()
    {
        _maps.CreateMap(out var map);
        return SEntMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map));
    }
    private EntityUid Organ(EntityUid body, string category)
    {
        Assert.That(_anatomy.TryGetOrgan(body, category, out var organ), Is.True);
        return organ;
    }
    private EntityUid InstallInterface(EntityUid body)
    {
        var module = SEntMan.SpawnEntity("NCImplantNeuralLinkMk1", SComp<TransformComponent>(body).Coordinates);
        Assert.That(_cyberware.TryInstall(new(NCActionResolutionId.New(), body, Organ(body, "Head"),
            module, "NCSlotNeuralCore")).Accepted, Is.True);
        return module;
    }
    private NCHumanityStateSnapshot State(EntityUid body) => SComp<NCHumanityComponent>(Organ(body, "Brain")).State;
    private NCHumanityStatus Status(EntityUid body)
    {
        Assert.That(_humanity.TryGetStatus(body, out var status), Is.True);
        return status;
    }
}
