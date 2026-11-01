// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Hosting;
using NUnit.Framework;

namespace Content.Meta.Tests.Hosting;

[TestFixture]
public sealed class MetaSharedRateLimitTests
{
    [Test]
    public void SwitchingOwnersAndDecksCannotRefillTheSameActor()
    {
        var actor = new MetaRateLimit(10, 1);
        Assert.That(MetaRateLimit.TryConsumeAll(0, 10, [new(10, 1), actor, new(10, 1)]), Is.True);
        Assert.That(MetaRateLimit.TryConsumeAll(0, 1, [new(10, 1), actor, new(10, 1)]), Is.False);
        Assert.That(MetaRateLimit.TryConsumeAll(999, 1, [new(10, 1), actor, new(10, 1)]), Is.False);
        Assert.That(MetaRateLimit.TryConsumeAll(1000, 1, [new(10, 1), actor, new(10, 1)]), Is.True);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void RefusedHierarchyDoesNotConsumeOtherScopes(int exhausted)
    {
        MetaRateLimit[] limits = [new(2, 1), new(2, 1), new(2, 1)];
        Assert.That(limits[exhausted].TryConsume(0, 2), Is.True);
        Assert.That(MetaRateLimit.TryConsumeAll(0, 1, limits), Is.False);
        for (var index = 0; index < limits.Length; index++)
            if (index != exhausted) Assert.That(limits[index].TryConsume(0, 2), Is.True);
    }

    [Test]
    public void PreviewDoesNotSpendCreditAndDuplicateScopeIsRejectedWithoutMutation()
    {
        var scope = new MetaRateLimit(1, 1);
        Assert.That(scope.CanConsume(0), Is.True);
        Assert.That(scope.CanConsume(0), Is.True);
        Assert.That(MetaRateLimit.TryConsumeAll(0, 1, [scope, scope]), Is.False);
        Assert.That(scope.TryConsume(0), Is.True);
        Assert.That(scope.CanConsume(-1), Is.False);
        Assert.That(scope.CanConsume(500), Is.False);
        Assert.That(scope.CanConsume(1000), Is.True);
    }
}
