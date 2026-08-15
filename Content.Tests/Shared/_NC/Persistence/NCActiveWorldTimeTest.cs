// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Content.Shared._NC.Persistence.Time;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Persistence;

[TestFixture]
[TestOf(typeof(NCActiveWorldTime))]
public sealed class NCActiveWorldTimeTest
{
    [Test]
    public void ActiveTimeAdvancesOnlyByExplicitElapsedDuration()
    {
        var start = NCActiveWorldTime.Zero.Advance(TimeSpan.FromMinutes(15));
        var end = start.Advance(TimeSpan.FromSeconds(30));

        Assert.Multiple(() =>
        {
            Assert.That(start.Elapsed, Is.EqualTo(TimeSpan.FromMinutes(15)));
            Assert.That(end.Elapsed, Is.EqualTo(TimeSpan.FromMinutes(15.5)));
            Assert.That(end.ElapsedSince(start), Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(end.CompareTo(start), Is.GreaterThan(0));
        });
    }

    [Test]
    public void ActiveTimeRejectsBackwardsMovement()
    {
        var current = NCActiveWorldTime.Zero.Advance(TimeSpan.FromMinutes(1));

        Assert.Multiple(() =>
        {
            Assert.That(() => current.Advance(TimeSpan.FromTicks(-1)), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => NCActiveWorldTime.Zero.ElapsedSince(current), Throws.InstanceOf<ArgumentOutOfRangeException>());
        });
    }
}
