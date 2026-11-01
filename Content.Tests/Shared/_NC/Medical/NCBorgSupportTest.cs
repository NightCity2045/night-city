// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Cyberware.Borg;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCBorgSupportTest
{
    [TestCase(10f, 2f, 10f, 5f)]
    [TestCase(10f, 2f, 3f, 3f)]
    [TestCase(0f, 2f, 10f, 0f)]
    [TestCase(-1f, 2f, 10f, 0f)]
    [TestCase(10f, 0f, 10f, 0f)]
    [TestCase(float.NaN, 2f, 10f, 0f)]
    [TestCase(10f, 2f, float.PositiveInfinity, 0f)]
    public void FiniteSupplyCannotPowerAnUnfundedInterval(float reserve, float rate, float interval, float expected)
        => Assert.That(NCBorgSupport.SuppliedSeconds(reserve, rate, interval), Is.EqualTo(expected));
}
