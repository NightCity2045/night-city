// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.CityServices;
using NUnit.Framework;
using System;

namespace Content.Tests.Server._NC.LivingCity.CityServices;

[TestFixture]
public sealed class NCCityServiceArrivalPolicyTest
{
    [Test]
    public void ArrivalProbeIsFailClosedByDefault()
    {
        var snapshot = new NCServiceDispatchSnapshot(
            new(Guid.NewGuid()),
            new(Guid.NewGuid()),
            new(Guid.NewGuid()),
            null,
            default,
            NCServiceDispatchStatus.EnRoute,
            default,
            default,
            default,
            0,
            [],
            new(1),
            new(2),
            null,
            string.Empty,
            1,
            Guid.NewGuid());
        var dispatch = new NCServiceDispatchRecord(snapshot, 1, default, new(1));
        var probe = new NCServiceDispatchArrivalAttemptEvent(dispatch, new(2));

        Assert.That(probe.Confirmed, Is.False);
        probe.RetryAfter = TimeSpan.FromMinutes(5);
        Assert.That(probe.Confirmed, Is.False, "Requesting a retry must not implicitly confirm physical arrival.");
    }

    [Test]
    public void UnconfirmedArrivalRetryUsesServicePolicyBounds()
    {
        var minimum = TimeSpan.FromMinutes(1);
        var maximum = TimeSpan.FromMinutes(30);

        Assert.Multiple(() =>
        {
            Assert.That(NCCityServiceSystem.GetArrivalRetryDelay(TimeSpan.Zero, minimum, maximum),
                Is.EqualTo(minimum));
            Assert.That(NCCityServiceSystem.GetArrivalRetryDelay(TimeSpan.FromSeconds(1), minimum, maximum),
                Is.EqualTo(minimum));
            Assert.That(NCCityServiceSystem.GetArrivalRetryDelay(TimeSpan.FromMinutes(5), minimum, maximum),
                Is.EqualTo(TimeSpan.FromMinutes(5)));
            Assert.That(NCCityServiceSystem.GetArrivalRetryDelay(TimeSpan.FromHours(1), minimum, maximum),
                Is.EqualTo(maximum));
        });
    }
}
