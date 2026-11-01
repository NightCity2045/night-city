// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Network;
using Content.Shared._NC.Meta.Network;
using NUnit.Framework;

namespace Content.Tests.Server._NC.Meta;

[TestFixture]
public sealed class NCMetaNetActionResolverTests
{
    [Test]
    public void CompatibleToolCanPassOrLoseAgainstNativeDefenseWithoutForgingAnOutcome()
    {
        var tool = new NCMetaNetToolProfilePrototype { Rating = 2 };
        var defense = new NCMetaNetDefenseProfilePrototype { Rating = 2, TraceOnSuccess = 1, TraceOnFailure = 3 };
        var passed = NCMetaNetActionResolver.Resolve(tool, defense, NCMetaNetNodeKind.Control, 0);
        Assert.That(passed, Is.EqualTo(new NCMetaNetResolution(true, true, 1, false, false)));
        defense.Rating = 3;
        var lost = NCMetaNetActionResolver.Resolve(tool, defense, NCMetaNetNodeKind.Control, 0);
        Assert.That(lost, Is.EqualTo(new NCMetaNetResolution(true, false, 3, false, false)));
    }

    [Test]
    public void RepeatedNativeAttemptsCrossIceAndBlackIceThresholdsAndTraceIsBounded()
    {
        var tool = new NCMetaNetToolProfilePrototype { Rating = 1 };
        var defense = new NCMetaNetDefenseProfilePrototype { Rating = 3, TraceOnFailure = 3, IceThreshold = 6, BlackIceThreshold = 9 };
        var first = NCMetaNetActionResolver.Resolve(tool, defense, NCMetaNetNodeKind.Defense, 0);
        var second = NCMetaNetActionResolver.Resolve(tool, defense, NCMetaNetNodeKind.Defense, first.Trace);
        var third = NCMetaNetActionResolver.Resolve(tool, defense, NCMetaNetNodeKind.Defense, second.Trace);
        Assert.That(first.Ice, Is.False);
        Assert.That(second.Ice, Is.True); Assert.That(second.BlackIce, Is.False);
        Assert.That(third.BlackIce, Is.True);
        Assert.That(NCMetaNetActionResolver.Resolve(tool, defense, NCMetaNetNodeKind.Defense, 99).Trace, Is.EqualTo(100));
    }

    [Test]
    public void IncompatibleOrMalformedProfileCannotAcquireAccessOrEraseTrace()
    {
        var tool = new NCMetaNetToolProfilePrototype { Rating = 100, CompatibleKinds = [NCMetaNetNodeKind.Defense] };
        var defense = new NCMetaNetDefenseProfilePrototype { Rating = 0 };
        var rejected = NCMetaNetActionResolver.Resolve(tool, defense, NCMetaNetNodeKind.Data, 5);
        Assert.That(rejected.Admitted, Is.False); Assert.That(rejected.Trace, Is.EqualTo(5));
        tool.CompatibleKinds.Add(NCMetaNetNodeKind.Data); defense.TraceOnSuccess = -50;
        rejected = NCMetaNetActionResolver.Resolve(tool, defense, NCMetaNetNodeKind.Data, 5);
        Assert.That(rejected.Admitted, Is.False); Assert.That(rejected.Trace, Is.EqualTo(5));
    }
}
