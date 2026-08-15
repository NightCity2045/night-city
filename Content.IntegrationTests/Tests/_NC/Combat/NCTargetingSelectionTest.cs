// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Combat.Targeting;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Combat;

[TestFixture]
[TestOf(typeof(NCTargetingSelectionSystem))]
public sealed class NCTargetingSelectionTest : GameTest
{
    private static readonly EntProtoId HumanMob = "MobHuman";
    private static readonly ProtoId<NCTargetZonePrototype> Head = "NCZoneHead";

    [SidedDependency(Side.Server)] private NCTargetingSelectionSystem _targeting = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void HumanSelectionIsValidatedNormalizedAndPrivate()
    {
        var human = SSpawn(HumanMob);

        Assert.Multiple(() =>
        {
            Assert.That(SEntMan.HasComponent<NCTargetingSelectionComponent>(human), Is.False);
            Assert.That(_targeting.GetSelection(human),
                Is.EqualTo(new NCTargetingState(NCTargetingMode.Automatic, null)));
        });

        Assert.That(
            _targeting.TrySetSelection(
                human,
                new NCTargetingState(NCTargetingMode.SelectedZone, Head),
                out var authoritative,
                out var failure),
            Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.EqualTo(NCTargetingSelectionFailure.None));
            Assert.That(authoritative, Is.EqualTo(new NCTargetingState(NCTargetingMode.SelectedZone, Head)));
            Assert.That(SEntMan.HasComponent<NCTargetingSelectionComponent>(human), Is.True);
        });

        // Invalid input cannot mutate the last accepted private selection.
        Assert.That(
            _targeting.TrySetSelection(
                human,
                new NCTargetingState(NCTargetingMode.SelectedZone, null),
                out authoritative,
                out failure),
            Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.EqualTo(NCTargetingSelectionFailure.ZoneRequired));
            Assert.That(authoritative, Is.EqualTo(new NCTargetingState(NCTargetingMode.SelectedZone, Head)));
            Assert.That(_targeting.GetSelection(human),
                Is.EqualTo(new NCTargetingState(NCTargetingMode.SelectedZone, Head)));
        });

        Assert.That(
            _targeting.TrySetSelection(
                human,
                new NCTargetingState(NCTargetingMode.SelectedZone, "NotAHumanTarget"),
                out authoritative,
                out failure),
            Is.False);
        Assert.That(failure, Is.EqualTo(NCTargetingSelectionFailure.ZoneNotInProfile));

        Assert.That(
            _targeting.TrySetSelection(
                human,
                new NCTargetingState((NCTargetingMode) byte.MaxValue, Head),
                out authoritative,
                out failure),
            Is.False);
        Assert.That(failure, Is.EqualTo(NCTargetingSelectionFailure.InvalidMode));

        // Automatic mode deliberately discards any client-supplied target.
        Assert.That(
            _targeting.TrySetSelection(
                human,
                new NCTargetingState(NCTargetingMode.Automatic, Head),
                out authoritative,
                out failure),
            Is.True);
        Assert.That(authoritative,
            Is.EqualTo(new NCTargetingState(NCTargetingMode.Automatic, null)));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void SelectionDescribesFutureVictimNotShootersCurrentOrgans()
    {
        var user = SSpawn(HumanMob);

        // Targeting intent belongs to the anatomy profile. Organ existence is checked later on the actual victim.
        SEntMan.RemoveComponent<BodyComponent>(user);

        Assert.That(
            _targeting.TrySetSelection(
                user,
                new NCTargetingState(NCTargetingMode.SelectedZone, Head),
                out var authoritative,
                out var failure),
            Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.EqualTo(NCTargetingSelectionFailure.None));
            Assert.That(authoritative, Is.EqualTo(new NCTargetingState(NCTargetingMode.SelectedZone, Head)));
        });

        var entityWithoutAnatomy = SSpawn(null);
        Assert.That(
            _targeting.TrySetSelection(
                entityWithoutAnatomy,
                new NCTargetingState(NCTargetingMode.SelectedZone, Head),
                out _,
                out failure),
            Is.False);
        Assert.That(failure, Is.EqualTo(NCTargetingSelectionFailure.AnatomyUnavailable));
    }
}
