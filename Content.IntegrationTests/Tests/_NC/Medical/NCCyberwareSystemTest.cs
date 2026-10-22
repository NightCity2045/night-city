// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Diagnostics.Detail;
using Content.Server._NC.Medical.Persistence;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Diagnostics.Detail;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Item;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCCyberwareSystem))]
public sealed class NCCyberwareSystemTest : GameTest
{
    [SidedDependency(Side.Server)] private SharedBatterySystem _battery = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _powerSlots = default!;
    private static readonly EntProtoId HumanMob = "MobHuman";

    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private NCCyberwareSystem _cyberware = default!;
    [SidedDependency(Side.Server)] private NCDiagnosticsSystem _diagnostics = default!;
    [SidedDependency(Side.Server)] private NCMedicalPersistenceSystem _persistence = default!;
    [SidedDependency(Side.Server)] private IComponentFactory _components = default!;
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;
    [SidedDependency(Side.Server)] private IResourceManager _resources = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void EveryCyberwareProfileAndPhysicalItemHasAResolvablePlaceholderIcon()
    {
        var profiles = _prototypes.EnumeratePrototypes<NCCyberwarePrototype>().ToArray();
        Assert.That(profiles, Is.Not.Empty);
        foreach (var profile in profiles)
        {
            foreach (var specifier in profile.HostIcons.Values.Append(profile.Icon))
            {
                Assert.That(specifier, Is.TypeOf<SpriteSpecifier.Rsi>(), profile.ID);
                var icon = (SpriteSpecifier.Rsi) specifier;
                var root = new ResPath("/Textures") / icon.RsiPath;
                Assert.Multiple(() =>
                {
                    Assert.That(_resources.ContentFileExists(root / "meta.json"), Is.True, profile.ID);
                    Assert.That(_resources.ContentFileExists(root / $"{icon.RsiState}.png"), Is.True, profile.ID);
                });
            }
        }

        string[] physicalItems =
        [
            "NCImplantNeuralLinkMk1",
            "NCImplantCirculatoryAssistMk1",
            "NCOrganHumanCyberArmLeft",
            "NCOrganHumanCyberArmRight",
            "NCOrganHumanCyberEyes",
            "NCOrganHumanCyberHeart",
        ];
        foreach (var id in physicalItems)
            Assert.That(_prototypes.Index<EntityPrototype>(id).HasComp<ItemComponent>(_components), Is.True, id);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void InstallAndRemoveUseTheLiveOrganGraphAndAreIdempotent()
    {
        var body = SSpawn(HumanMob);
        var head = GetOrgan(body, "Head");
        var module = SSpawn("NCImplantNeuralLinkMk1");
        var installId = NCActionResolutionId.New();
        var request = new NCCyberwareInstallRequest(
            installId,
            body,
            head,
            module,
            "NCSlotNeuralCore",
            true,
            true);

        var installed = _cyberware.TryInstall(request);
        var duplicateInstall = _cyberware.TryInstall(request);
        var state = SComp<NCCyberwareComponent>(module);
        var host = SComp<NCCyberwareSlotHostComponent>(head);
        Assert.Multiple(() =>
        {
            Assert.That(installed.Status, Is.EqualTo(NCCyberwareMutationStatus.Applied));
            Assert.That(duplicateInstall.Status, Is.EqualTo(NCCyberwareMutationStatus.Duplicate));
            Assert.That(state.InstalledSlot?.Id, Is.EqualTo("NCSlotNeuralCore"));
            Assert.That(state.NeuralState, Is.EqualTo(NCCyberwareNeuralState.Linked));
            Assert.That(state.Calibrated, Is.True);
            Assert.That(SComp<Content.Shared.Body.ChildOrganComponent>(module).Parent, Is.EqualTo(head));
            Assert.That(host.Modules!.ContainedEntities, Does.Contain(module));
            Assert.That(_cyberware.CollectCyberware(body), Does.Contain(module));
        });

        var occupied = SSpawn("NCImplantNeuralLinkMk1");
        var occupiedResult = _cyberware.TryInstall(new NCCyberwareInstallRequest(
            NCActionResolutionId.New(),
            body,
            head,
            occupied,
            "NCSlotNeuralCore"));
        Assert.That(occupiedResult.Failure, Is.EqualTo(NCCyberwareMutationFailure.SlotOccupied));

        var removeId = NCActionResolutionId.New();
        var remove = new NCCyberwareRemoveRequest(removeId, body, module);
        var removed = _cyberware.TryRemove(remove);
        var duplicateRemove = _cyberware.TryRemove(remove);
        Assert.Multiple(() =>
        {
            Assert.That(removed.Status, Is.EqualTo(NCCyberwareMutationStatus.Applied));
            Assert.That(duplicateRemove.Status, Is.EqualTo(NCCyberwareMutationStatus.Duplicate));
            Assert.That(state.InstalledSlot, Is.Null);
            Assert.That(SComp<Content.Shared.Body.ChildOrganComponent>(module).Parent, Is.Null);
            Assert.That(host.Modules!.ContainedEntities, Does.Not.Contain(module));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void EmpAndBodyServiceAreBoundedAndExactlyOnce()
    {
        var body = SSpawn(HumanMob);
        var cell = NCCyberwareTestPower.Install(SEntMan, _cyberware, _powerSlots, _battery, body);
        var neural = Install(body, "Head", "NCImplantNeuralLinkMk1", "NCSlotNeuralCore", true);
        var pump = Install(body, "Thorax", "NCImplantCirculatoryAssistMk1", "NCSlotThoracicSupport");
        var neuralState = SComp<NCCyberwareComponent>(neural);
        var pumpState = SComp<NCCyberwareComponent>(pump);
        var powerAtStart = _battery.GetCharge(cell);
        Assert.That(_cyberware.TrySetLoad(neural, 1f), Is.True);
        Assert.That(_cyberware.AdvanceModule(neural, TimeSpan.FromSeconds(10), SGameTiming.CurTime + TimeSpan.FromSeconds(10)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(_battery.GetCharge(cell), Is.LessThan(powerAtStart));
            Assert.That(neuralState.Heat, Is.GreaterThan(0f));
            Assert.That(neuralState.Operational, Is.True);
        });

        neuralState.Heat = 80f;
        pumpState.Heat = 40f;

        var serviceId = NCActionResolutionId.New();
        var service = _cyberware.TryApplyServiceToBody(
            body,
            serviceId,
            "NCCoolantServiceAgent",
            1f);
        var neuralHeat = neuralState.Heat;
        var pumpHeat = pumpState.Heat;
        var duplicateService = _cyberware.TryApplyServiceToBody(
            body,
            serviceId,
            "NCCoolantServiceAgent",
            1f);
        Assert.Multiple(() =>
        {
            Assert.That(service.Status, Is.EqualTo(NCCyberwareMutationStatus.Applied));
            Assert.That(service.AffectedModules, Is.EqualTo(1));
            Assert.That(neuralHeat, Is.EqualTo(45f).Within(0.001f));
            Assert.That(pumpHeat, Is.EqualTo(40f).Within(0.001f));
            Assert.That(duplicateService.Status, Is.EqualTo(NCCyberwareMutationStatus.Duplicate));
            Assert.That(neuralState.Heat, Is.EqualTo(neuralHeat));
            Assert.That(pumpState.Heat, Is.EqualTo(pumpHeat));
        });

        var powerBefore = _battery.GetCharge(cell);
        var integrityBefore = SComp<NCOrganIntegrityComponent>(neural).CurrentIntegrity;
        var empId = NCActionResolutionId.New();
        var emp = new NCCyberwareEmpRequest(empId, body, 2_000f, TimeSpan.FromSeconds(8), 7);
        var appliedEmp = _cyberware.ApplyEmp(emp);
        var powerAfter = _battery.GetCharge(cell);
        var integrityAfter = SComp<NCOrganIntegrityComponent>(neural).CurrentIntegrity;
        var duplicateEmp = _cyberware.ApplyEmp(emp);
        Assert.Multiple(() =>
        {
            Assert.That(appliedEmp.Status, Is.EqualTo(NCCyberwareMutationStatus.Applied));
            Assert.That(appliedEmp.AffectedModules, Is.EqualTo(3));
            Assert.That(neuralState.EmpDisabledUntil, Is.GreaterThan(TimeSpan.Zero));
            Assert.That(neuralState.NeuralState, Is.EqualTo(NCCyberwareNeuralState.Disrupted));
            Assert.That(powerAfter, Is.LessThan(powerBefore));
            Assert.That(integrityAfter, Is.LessThan(integrityBefore));
            Assert.That(duplicateEmp.Status, Is.EqualTo(NCCyberwareMutationStatus.Duplicate));
            Assert.That(_battery.GetCharge(cell), Is.EqualTo(powerAfter));
            Assert.That(SComp<NCOrganIntegrityComponent>(neural).CurrentIntegrity, Is.EqualTo(integrityAfter));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void DiagnosticsAndPersistenceUseBoundedSemanticCyberwareState()
    {
        var source = SSpawn(HumanMob);
        var cell = NCCyberwareTestPower.Install(SEntMan, _cyberware, _powerSlots, _battery, source, 2345f);
        var installed = Install(source, "Head", "NCImplantNeuralLinkMk1", "NCSlotNeuralCore", true);
        var knownId = Guid.Parse("fc868755-43fe-491a-8ca4-a692315821f6");
        var state = SComp<NCCyberwareComponent>(installed);
        state.StableInstanceId = knownId;
        state.Heat = 42f;

        Assert.That(_diagnostics.TryBuildSnapshot(
            source,
            "NCDiagnosticsVisual",
            NCActionResolutionId.New(),
            TimeSpan.FromSeconds(1),
            out var visual), Is.True);
        Assert.That(_diagnostics.TryBuildSnapshot(
            source,
            "NCDiagnosticsScannerInternal",
            NCActionResolutionId.New(),
            TimeSpan.FromSeconds(1),
            out var scanner), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(visual.Cyberware, Is.Empty);
            Assert.That(scanner.Cyberware, Has.Count.EqualTo(2));
            Assert.That(scanner.Cyberware[0].Profile.Id, Is.EqualTo("NCNeuralLinkMk1"));
            Assert.That(scanner.Cyberware[0].Slot?.Id, Is.EqualTo("NCSlotNeuralCore"));
        });

        var first = _persistence.SerializeMedicalState(source);
        Assert.That(first, Is.Not.Null);
        var persisted = first!;
        Assert.That(persisted.CyberwareModules, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(persisted.CyberwareModules[0].HostCategory?.Id, Is.EqualTo("Head"));
            Assert.That(persisted.CyberwareModules[0].Slot?.Id, Is.EqualTo("NCSlotNeuralCore"));
            Assert.That(persisted.CyberwareModules[0].StableInstanceId, Is.EqualTo(knownId));
            Assert.That(typeof(NCCyberwareStateSnapshot).GetFields()
                .Any(field => field.FieldType == typeof(EntityUid)), Is.False);
        });

        var target = SSpawn(HumanMob);
        Assert.That(_persistence.RestoreMedicalState(target, persisted), Is.True);
        var restored = _cyberware.CollectCyberware(target).Single(module =>
            SComp<NCCyberwareComponent>(module).StableInstanceId == knownId);
        var restoredState = SComp<NCCyberwareComponent>(restored);
        Assert.Multiple(() =>
        {
            Assert.That(restoredState.CurrentPower, Is.Zero);
            Assert.That(_persistence.SerializeMedicalState(target)!.CyberwareModules.Single(value => value.PowerBus != null)
                .PowerBus!.Main!.Charge, Is.EqualTo(_battery.GetCharge(cell)));
            Assert.That(restoredState.Heat, Is.EqualTo(42f));
            Assert.That(restoredState.InstalledSlot?.Id, Is.EqualTo("NCSlotNeuralCore"));
            Assert.That(SComp<Content.Shared.Body.ChildOrganComponent>(restored).Parent,
                Is.EqualTo(GetOrgan(target, "Head")));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void MechanicalRegionTraumaNeverCreatesOrganicWoundsAndIsExactlyOnce()
    {
        var source = SSpawn(HumanMob);
        var snapshot = _persistence.SerializeMedicalState(source)!;
        var armState = snapshot.Organs["ArmLeft"];
        var stableId = Guid.Parse("d067c60f-c33d-48a6-acb3-d7da7b3c4872");
        armState.EntityPrototype = "NCOrganHumanCyberArmLeft";
        armState.CyberwareIdentity = "NCOrganHumanCyberArmLeft";
        armState.Integrity!.MaxIntegrity = 160f;
        armState.Integrity.CurrentIntegrity = 160f;
        armState.Integrity.FunctionalEfficiency = 1f;
        armState.Integrity.Necrosis = 0f;
        armState.Integrity.Material = NCAnatomicalMaterialKind.Mechanical;
        armState.Nerve!.CyberprostheticInterface = true;
        armState.Cyberware = new NCCyberwareStateSnapshot
        {
            EntityPrototype = "NCOrganHumanCyberArmLeft",
            Profile = "NCIndustrialCyberArm",
            StableInstanceId = stableId,
            CurrentIntegrity = 160f,
            MaximumIntegrity = 160f,
            CurrentPower = 0f,
            Heat = 0f,
            Load = 0f,
            EmpDisabledUntil = TimeSpan.Zero,
            NeuralIntegrity = 1f,
            NeuralState = NCCyberwareNeuralState.Disconnected,
            Calibrated = false,
        };
        armState.Cyberware.Assembly = _cyberware.CreateDefaultAssemblySnapshot("NCIndustrialCyberArm", stableId);

        var body = SSpawn(HumanMob);
        Assert.That(_persistence.RestoreMedicalState(body, snapshot), Is.True);
        var arm = GetOrgan(body, "ArmLeft");
        Assert.That(SComp<NCCyberwareComponent>(arm).StableInstanceId, Is.EqualTo(stableId));

        var request = CreateTraumaRequest(body, 12f);
        var first = new NCCombatTraumaRequestedEvent(request);
        SEntMan.EventBus.RaiseLocalEvent(body, ref first);
        var integrityAfter = SComp<NCOrganIntegrityComponent>(arm).CurrentIntegrity;
        var duplicate = new NCCombatTraumaRequestedEvent(request);
        SEntMan.EventBus.RaiseLocalEvent(body, ref duplicate);

        Assert.Multiple(() =>
        {
            Assert.That(first.Handled, Is.True);
            Assert.That(first.Applied, Is.True);
            Assert.That(integrityAfter, Is.EqualTo(148f).Within(0.001f));
            Assert.That(duplicate.Handled, Is.True);
            Assert.That(duplicate.Applied, Is.False);
            Assert.That(SComp<NCOrganIntegrityComponent>(arm).CurrentIntegrity, Is.EqualTo(integrityAfter));
            Assert.That(STryComp<NCWoundContainerComponent>(body, out var wounds)
                ? wounds.Wounds.Count
                : 0, Is.Zero);
            Assert.That(STryComp<NCBleedingComponent>(body, out var bleeding)
                ? bleeding.Sources.Count
                : 0, Is.Zero);
        });
    }

    private EntityUid Install(
        EntityUid body,
        ProtoId<Content.Shared.Body.OrganCategoryPrototype> hostCategory,
        EntProtoId modulePrototype,
        ProtoId<NCCyberwareSlotPrototype> slot,
        bool neural = false)
    {
        var host = GetOrgan(body, hostCategory);
        var module = SSpawn(modulePrototype);
        var result = _cyberware.TryInstall(new NCCyberwareInstallRequest(
            NCActionResolutionId.New(),
            body,
            host,
            module,
            slot,
            neural,
            neural));
        Assert.That(result.Status, Is.EqualTo(NCCyberwareMutationStatus.Applied));
        return module;
    }

    private EntityUid GetOrgan(
        EntityUid body,
        ProtoId<Content.Shared.Body.OrganCategoryPrototype> category)
    {
        Assert.That(_anatomy.TryGetOrgan(body, category, out var organ), Is.True);
        return organ;
    }

    private static NCCombatTraumaRequest CreateTraumaRequest(EntityUid body, float postArmorDamage)
    {
        return new NCCombatTraumaRequest(
            NCActionResolutionId.New(),
            12,
            34,
            body,
            null,
            null,
            null,
            "NCZoneArmLeft",
            "ArmLeft",
            "BallisticProjectile",
            new DamageSpecifier(),
            FixedPoint2.New(postArmorDamage),
            FixedPoint2.Zero,
            FixedPoint2.New(postArmorDamage),
            FixedPoint2.Zero,
            Vector2.Zero,
            Vector2.UnitX,
            NCTraumaFlags.ArmorPenetrated,
            0f,
            TimeSpan.Zero);
    }
}
