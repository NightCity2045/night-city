// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Cyberware.Visuals;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware.Visuals;
using Content.Shared.Body;
using Content.Shared.Hands.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>Gate 7 contracts, compiled only unless the owner explicitly opts into integration execution.</summary>
[TestFixture]
public sealed class NCCyberwareExteriorTest : GameTest
{
    [SidedDependency(Side.Server)] private NCCyberwareExteriorSystem _exterior = default!;
    [SidedDependency(Side.Server)] private NCCyberwareSystem _cyberware = default!;
    [SidedDependency(Side.Server)] private SharedHandsSystem _hands = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;
    [SidedDependency(Side.Server)] private IGameTiming _timing = default!;

    [Test, RunOnSide(Side.Server)]
    public void RealSleeveChangesOnlyTheExterior()
    {
        var (user, part, kit) = Scene();
        var before = SComp<NCOrganIntegrityComponent>(part).CurrentIntegrity;
        Assert.That(_exterior.TryApplyFinish(user, kit, part), Is.True);
        Assert.That(SComp<NCCyberwareExteriorComponent>(part).Finish.Id, Is.EqualTo("NCFinishSyntheticSkin"));
        Assert.That(SComp<NCOrganIntegrityComponent>(part).CurrentIntegrity, Is.EqualTo(before));
        Assert.That(SComp<VisualOrganComponent>(part).Data.RsiPath, Is.EqualTo("Mobs/Species/Human/parts.rsi"));
    }

    [Test, RunOnSide(Side.Server)]
    public void UnheldKitDoesNotApplyAFreeFinish()
    {
        var (user, part, _) = Scene();
        var kit = SEntMan.SpawnEntity("NCCyberwareSyntheticSkinKit", SComp<TransformComponent>(user).Coordinates);
        Assert.That(_exterior.TryApplyFinish(user, kit, part), Is.False);
        Assert.That(SComp<NCCyberwareExteriorComponent>(part).Finish.Id, Is.EqualTo("NCFinishChrome"));
    }

    [Test, RunOnSide(Side.Server)]
    public void OneKitCannotCoatTwoPartsBeforeQueuedDeletion()
    {
        var (user, part, kit) = Scene();
        var second = SEntMan.SpawnEntity("NCOrganHumanCyberArmRight", SComp<TransformComponent>(user).Coordinates);
        Assert.That(_exterior.TryApplyFinish(user, kit, part), Is.True);
        Assert.That(_exterior.TryApplyFinish(user, kit, second), Is.False);
        Assert.That(SComp<NCCyberwareExteriorComponent>(second).Finish.Id, Is.EqualTo("NCFinishChrome"));
    }

    [Test, RunOnSide(Side.Server)]
    public void OutOfReachPartDoesNotConsumeTheHeldKit()
    {
        var (user, _, kit) = Scene();
        var map = SComp<TransformComponent>(user).MapID;
        var far = SEntMan.SpawnEntity("NCOrganHumanCyberArmLeft", new MapCoordinates(new Vector2(10, 0), map));
        Assert.That(_exterior.TryApplyFinish(user, kit, far), Is.False);
        Assert.That(_hands.IsHolding(user, kit), Is.True);
    }

    [Test, RunOnSide(Side.Server)]
    public void RupturedFinishRoundTripsIndependentlyOfMechanicalService()
    {
        var (_, part, _) = Scene();
        var exterior = SComp<NCCyberwareExteriorComponent>(part);
        exterior.Finish = "NCFinishSyntheticSkin";
        exterior.Ruptured = true;
        Assert.That(_cyberware.TryCaptureOrganState(part, out var saved), Is.True);
        exterior.Ruptured = false;
        Assert.That(_cyberware.TryApplyPersistentState(part, saved, _timing.CurTime), Is.True);
        Assert.That(exterior.Ruptured, Is.True);
        Assert.That(exterior.Finish.Id, Is.EqualTo("NCFinishSyntheticSkin"));
    }

    [Test, RunOnSide(Side.Server)]
    public void UnknownFinishIsRejectedBeforeLiveMutation()
    {
        var (_, part, _) = Scene();
        Assert.That(_cyberware.TryCaptureOrganState(part, out var saved), Is.True);
        saved.Exterior!.Finish = "MissingCyberwareFinish";
        Assert.That(_cyberware.TryApplyPersistentState(part, saved, _timing.CurTime), Is.False);
        Assert.That(SComp<NCCyberwareExteriorComponent>(part).Finish.Id, Is.EqualTo("NCFinishChrome"));
    }

    [Test, RunOnSide(Side.Server)]
    public void ExteriorProjectionCannotRepaintAnotherOrgansSharedData()
    {
        var (user, part, _) = Scene();
        var source = new PrototypeLayerData { RsiPath = "Mobs/Species/Human/parts.rsi", State = "l_arm", Color = Color.White };
        var ev = new NCGetOrganVisualAppearanceEvent(source);
        SEntMan.EventBus.RaiseLocalEvent(part, ref ev);
        Assert.That(source.RsiPath, Is.EqualTo("Mobs/Species/Human/parts.rsi"));
        Assert.That(ev.Data, Is.Not.SameAs(source));
        Assert.That(ev.Data.RsiPath, Is.EqualTo("_NC/Mobs/Cyberware/parts.rsi"));
    }

    private (EntityUid User, EntityUid Part, EntityUid Kit) Scene()
    {
        _maps.CreateMap(out var map);
        var coordinates = new MapCoordinates(Vector2.Zero, map);
        var user = SEntMan.SpawnEntity("MobHuman", coordinates);
        var part = SEntMan.SpawnEntity("NCOrganHumanCyberArmLeft", coordinates);
        var kit = SEntMan.SpawnEntity("NCCyberwareSyntheticSkinKit", coordinates);
        Assert.That(_hands.TryPickupAnyHand(user, kit), Is.True);
        return (user, part, kit);
    }
}
