// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Hosting;
using NUnit.Framework;

namespace Content.Meta.Tests.Hosting;

[TestFixture]
public sealed class MetaAuthorityTests
{
    [TestCase("/server.cfg")]
    [TestCase("C:/server.cfg")]
    [TestCase("../secret")]
    [TestCase("config//mode")]
    [TestCase("config/./mode")]
    [TestCase("config/../mode")]
    [TestCase("config\\mode")]
    public void HostPathsAreRejected(string path) => Assert.That(MetaPaths.IsValid(path), Is.False);

    [Test]
    public void PrefixUsesSegmentBoundary()
    {
        Assert.That(MetaPaths.IsWithin("config/mode.txt", "config"), Is.True);
        Assert.That(MetaPaths.IsWithin("configuration/mode.txt", "config"), Is.False);
    }

    [Test]
    public void PermissionIsIntersectionAndRecheckedAgainstAllEpochs()
    {
        var generation = Guid.NewGuid();
        var grant = new MetaGrant(new MetaAuthorityStamp(Guid.NewGuid(), generation, "owner", "door", 3, 5, 1000),
            ["door.read", "door.control"], ["door.read"], ["door.read", "door.control"]);
        Assert.That(grant.CanUse("door.read", generation, "door", 3, 5, 10), Is.True);
        Assert.That(grant.CanUse("door.control", generation, "door", 3, 5, 10), Is.False);
        Assert.That(grant.CanUse("door.read", generation, "door", 3, 6, 10), Is.False);
        Assert.That(grant.CanUse("door.read", Guid.NewGuid(), "door", 3, 5, 10), Is.False);
        Assert.That(grant.CanUse("door.read", generation, "door", 3, 5, 1000), Is.False);
        grant.Revoke();
        Assert.That(grant.CanUse("door.read", generation, "door", 3, 5, 10), Is.False);
    }

    [Test]
    public void ClockRegressionDoesNotRefillRateLimit()
    {
        var quota = new MetaRateLimit(2, 1);
        Assert.That(quota.TryConsume(0), Is.True);
        Assert.That(quota.TryConsume(0), Is.True);
        Assert.That(quota.TryConsume(0), Is.False);
        Assert.That(quota.TryConsume(1000), Is.True);
        Assert.That(quota.TryConsume(500), Is.False);
    }
}
