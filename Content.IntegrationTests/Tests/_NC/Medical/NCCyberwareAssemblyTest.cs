// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Persistence;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Stacks;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
public sealed class NCCyberwareAssemblyTest : GameTest
{
    private static readonly ProtoId<NCCyberwarePartSlotPrototype> ControllerSlot = "NCIndustrialArmControllerSlot";
    [SidedDependency(Side.Server)] private NCCyberwareSystem _cyberware = default!;
    [SidedDependency(Side.Server)] private NCMedicalPersistenceSystem _persistence = default!;
    [SidedDependency(Side.Server)] private SharedHandsSystem _hands = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;
    [SidedDependency(Side.Server)] private SharedStackSystem _stacks = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _slots = default!;

    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test]
    [RunOnSide(Side.Server)]
    public void EmptyFrameCanBeAssembledFromThreeRealHeldParts()
    {
        var (user, bench, completeArm) = CreateBench();
        SEntMan.DeleteEntity(completeArm);
        var frame = SEntMan.SpawnEntity("NCOrganHumanCyberArmLeftFrame", SComp<TransformComponent>(user).Coordinates);
        var assembly = SComp<NCCyberwareAssemblyComponent>(frame);
        Assert.That(assembly.Parts.Values.All(slot => slot.ContainedEntity == null), Is.True);
        Assert.That(_hands.TryPickupAnyHand(user, frame), Is.True);
        Assert.That(_cyberware.TryMountAssembly(bench, user, frame), Is.True);
        foreach (var prototype in new[] { "NCCyberarmActuator", "NCCyberarmController", "NCCyberarmSensor" })
        {
            var part = SEntMan.SpawnEntity(prototype, SComp<TransformComponent>(user).Coordinates);
            var identity = SComp<NCCyberwarePartComponent>(part).StableInstanceId;
            Assert.That(_hands.TryPickupAnyHand(user, part), Is.True);
            Assert.That(_cyberware.TryInsertAssemblyPart(bench, user, part), Is.True);
            Assert.That(SComp<NCCyberwarePartComponent>(part).StableInstanceId, Is.EqualTo(identity));
        }
        Assert.That(assembly.FunctionalEfficiency, Is.EqualTo(1f));
        Assert.That(_cyberware.TryTestAssembly(bench, user, frame, assembly.ConfigurationRevision), Is.True);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void RemovingAndRefittingTheSameControllerPreservesTheItemAndInvalidatesTheBenchCheck()
    {
        var (user, bench, arm) = CreateBench();
        var assembly = SComp<NCCyberwareAssemblyComponent>(arm);
        var controller = assembly.Parts[ControllerSlot].ContainedEntity!.Value;
        var identity = SComp<NCCyberwarePartComponent>(controller).StableInstanceId;
        var revision = assembly.ConfigurationRevision;
        Assert.That(_cyberware.TryTestAssembly(bench, user, arm, revision), Is.True);
        Assert.That(assembly.ValidatedRevision, Is.EqualTo(revision));

        Assert.That(_cyberware.TryRemoveAssemblyPart(bench, user, arm, ControllerSlot, revision), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(assembly.FunctionalEfficiency, Is.Zero);
            Assert.That(assembly.ValidatedRevision, Is.Null);
            Assert.That(assembly.ConfigurationRevision, Is.GreaterThan(revision));
            Assert.That(_hands.IsHolding(user, controller), Is.True);
            Assert.That(_cyberware.TryRemoveAssemblyPart(bench, user, arm, ControllerSlot, revision), Is.False);
        });
        Assert.That(_cyberware.TryInsertAssemblyPart(bench, user, controller), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(assembly.Parts[ControllerSlot].ContainedEntity, Is.EqualTo(controller));
            Assert.That(SComp<NCCyberwarePartComponent>(controller).StableInstanceId, Is.EqualTo(identity));
            Assert.That(assembly.FunctionalEfficiency, Is.EqualTo(1f));
            Assert.That(assembly.ValidatedRevision, Is.Null);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void WrongOrOccupiedSlotRejectsTheHeldItemWithoutMovingIt()
    {
        var (user, bench, arm) = CreateBench();
        var assembly = SComp<NCCyberwareAssemblyComponent>(arm);
        var revision = assembly.ConfigurationRevision;
        var spare = SEntMan.SpawnEntity("NCCyberarmController", SComp<TransformComponent>(user).Coordinates);
        Assert.That(_hands.TryPickupAnyHand(user, spare), Is.True);
        Assert.That(_cyberware.TryInsertAssemblyPart(bench, user, spare), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(_hands.IsHolding(user, spare), Is.True);
            Assert.That(assembly.ConfigurationRevision, Is.EqualTo(revision));
            Assert.That(assembly.Parts.Values.Count(slot => slot.ContainedEntity != null), Is.EqualTo(3));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void RepairConsumesMaterialButNeverClearsPermanentDamageOrRefillsCharge()
    {
        var (user, bench, arm) = CreateBench();
        Assert.That(_cyberware.TryCaptureOrganState(arm, out var snapshot), Is.True);
        snapshot.PermanentDamage = 0.25f;
        snapshot.CurrentIntegrity = 0f;
        var controller = snapshot.Assembly!.Parts.Single(part => part.Slot == ControllerSlot);
        controller.PermanentDamage = 0.25f;
        controller.CurrentIntegrity = 0f;
        Assert.That(_cyberware.TryApplyPersistentState(arm, snapshot, SGameTiming.CurTime), Is.True);
        var material = SEntMan.SpawnEntity("SheetSteel", SComp<TransformComponent>(user).Coordinates);
        Assert.That(_hands.TryPickupAnyHand(user, material), Is.True);
        var stack = SComp<StackComponent>(material);
        _stacks.SetCount((material, stack), 10);
        var count = stack.Count;
        for (var i = 0; i < 8; i++)
            Assert.That(_cyberware.TryRepairAssembly(bench, user, material), Is.True);
        Assert.That(_cyberware.TryRepairAssembly(bench, user, material), Is.False);
        Assert.That(_cyberware.TryCaptureOrganState(arm, out var repaired), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(stack.Count, Is.EqualTo(count - 8));
            Assert.That(repaired.CurrentIntegrity, Is.EqualTo(120f));
            Assert.That(repaired.PermanentDamage, Is.EqualTo(0.25f));
            Assert.That(repaired.CurrentPower, Is.Zero);
            var part = repaired.Assembly!.Parts.Single(part => part.Slot == ControllerSlot);
            Assert.That(part.CurrentIntegrity, Is.EqualTo(30f));
            Assert.That(part.PermanentDamage, Is.EqualTo(0.25f));
            Assert.That(SComp<NCCyberwareAssemblyComponent>(arm).FunctionalEfficiency, Is.EqualTo(0.75f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void SnapshotPreservesMissingPartsAndRejectsDuplicatePhysicalIdentities()
    {
        var (user, bench, arm) = CreateBench();
        var assembly = SComp<NCCyberwareAssemblyComponent>(arm);
        Assert.That(_cyberware.TryRemoveAssemblyPart(bench, user, arm, ControllerSlot,
            assembly.ConfigurationRevision), Is.True);
        Assert.That(_cyberware.TryCaptureOrganState(arm, out var snapshot), Is.True);
        Assert.That(snapshot.Assembly!.Parts, Has.Count.EqualTo(2));
        // Retire the old physical frame before materializing its saved identity.
        SEntMan.DeleteEntity(arm);
        var restored = SEntMan.SpawnEntity("NCOrganHumanCyberArmLeft", SComp<TransformComponent>(user).Coordinates);
        Assert.That(_cyberware.TryApplyPersistentState(restored, snapshot, SGameTiming.CurTime), Is.True);
        var restoredAssembly = SComp<NCCyberwareAssemblyComponent>(restored);
        Assert.Multiple(() =>
        {
            Assert.That(restoredAssembly.Parts[ControllerSlot].ContainedEntity, Is.Null);
            Assert.That(restoredAssembly.FunctionalEfficiency, Is.Zero);
            Assert.That(SComp<NCCyberwareComponent>(restored).StableInstanceId, Is.EqualTo(snapshot.StableInstanceId));
        });
        snapshot.Assembly.Parts[1].StableInstanceId = snapshot.Assembly.Parts[0].StableInstanceId;
        Assert.That(_cyberware.TryApplyPersistentState(restored, snapshot, SGameTiming.CurTime), Is.False);
        Assert.That(restoredAssembly.Parts.Values.Count(slot => slot.ContainedEntity != null), Is.EqualTo(2));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void LegacyAssemblyMigrationIsStableAndCurrentSnapshotsNeverRefillEmptySlots()
    {
        var (user, _, arm) = CreateBench();
        Assert.That(_cyberware.TryCaptureOrganState(arm, out var cyberware), Is.True);
        cyberware.Assembly = null;
        var legacy = _persistence.SerializeMedicalState(user)!;
        legacy.Version = NCMedicalSnapshotVersions.SpatialSurgery;
        var armState = legacy.Organs["ArmLeft"];
        armState.EntityPrototype = "NCOrganHumanCyberArmLeft";
        armState.CyberwareIdentity = armState.EntityPrototype;
        armState.Cyberware = cyberware;
        armState.Integrity!.MaxIntegrity = 160f;
        armState.Integrity.CurrentIntegrity = 160f;
        armState.Integrity.Material = NCAnatomicalMaterialKind.Mechanical;
        armState.Nerve!.CyberprostheticInterface = true;
        Assert.That(_persistence.TryMigrateSnapshot(legacy, out var first), Is.True);
        Assert.That(_persistence.TryMigrateSnapshot(legacy, out var repeated), Is.True);
        var migrated = first.Organs["ArmLeft"].Cyberware!.Assembly!;
        Assert.That(migrated.Parts.Select(part => part.StableInstanceId),
            Is.EqualTo(repeated.Organs["ArmLeft"].Cyberware!.Assembly!.Parts.Select(part => part.StableInstanceId)));
        migrated.Parts.RemoveAt(0);
        Assert.That(_persistence.TryMigrateSnapshot(first, out var current), Is.True);
        Assert.That(current.Organs["ArmLeft"].Cyberware!.Assembly!.Parts, Has.Count.EqualTo(2));
        Assert.That(legacy.Organs["ArmLeft"].Cyberware!.Assembly, Is.Null);
    }

    private (EntityUid User, EntityUid Bench, EntityUid Arm) CreateBench()
    {
        _maps.CreateMap(out var mapId);
        var user = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, mapId));
        var bench = SEntMan.SpawnEntity("NCCyberwareWorkbench", new MapCoordinates(new Vector2(1f, 0f), mapId));
        var cell = SEntMan.SpawnEntity("PowerCellHigh", SComp<TransformComponent>(user).Coordinates);
        Assert.That(_slots.TryInsert(bench, "nc_bench_cell", cell, null), Is.True);
        var arm = SEntMan.SpawnEntity("NCOrganHumanCyberArmLeft", new MapCoordinates(Vector2.Zero, mapId));
        Assert.That(_hands.TryPickupAnyHand(user, arm), Is.True);
        Assert.That(_cyberware.TryMountAssembly(bench, user, arm), Is.True);
        return (user, bench, arm);
    }
}
