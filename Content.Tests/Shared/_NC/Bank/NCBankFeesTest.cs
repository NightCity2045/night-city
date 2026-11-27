// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Bank;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Bank;

[TestFixture]
[TestOf(typeof(NCBankFees))]
public sealed class NCBankFeesTest
{
    [TestCase(1000, 4000, 400)]
    [TestCase(1000, 2000, 200)]
    [TestCase(1000, 1000, 100)]
    [TestCase(1, 4000, 1)]
    [TestCase(3, 1000, 1)]
    [TestCase(1000, 0, 0)]
    [TestCase(0, 4000, 0)]
    public void FeeIsRoundedUp(long amount, int basisPoints, long expected)
    {
        Assert.That(NCBankFees.Calculate(amount, basisPoints), Is.EqualTo(expected));
    }
}
