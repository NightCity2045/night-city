// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.IntegrationTests.Fixtures;
using Content.Server.Database;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Hardening;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(NCPersistenceTelemetry))]
public sealed class NCLivingCityHardeningRuntimeTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test]
    public void PersistenceTelemetryRecordsReadLatency()
    {
        var before = NCPersistenceTelemetry.Snapshot();
        NCPersistenceTelemetry.Record(false, TimeSpan.FromMilliseconds(5), null, false);
        var after = NCPersistenceTelemetry.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(after.Reads, Is.GreaterThanOrEqualTo(before.Reads + 1));
            Assert.That(after.LatencySamples, Is.GreaterThanOrEqualTo(1));
            Assert.That(after.LatencyP99Milliseconds, Is.GreaterThanOrEqualTo(0));
        });
    }
}
