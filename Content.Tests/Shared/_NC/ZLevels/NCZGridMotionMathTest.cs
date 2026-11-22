// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Content.Shared._NC.ZLevels.Core;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.ZLevels;

[TestFixture]
public sealed class NCZGridMotionMathTest
{
    [TestCase(1, -1, 0f)]
    [TestCase(1, 0, 0f)]
    [TestCase(1, 2, 0.5f)]
    [TestCase(1, 4, 1f)]
    [TestCase(1, 5, 1f)]
    [TestCase(-1, -1, 0f)]
    [TestCase(-1, 2, -0.5f)]
    [TestCase(-1, 4, -1f)]
    [TestCase(-1, 5, -1f)]
    public void ProgressStaysBetweenDepartureAndArrival(int direction, int elapsed, float expected)
    {
        var start = TimeSpan.FromSeconds(10);
        var offset = NCZGridMotionMath.Offset(start, start + TimeSpan.FromSeconds(4),
            start + TimeSpan.FromSeconds(elapsed), direction);
        Assert.That(offset, Is.EqualTo(expected).Within(0.0001f));
    }

    [TestCase(1)]
    [TestCase(-1)]
    public void FinishedInstantTripIsAtDestination(int direction)
    {
        var now = TimeSpan.FromSeconds(10);
        Assert.That(NCZGridMotionMath.Offset(now, now, now, direction), Is.EqualTo((float) direction));
    }
}
