// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Surgery;
using Content.Server._NC.Medical.Persistence;
using Content.Server._NC.Combat.Targeting;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>Gate 3 physical contracts. Compile only unless the owner explicitly opts into integration execution.</summary>
[TestFixture]
public sealed class NCCyberwareSurgeryTest : GameTest
{
    private static readonly ProtoId<NCFreeformSurgeryProfilePrototype> HumanField = "NCHumanFreeformSurgery";
    [SidedDependency(Side.Server)] private NCCyberwareSystem _cyberware = default!;
    [SidedDependency(Side.Server)] private NCMedicalPersistenceSystem _persistence = default!;
    [SidedDependency(Side.Server)] private NCSurgeryInteractionSystem _surgeryInteraction = default!;
    [SidedDependency(Side.Server)] private NCTargetingSelectionSystem _targeting = default!;
    [SidedDependency(Side.Server)] private NCOrganDetachmentSystem _detachment = default!;
    [SidedDependency(Side.Server)] private NCFreeformSurgeryEffectSystem _effects = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;
    [SidedDependency(Side.Server)] private SharedBatterySystem _battery = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _slots = default!;
    [SidedDependency(Side.Server)] private SharedContainerSystem _containers = default!;
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;

    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test]
    [RunOnSide(Side.Server)]
    public void SimpleMountCompletesItsFunctionalCheckImmediatelyAndDoesNotHealThePatient()
    {
        var (body, user, cell) = Scene();
        var thorax = Organ(body, "Thorax");
        Open(thorax, "NCZoneChest");
        var implant = SpawnAt("NCImplantCirculatoryAssistMk1", user);
        Stage(user, body, implant, "NCZoneChest");
        var integrity = SComp<NCOrganIntegrityComponent>(thorax);
        integrity.CurrentIntegrity -= 10f;
        var health = integrity.CurrentIntegrity;
        var charge = _battery.GetCharge(cell);
        var tool = SpawnAt("NCCyberwareServiceTool", user);
        var result = _effects.ApplyStroke(Stroke(body, user, thorax, tool, null,
            "NCSurgeryToolCyberware", "NCZoneChest"));
        Assert.That(result.Accepted, Is.True);
        Assert.That(result.Failure, Is.EqualTo(NCFreeformSurgeryFailure.None));
        Assert.That(SComp<NCCyberwareComponent>(implant).Operational, Is.True);
        Assert.That(integrity.CurrentIntegrity, Is.EqualTo(health));
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(charge - 1f).Within(0.001f));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ComplexMountRemainsPhysicalUntilSeparateTestAndReplaySpendsNoSecondCharge()
    {
        var (body, user, cell) = Scene();
        var head = Organ(body, "Head");
        Open(head, "NCZoneHead");
        var implant = SpawnAt("NCImplantNeuralLinkMk1", user);
        Stage(user, body, implant, "NCZoneHead");
        var tool = SpawnAt("NCCyberwareServiceTool", user);
        var mount = Stroke(body, user, head, tool, null, "NCSurgeryToolCyberware", "NCZoneHead");
        var result = _effects.ApplyStroke(mount);
        Assert.That(result.Accepted, Is.True);
        Assert.That(result.Failure, Is.EqualTo(NCFreeformSurgeryFailure.CyberwareCalibrationRequired));
        Assert.That(SComp<NCCyberwareComponent>(implant).Calibrated, Is.False);
        Assert.That(_effects.ApplyStroke(mount).Applied, Is.False);
        Assert.That(SComp<ChildOrganComponent>(implant).Parent, Is.EqualTo(head));
        var test = Stroke(body, user, head, tool, implant, "NCSurgeryToolCyberwareCalibration", "NCZoneHead");
        var before = _battery.GetCharge(cell);
        Assert.That(_effects.ApplyStroke(test).Accepted, Is.True);
        var after = _battery.GetCharge(cell);
        Assert.That(after, Is.EqualTo(before - 1f).Within(0.001f));
        Assert.That(_effects.ApplyStroke(test).Applied, Is.False);
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(after));
        Assert.That(SComp<NCCyberwareComponent>(implant).Operational, Is.True);
    }

    [TestCase("part", NCFreeformSurgeryFailure.CyberwareAssemblyIncomplete)]
    [TestCase("signal", NCFreeformSurgeryFailure.CyberwareSignalWeak)]
    [TestCase("frame", NCFreeformSurgeryFailure.CyberwareOutputLow)]
    [TestCase("charge", NCFreeformSurgeryFailure.CyberwareNoPower)]
    [TestCase("heat", NCFreeformSurgeryFailure.CyberwareOverheated)]
    [TestCase("width", NCFreeformSurgeryFailure.CyberwarePowerOverload)]
    [RunOnSide(Side.Server)]
    public void FunctionalTestExplainsTheActualDefectWithoutRefillingOrMovingAnything(string defect,
        NCFreeformSurgeryFailure expected)
    {
        var (body, _, cell) = Scene();
        var arm = AttachArm(body);
        var state = SComp<NCCyberwareComponent>(arm);
        switch (defect)
        {
            case "part":
                SEntMan.DeleteEntity(SComp<NCCyberwareAssemblyComponent>(arm).Parts.Values.First().ContainedEntity!.Value);
                break;
            case "signal": state.NeuralIntegrity = 0.3f; break;
            case "frame": SComp<NCOrganIntegrityComponent>(arm).CurrentIntegrity = 1f; break;
            case "charge": _battery.SetCharge(cell, 0f); break;
            case "heat": state.Heat = 120f; break;
            case "width":
                var bus = _cyberware.CollectCyberware(body).Single(uid => SEntMan.HasComponent<NCCyberwarePowerBusComponent>(uid));
                SComp<NCCyberwarePowerBusComponent>(bus).MaximumPower = 0.1f;
                break;
        }
        var before = _battery.GetCharge(cell);
        Assert.That(_cyberware.TryCalibrateCyberware((arm, state), NCActionResolutionId.New(), out var failure), Is.False);
        Assert.That(failure, Is.EqualTo(expected));
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(before));
        Assert.That(SComp<OrganComponent>(arm).Body, Is.EqualTo(body));
        Assert.That(state.Calibrated, Is.False);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ForcedDisconnectionAndDonorTransferInvalidateCalibrationWithoutDuplicatingHardware()
    {
        var (body, user, _) = Scene();
        var arm = AttachArm(body);
        var state = SComp<NCCyberwareComponent>(arm);
        var id = state.StableInstanceId;
        Assert.That(_cyberware.TryCalibrateCyberware((arm, state), NCActionResolutionId.New(), out _), Is.True);
        var tool = SpawnAt("NCCyberwareServiceTool", user);
        Assert.That(_effects.ApplyStroke(Stroke(body, user, arm, tool, arm,
            "NCSurgeryToolCyberwareDisconnection", "NCZoneArmLeft")).Accepted, Is.True);
        Assert.That(state.Calibrated, Is.False);
        Assert.That(_cyberware.TryCalibrateCyberware((arm, state), NCActionResolutionId.New(), out _), Is.True);
        Assert.That(_detachment.TryDetachOrgan(body, "ArmLeft", out var detached), Is.True);
        Assert.That(detached, Is.EqualTo(arm));
        Assert.That(state.Calibrated, Is.False);
        Assert.That(_detachment.TryDetachOrgan(user, "ArmLeft", out _), Is.True);
        // An old loose-item save may still carry its former patient's calibration flag.
        state.Calibrated = true;
        state.NeuralState = NCCyberwareNeuralState.Linked;
        Assert.That(_detachment.TryAttachOrgan(user, "Torso", detached), Is.True);
        Assert.That(state.Calibrated, Is.False);
        Assert.That(state.StableInstanceId, Is.EqualTo(id));
        Assert.That(SComp<OrganComponent>(arm).Body, Is.EqualTo(user));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void LocalServiceConsumesOnceAndPreservesThePermanentCeilingAndSupply()
    {
        var (body, user, cell) = Scene();
        var arm = AttachArm(body);
        var state = SComp<NCCyberwareComponent>(arm);
        var frame = SComp<NCOrganIntegrityComponent>(arm);
        state.PermanentDamage = 0.5f;
        frame.CurrentIntegrity = frame.MaxIntegrity * 0.5f - 1f;
        var tool = SpawnAt("NCCyberwareServiceKit", user);
        var request = Stroke(body, user, arm, tool, arm, "NCSurgeryToolCyberwareRepair", "NCZoneArmLeft");
        var charge = _battery.GetCharge(cell);
        Assert.That(_effects.ApplyStroke(request).Applied, Is.True);
        Assert.That(frame.CurrentIntegrity, Is.EqualTo(frame.MaxIntegrity * 0.5f));
        Assert.That(state.PermanentDamage, Is.EqualTo(0.5f));
        Assert.That(SComp<NCSurgeryToolComponent>(tool).UsesRemaining, Is.EqualTo(9));
        Assert.That(_effects.ApplyStroke(request).Applied, Is.False);
        Assert.That(SComp<NCSurgeryToolComponent>(tool).UsesRemaining, Is.EqualTo(9));
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(charge));
        Assert.That(_effects.ApplyStroke(request with { ResolutionId = NCActionResolutionId.New() }).Accepted, Is.False);
        Assert.That(SComp<NCSurgeryToolComponent>(tool).UsesRemaining, Is.EqualTo(9));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TwoExtractionsCommitOnceAndClosedAccessLeavesTheSameItemAndIncision()
    {
        var (body, user, _) = Scene();
        var head = Organ(body, "Head");
        Open(head, "NCZoneHead");
        var implant = SpawnAt("NCImplantNeuralLinkMk1", user);
        Assert.That(_cyberware.TryInstall(new NCCyberwareInstallRequest(NCActionResolutionId.New(), body, head,
            implant, "NCSlotNeuralCore")).Accepted, Is.True);
        var identity = SComp<NCCyberwareComponent>(implant).StableInstanceId;
        var tool = SpawnAt("NCCyberwareServiceTool", user);
        var context = Stroke(body, user, head, tool, implant, "NCSurgeryToolCyberware", "NCZoneHead");
        var incision = SComp<NCSurgicalSiteComponent>(head).Incisions.Single();
        incision.Sutured = true;
        Assert.That(_effects.ApplyStroke(context).Accepted, Is.False);
        Assert.That(SComp<NCCyberwareComponent>(implant).InstalledSlot, Is.Not.Null);
        incision.Sutured = false;
        Assert.That(_effects.ApplyStroke(context).Applied, Is.True);
        Assert.That(_effects.ApplyStroke(context with { ResolutionId = NCActionResolutionId.New() }).Accepted, Is.False);
        Assert.That(SComp<NCCyberwareComponent>(implant).StableInstanceId, Is.EqualTo(identity));
        Assert.That(SComp<NCSurgicalSiteComponent>(head).Incisions.Single(), Is.SameAs(incision));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void StagedItemMovedIntoAContainerIsNotTeleportedIntoThePatient()
    {
        var (body, user, cell) = Scene();
        var head = Organ(body, "Head");
        Open(head, "NCZoneHead");
        var implant = SpawnAt("NCImplantNeuralLinkMk1", user);
        Stage(user, body, implant, "NCZoneHead");
        var tool = SpawnAt("NCCyberwareServiceTool", user);
        var context = Stroke(body, user, head, tool, null, "NCSurgeryToolCyberware", "NCZoneHead");
        var storage = SpawnAt("NCCyberwareWorkbench", user);
        var container = _containers.EnsureContainer<ContainerSlot>(storage, "staged-item-test");
        Assert.That(_containers.Insert(implant, container), Is.True);
        var charge = _battery.GetCharge(cell);
        Assert.That(_effects.ApplyStroke(context).Accepted, Is.False);
        Assert.That(container.ContainedEntity, Is.EqualTo(implant));
        Assert.That(SComp<NCCyberwareComponent>(implant).InstalledSlot, Is.Null);
        Assert.That(SComp<NCSurgicalSiteComponent>(head).Incisions, Has.Count.EqualTo(1));
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(charge));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TechnicalRestoreRollbackCanRetainTheOriginalCalibrationWithoutAnotherTestDebit()
    {
        var (body, _, cell) = Scene();
        var arm = AttachArm(body);
        var state = SComp<NCCyberwareComponent>(arm);
        Assert.That(_cyberware.TryCalibrateCyberware((arm, state), NCActionResolutionId.New(), out _), Is.True);
        var charge = _battery.GetCharge(cell);
        Assert.That(_detachment.TryDetachOrgan(body, "ArmLeft", out var detached, preserveCyberwareCalibration: true), Is.True);
        Assert.That(_detachment.TryAttachOrgan(body, "Torso", detached, preserveCyberwareCalibration: true), Is.True);
        Assert.That(detached, Is.EqualTo(arm));
        Assert.That(state.Calibrated, Is.True);
        Assert.That(_battery.GetCharge(cell), Is.EqualTo(charge));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void UncalibratedEmpDisabledHardwareCanBeRestoredWithoutGainingCalibration()
    {
        var (body, _, _) = Scene();
        var arm = AttachArm(body);
        Assert.That(_cyberware.ApplyEmp(new NCCyberwareEmpRequest(NCActionResolutionId.New(), body,
            2000f, TimeSpan.FromSeconds(10))).Accepted, Is.True);
        Assert.That(SComp<NCCyberwareComponent>(arm).NeuralState, Is.EqualTo(NCCyberwareNeuralState.Disrupted));
        Assert.That(_cyberware.TryCaptureOrganState(arm, out var hardware), Is.True);
        Assert.That(_cyberware.ValidatePersistentState(hardware, false), Is.True);
        var saved = _persistence.SerializeMedicalState(body)!;
        Assert.That(saved, Is.Not.Null, "EMP-disabled uncalibrated hardware must remain serializable.");
        Assert.That(_persistence.TryMigrateSnapshot(saved, out var current), Is.True);
        Assert.That(_cyberware.CanRestorePhysicalIdentities(body, current), Is.True);
        Assert.That(_persistence.RestoreMedicalState(body, saved), Is.True);
        var restored = SComp<NCCyberwareComponent>(Organ(body, "ArmLeft"));
        Assert.That(restored.Calibrated, Is.False);
        Assert.That(restored.Operational, Is.False);
    }

    private (EntityUid Body, EntityUid User, EntityUid Cell) Scene()
    {
        _maps.CreateMap(out var map);
        var body = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map));
        var user = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(new Vector2(0, 1), map));
        return (body, user, NCCyberwareTestPower.Install(SEntMan, _cyberware, _slots, _battery, body, 1000f));
    }

    private EntityUid AttachArm(EntityUid body)
    {
        Assert.That(_detachment.TryDetachOrgan(body, "ArmLeft", out _), Is.True);
        var arm = SpawnAt("NCOrganHumanCyberArmLeft", body);
        Assert.That(_detachment.TryAttachOrgan(body, "Torso", arm), Is.True);
        Open(arm, "NCZoneArmLeft");
        return arm;
    }

    private EntityUid Organ(EntityUid body, ProtoId<OrganCategoryPrototype> category) =>
        SComp<BodyComponent>(body).Organs!.ContainedEntities.Single(uid => SComp<OrganComponent>(uid).Category == category);

    private EntityUid SpawnAt(EntProtoId prototype, EntityUid target) =>
        SEntMan.SpawnEntity(prototype, SComp<TransformComponent>(target).Coordinates);

    private void Stage(EntityUid user, EntityUid body, EntityUid module, ProtoId<NCTargetZonePrototype> zone)
    {
        Assert.That(_targeting.TrySetSelection(user, new NCTargetingState(NCTargetingMode.SelectedZone, zone),
            out _, out _), Is.True);
        Assert.That(_surgeryInteraction.TryStageCyberware(user, body, module), Is.True);
    }

    private void Open(EntityUid region, ProtoId<Content.Shared._NC.Combat.Targeting.NCTargetZonePrototype> zone)
    {
        SComp<NCSurgicalSiteComponent>(region).Incisions.Add(new NCSurgicalIncisionRecord
        {
            IncisionId = Guid.NewGuid(), TargetZone = zone, Depth = 0.9f,
            EdgeSeparation = 0.3f, BoneCasingOpen = true, Surface = NCWoundSurface.Front,
            Polyline = [new Vector2(0.45f, 0.5f), new Vector2(0.55f, 0.5f)],
            SourceResolutionId = NCActionResolutionId.New(),
            CreatedAt = SGameTiming.CurTime, ModifiedAt = SGameTiming.CurTime,
        });
    }

    private NCFreeformSurgeryEffectContext Stroke(EntityUid body, EntityUid user, EntityUid region, EntityUid tool,
        EntityUid? target, ProtoId<NCSurgeryToolCapabilityPrototype> capability,
        ProtoId<Content.Shared._NC.Combat.Targeting.NCTargetZonePrototype> zone)
    {
        var profile = _prototypes.Index(HumanField);
        return new NCFreeformSurgeryEffectContext(user, body, region, tool, SComp<NCSurgeryToolComponent>(tool), zone,
            NCWoundSurface.Front, 0.9f, [new Vector2(0.45f, 0.5f), new Vector2(0.55f, 0.5f)],
            profile.Tools[capability], 1f, 1f, profile.UnpreparedFieldContamination, profile.MinimumOpenRetraction,
            target, false, zone == "NCZoneHead", profile.IncidentalTendonRuptureThreshold,
            profile.IncidentalBoneCrackThreshold, profile.IncidentalBoneBreakThreshold, ResolutionId: NCActionResolutionId.New());
    }
}
