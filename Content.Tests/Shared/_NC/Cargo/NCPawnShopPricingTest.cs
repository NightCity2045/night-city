// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Cargo.Pawn;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Cargo;

[TestFixture]
public sealed class NCPawnShopPricingTest
{
    [TestCase(100, 30, 30)]
    [TestCase(100, 15, 15)]
    [TestCase(100, 45, 45)]
    [TestCase(19, 15, 2)]
    [TestCase(19, 45, 8)]
    public void MerchantRateRoundsDown(double appraisal, int percentage, long expected)
    {
        Assert.That(NCPawnShopPricing.TryGetPayout(appraisal, percentage, out var payout), Is.True);
        Assert.That(payout, Is.EqualTo(expected));
    }

    [TestCase(double.NaN, 30)]
    [TestCase(double.PositiveInfinity, 30)]
    [TestCase(double.MaxValue, 45)]
    [TestCase(-100, 30)]
    [TestCase(0, 30)]
    [TestCase(1, 15)]
    [TestCase(100, 0)]
    [TestCase(100, 101)]
    public void InvalidOrUnpayableAppraisalsCannotIssueMoney(double appraisal, int percentage)
    {
        Assert.That(NCPawnShopPricing.TryGetPayout(appraisal, percentage, out var payout), Is.False);
        Assert.That(payout, Is.Zero);
    }
}
