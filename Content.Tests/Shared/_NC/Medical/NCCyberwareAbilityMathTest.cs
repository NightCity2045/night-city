// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Content.Shared._NC.Medical.Cyberware.Abilities;
using Content.Shared.Movement.Systems;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCCyberwareAbilityMathTest
{
    [TestCase(36f, 0f, 36f)]
    [TestCase(36f, 6f, 30f)]
    [TestCase(36f, 35f, 1f)]
    [TestCase(36f, 36f, 0f)]
    [TestCase(36f, 60f, 0f)]
    public void CooldownUsesElapsedOnlineTime(float initial, float elapsed, float expected)
        => Assert.That(NCCyberwareAbilityMath.Remaining(initial, TimeSpan.Zero, TimeSpan.FromSeconds(elapsed)), Is.EqualTo(expected));

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(-1f)]
    [TestCase(86401f)]
    public void InvalidPersistedCooldownIsRejected(float value)
        => Assert.That(NCCyberwareAbilityMath.ValidCooldown(value), Is.False);

    [Test]
    public void RestoringRemainingTimeAtANewClockDoesNotAdvanceOfflineTime()
    {
        var saved = NCCyberwareAbilityMath.Remaining(36, TimeSpan.Zero, TimeSpan.FromSeconds(14));
        var restoredAt = TimeSpan.FromDays(30);
        Assert.That(NCCyberwareAbilityMath.Remaining(saved, restoredAt, restoredAt), Is.EqualTo(22));
        Assert.That(NCCyberwareAbilityMath.Remaining(saved, restoredAt, restoredAt + TimeSpan.FromSeconds(1)), Is.EqualTo(21));
    }

    [Test]
    public void ClockRewindCannotCreateANegativeOrLongerCooldown()
        => Assert.That(NCCyberwareAbilityMath.Remaining(20, TimeSpan.FromSeconds(50), TimeSpan.Zero), Is.EqualTo(20));

    [TestCase(0.75f, 0.25f, 0.8125f)]
    [TestCase(0.5f, 0.25f, 0.625f)]
    [TestCase(1f, 0.25f, 1f)]
    [TestCase(0.75f, 0f, 0.75f)]
    public void StabilizationReducesOnlyTheExistingPenalty(float multiplier, float reduction, float expected)
        => Assert.That(NCCyberwareAbilityMath.RestoreMovingPenalty(multiplier, reduction), Is.EqualTo(expected).Within(0.00001f));

    [TestCase(true)]
    [TestCase(false)]
    public void FinalSpeedLimitSurvivesOtherProviderOrdering(bool burstFirst)
    {
        var args = new RefreshMovementSpeedModifiersEvent();
        if (burstFirst) { args.ModifySpeed(1.25f); args.LimitSpeed(4.5f, 6f); }
        args.ModifySpeed(2f);
        if (!burstFirst) { args.ModifySpeed(1.25f); args.LimitSpeed(4.5f, 6f); }
        args.ClampToBaseSpeeds(2.5f, 4.5f);
        Assert.That(args.WalkSpeedModifier * 2.5f, Is.EqualTo(4.5f).Within(0.00001f));
        Assert.That(args.SprintSpeedModifier * 4.5f, Is.EqualTo(6f).Within(0.00001f));
    }

    [Test]
    public void MotorAssistancePreservesExistingInjurySlowdown()
    {
        var args = new RefreshMovementSpeedModifiersEvent();
        args.ModifySpeed(0.5f);
        args.ModifySpeed(1.25f);
        args.LimitSpeed(4.5f, 6f);
        args.ClampToBaseSpeeds(2.5f, 4.5f);
        Assert.That(args.SprintSpeedModifier, Is.EqualTo(0.625f));
    }

    [Test]
    public void UnaugmentedEventRetainsOrdinaryModifierBehavior()
    {
        var args = new RefreshMovementSpeedModifiersEvent();
        args.ModifySpeed(2f);
        args.ClampToBaseSpeeds(2.5f, 4.5f);
        Assert.That(args.SprintSpeedModifier, Is.EqualTo(2f));
    }
}
