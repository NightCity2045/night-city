// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.IO;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Persistence;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Paper;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>Gate 8 contracts. Execution remains opt-in; a YAML round trip is not a real process restart.</summary>
[TestFixture]
public sealed class NCCyberwareServiceTest : GameTest
{
    [SidedDependency(Side.Server)] private NCCyberwareSystem _cyberware = default!;
    [SidedDependency(Side.Server)] private NCMedicalPersistenceSystem _medical = default!;
    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private SharedHandsSystem _hands = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _slots = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;
    [SidedDependency(Side.Server)] private MapLoaderSystem _loader = default!;
    [SidedDependency(Side.Server)] private IGameTiming _timing = default!;
    [SidedDependency(Side.Server)] private Content.Shared.Interaction.SharedInteractionSystem _interaction = default!;

    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test, RunOnSide(Side.Server)]
    public void BenchCellUsesTheOrdinaryHeldItemInteraction()
    {
        _maps.CreateMap(out var map);
        var coordinates = new MapCoordinates(Vector2.Zero, map);
        var user = SEntMan.SpawnEntity("MobHuman", coordinates);
        var bench = SEntMan.SpawnEntity("NCCyberwareWorkbench", coordinates);
        var cell = SEntMan.SpawnEntity("PowerCellHigh", coordinates);
        Assert.That(_hands.TryPickupAnyHand(user, cell), Is.True);
        _interaction.UserInteraction(user, SComp<TransformComponent>(bench).Coordinates, bench, altInteract: false);
        Assert.That(_slots.TryGetSlot(bench, "nc_bench_cell", out var slot), Is.True);
        Assert.That(slot.Item, Is.EqualTo(cell));
    }

    [Test, RunOnSide(Side.Server)]
    public void FlashRequiresHeldMediaAndRejectsTheOldRevision()
    {
        var (user, bench, arm) = Scene();
        var card = SEntMan.SpawnEntity("NCCyberwareServiceLockCard", SComp<TransformComponent>(user).Coordinates);
        var revision = SComp<NCCyberwareComponent>(arm).Service.Revision;
        Assert.That(_cyberware.TryFlashFirmware(bench, user, card, arm, revision), Is.False);
        Assert.That(_hands.TryPickupAnyHand(user, card), Is.True);
        Assert.That(_cyberware.TryFlashFirmware(bench, user, card, arm, revision), Is.True);
        Assert.That(_cyberware.FirmwareAllowsActiveModes(arm), Is.False);
        Assert.That(_cyberware.TryFlashFirmware(bench, user, card, arm, revision), Is.False);
    }

    [Test, RunOnSide(Side.Server)]
    public void ServiceReportUsesHeldBlankPaperAndDoesNotBecomeAnAuthorization()
    {
        var (user, bench, arm) = Scene();
        var paper = SEntMan.SpawnEntity("Paper", SComp<TransformComponent>(user).Coordinates);
        Assert.That(_cyberware.TryWriteServiceDocument(bench, user, paper), Is.False);
        Assert.That(_hands.TryPickupAnyHand(user, paper), Is.True);
        Assert.That(_cyberware.TryWriteServiceDocument(bench, user, paper), Is.True);
        Assert.That(SComp<PaperComponent>(paper).Content, Does.Contain(SComp<NCCyberwareComponent>(arm).StableInstanceId.ToString("D")));
        Assert.That(SComp<PaperComponent>(paper).EditingDisabled, Is.False);
        Assert.That(_cyberware.TryWriteServiceDocument(bench, user, paper), Is.False);
    }

    [Test, RunOnSide(Side.Server)]
    public void SoldImplantCannotBeRestoredFromThePreviousOwnersOldSnapshot()
    {
        var (source, _, _) = Scene();
        var destination = SEntMan.SpawnEntity("MobHuman", SComp<TransformComponent>(source).Coordinates);
        var module = SEntMan.SpawnEntity("NCImplantNeuralLinkMk1", SComp<TransformComponent>(source).Coordinates);
        Assert.That(_anatomy.TryGetOrgan(source, "Head", out var head), Is.True);
        Assert.That(_cyberware.TryInstall(new NCCyberwareInstallRequest(NCActionResolutionId.New(), source,
            head, module, "NCSlotNeuralCore", true, true)).Accepted, Is.True);
        var previousOwner = _medical.SerializeMedicalState(source);
        Assert.That(previousOwner, Is.Not.Null);
        var identity = SComp<NCCyberwareComponent>(module).StableInstanceId;
        Assert.That(_cyberware.TryRemove(new NCCyberwareRemoveRequest(NCActionResolutionId.New(), source, module)).Accepted, Is.True);
        Assert.That(_anatomy.TryGetOrgan(destination, "Head", out var nextHead), Is.True);
        Assert.That(_cyberware.TryInstall(new NCCyberwareInstallRequest(NCActionResolutionId.New(), destination,
            nextHead, module, "NCSlotNeuralCore")).Accepted, Is.True);
        Assert.That(_medical.RestoreMedicalState(source, previousOwner!), Is.False);
        Assert.That(SComp<NCCyberwareComponent>(module).StableInstanceId, Is.EqualTo(identity));
        Assert.That(_cyberware.TryGetOwningBody(module, out var owner) && owner == destination, Is.True);
    }

    [Test, RunOnSide(Side.Server)]
    public void LooseItemYamlPreservesConfigurationServiceHeatAndIdentity()
    {
        var (user, bench, arm) = Scene();
        Assert.That(_cyberware.TryEjectAssembly(bench, user, arm), Is.True);
        Assert.That(_hands.TryDrop(user, arm), Is.True);
        var hardware = SComp<NCCyberwareComponent>(arm);
        hardware.Heat = 19f;
        NCCyberwareServiceHistory.TryAppend(hardware.Service, hardware.Service.Revision, Guid.NewGuid(),
            NCCyberwareServiceOperation.Repaired, "1:18");
        var identity = hardware.StableInstanceId;
        var revision = hardware.Service.Revision;
        using var writer = new StringWriter();
        Assert.That(_loader.TrySaveGeneric([arm], writer, out _, SerializationOptions.Default with
        {
            MissingEntityBehaviour = MissingEntityBehaviour.Ignore, ErrorOnOrphan = false,
        }), Is.True);
        SEntMan.DeleteEntity(arm);
        using var reader = new StringReader(writer.ToString());
        Assert.That(_loader.TryLoadGeneric(reader, "cyberware Gate 8 contract", out var result, MapLoadOptions.Default), Is.True);
        Assert.That(result.Orphans, Has.Count.EqualTo(1));
        foreach (var root in result.Orphans)
        {
            var restored = SComp<NCCyberwareComponent>(root);
            Assert.That(restored.StableInstanceId, Is.EqualTo(identity));
            Assert.That(restored.Heat, Is.EqualTo(19f));
            Assert.That(restored.Service.Revision, Is.EqualTo(revision));
            Assert.That(restored.Service.History[^1].Owner, Is.EqualTo("1:18"));
        }
    }

    [Test, RunOnSide(Side.Server)]
    public void WorldClockUsesSavedRemainderInsteadOfPreviousServerUptime()
    {
        var (_, _, arm) = Scene();
        var hardware = SComp<NCCyberwareComponent>(arm);
        var stamp = SEntMan.EnsureComponent<NCCyberwareWorldSnapshotComponent>(arm);
        stamp.Saved = true;
        stamp.CapturedAt = TimeSpan.FromSeconds(10000);
        hardware.PersistenceCapturedAt = stamp.CapturedAt;
        hardware.EmpDisabledUntil = TimeSpan.FromSeconds(10013);
        Assert.That(_cyberware.TryRestoreWorldEntity((arm, stamp)), Is.True);
        Assert.That(hardware.EmpDisabledUntil - _timing.CurTime, Is.EqualTo(TimeSpan.FromSeconds(13)));
        var deadline = hardware.EmpDisabledUntil;
        Assert.That(_cyberware.TryRestoreWorldEntity((arm, stamp)), Is.True);
        Assert.That(hardware.EmpDisabledUntil, Is.EqualTo(deadline));
    }

    private (EntityUid User, EntityUid Bench, EntityUid Arm) Scene()
    {
        _maps.CreateMap(out var map);
        var coordinates = new MapCoordinates(Vector2.Zero, map);
        var user = SEntMan.SpawnEntity("MobHuman", coordinates);
        var bench = SEntMan.SpawnEntity("NCCyberwareWorkbench", coordinates);
        var cell = SEntMan.SpawnEntity("PowerCellHigh", coordinates);
        Assert.That(_slots.TryInsert(bench, "nc_bench_cell", cell, null), Is.True);
        var arm = SEntMan.SpawnEntity("NCOrganHumanCyberArmLeft", coordinates);
        Assert.That(_hands.TryPickupAnyHand(user, arm), Is.True);
        Assert.That(_cyberware.TryMountAssembly(bench, user, arm), Is.True);
        return (user, bench, arm);
    }
}
