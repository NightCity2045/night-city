// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Hosting;
using NUnit.Framework;

namespace Content.Meta.Tests.Hosting;

[TestFixture]
public sealed class MetaMemoryAdmissionTests
{
    [Test]
    public void DefaultEmbeddedShareLeavesNativeHeadroomWithoutIncreasingPhysicalRam()
    {
        Assert.That(MetaMemoryAdmission.TryPlan(262144, 1, 65536, 32768, 0, 0, out var guest), Is.True);
        Assert.That(guest, Is.EqualTo(163840));
        Assert.That(guest + 65536 + 32768, Is.EqualTo(262144));
        Assert.That(MetaMemoryAdmission.TryPlan(65536, 1, 16384, 32768, 0, 0, out _), Is.False);
    }
    [Test]
    public void ExplicitGuestLimitIsHonoredAndExistingReservationsStillBoundTheSum()
    {
        Assert.That(MetaMemoryAdmission.TryPlan(1048576, 1, 65536, 32768, 131072, 655360, out var guest), Is.True);
        Assert.That(guest, Is.EqualTo(131072));
        Assert.That(MetaMemoryAdmission.TryPlan(1048576, 1, 65536, 32768, 524288, 655360, out _), Is.False);
        Assert.That(MetaMemoryAdmission.TryPlan(1048576, 1, 65536, 131072, 32768, 0, out _), Is.False);
    }
}
