// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using NUnit.Framework;

namespace Content.Tests.Server._NC.LivingCity.Actors;

[TestFixture]
public sealed class NCPhysicalActorSpawnFallbackPolicyTest
{
    [Test]
    public void ValidStaleCityZoneWithoutResolvableCityCandidatesUsesStationFallback()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NCPhysicalActorLifecycleSystem.RequiresStationFallbackAfterCitySearch(0, false), Is.True);
            Assert.That(NCPhysicalActorLifecycleSystem.RequiresStationFallbackAfterCitySearch(1, false), Is.True);
            Assert.That(NCPhysicalActorLifecycleSystem.RequiresStationFallbackAfterCitySearch(1, true), Is.False);
        });
    }
}
