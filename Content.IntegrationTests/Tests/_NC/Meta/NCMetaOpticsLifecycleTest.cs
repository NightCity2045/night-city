// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.IntegrationTests.Tests._NC.Medical;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Humanity;
using Content.Server._NC.Medical.Persistence;
using Content.Server._NC.Medical.Physiology;
using Content.Server._NC.Meta.Domains;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared.Body;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.NightVision;
using Content.Shared.Overlays;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture]
[TestOf(typeof(NCMetaOpticsSystem))]
public sealed class NCMetaOpticsLifecycleTest : GameTest
{
    [SidedDependency(Side.Server)] private NCMetaOpticsSystem _optics = default!;
    [SidedDependency(Side.Server)] private NCCyberwareSystem _cyberware = default!;
    [SidedDependency(Side.Server)] private NCMedicalPersistenceSystem _medical = default!;
    [SidedDependency(Side.Server)] private NCHumanitySystem _humanity = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySchedulerSystem _physiology = default!;
    [SidedDependency(Side.Server)] private SharedBatterySystem _battery = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _slots = default!;
    [SidedDependency(Side.Server)] private SharedNightVisionSystem _nightVision = default!;
    public override PoolSettings PoolSettings => new() { Connected = false };

    private (EntityUid Body, EntityUid Module) CreatePoweredOptics()
    {
        // Capture current native hardware state, including its required service/exterior sections.
        // Restore supplies the starting graph; calibration and filter changes use the public APIs.
        var template = SSpawn("MobHuman");
        NCCyberwareTestPower.Install(SEntMan, _cyberware, _slots, _battery, template);
        var snapshot = _medical.SerializeMedicalState(template)!;
        var detachedOptics = SSpawn("NCMetaCyberOptics");
        Assert.That(_cyberware.TryCaptureOrganState(detachedOptics, out var opticsState), Is.True);
        Assert.That(_cyberware.ValidatePersistentState(opticsState, installedModule: false), Is.True);
        var profile = SProtoMan.Index<NCCyberwarePrototype>("NCCyberopticsMk1");
        var eyes = snapshot.Organs["Eyes"];
        eyes.EntityPrototype = "NCMetaCyberOptics";
        eyes.CyberwareIdentity = "NCMetaCyberOptics";
        eyes.Integrity!.MaxIntegrity = profile.MaximumIntegrity;
        eyes.Integrity.CurrentIntegrity = profile.MaximumIntegrity;
        eyes.Integrity.FunctionalEfficiency = 1f;
        eyes.Integrity.Necrosis = 0f;
        eyes.Integrity.Material = NCAnatomicalMaterialKind.Mechanical;
        eyes.Nerve = new NCNerveStateSnapshot { NeuralIntegrity = 1f, Sensitivity = 1f, MotorFunction = 1f, CyberprostheticInterface = true };
        eyes.Cyberware = opticsState;
        // This restore replaces the fixtures rather than duplicating live module, part and cell serials.
        SEntMan.DeleteEntity(detachedOptics);
        SEntMan.DeleteEntity(template);
        var body = SSpawn("MobHuman");
        Assert.That(_humanity.TryPrepareLegacyState(body, snapshot), Is.True);
        Assert.That(_medical.RestoreMedicalState(body, snapshot), Is.True);
        Assert.That(_physiology.ProcessNow(body, TimeSpan.Zero), Is.True);
        var module = SComp<BodyComponent>(body).Organs!.ContainedEntities.Single(uid => SComp<OrganComponent>(uid).Category == "Eyes");
        Assert.That(_cyberware.TryCalibrateCyberware(module, NCActionResolutionId.New(), out _), Is.True);
        Assert.That(SComp<NCCyberwareComponent>(module).Operational, Is.True);
        return (body, module);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ClearAndReenableInTheSameFrameRetainsTheNewNativeVisionProvider()
    {
        var (body, module) = CreatePoweredOptics();
        Assert.That(_optics.TrySetManualFilter(module, body, true), Is.True);
        var first = SComp<NightVisionComponent>(body);
        Assert.That(first.Enabled, Is.True);
        Assert.That(_optics.TrySetManualFilter(module, body, false), Is.True);
        Assert.That(SEntMan.HasComponent<NightVisionComponent>(body), Is.False);
        Assert.That(_optics.TrySetManualFilter(module, body, true), Is.True);
        var second = SComp<NightVisionComponent>(body);
        Assert.That(second, Is.Not.SameAs(first));
        Assert.That(second.Enabled, Is.True);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ClearingOpticsNeverRemovesOrDisablesAReplacementForeignProvider()
    {
        var (body, module) = CreatePoweredOptics();
        Assert.That(_optics.TrySetManualFilter(module, body, true), Is.True);
        SEntMan.RemoveComponent<NightVisionComponent>(body);
        var foreign = SEntMan.AddComponent<NightVisionComponent>(body);
        _nightVision.SetEnabled((body, foreign), true, body);
        Assert.That(_optics.TrySetManualFilter(module, body, false), Is.True);
        Assert.That(SComp<NightVisionComponent>(body), Is.SameAs(foreign));
        Assert.That(foreign.Enabled, Is.True);
        Assert.That(_optics.TrySetManualFilter(module, body, true), Is.False, "A foreign provider cannot be silently taken over.");
        Assert.That(SComp<NightVisionComponent>(body), Is.SameAs(foreign));
    }
}
