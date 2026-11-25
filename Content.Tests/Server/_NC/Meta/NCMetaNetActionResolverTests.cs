// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Network;
using Content.Shared._NC.Meta.Network;
using NUnit.Framework;

#pragma warning disable RA0039 // Unit tests build data objects directly, without loading a server.
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
        Assert.That(passed, Is.EqualTo(new NCMetaNetResolution(true, true, 1)));
        defense.Rating = 3;
        var lost = NCMetaNetActionResolver.Resolve(tool, defense, NCMetaNetNodeKind.Control, 0);
        Assert.That(lost, Is.EqualTo(new NCMetaNetResolution(true, false, 3)));
    }

    [Test]
    public void RepeatedFailuresRaiseTraceAndTraceIsBounded()
    {
        var tool = new NCMetaNetToolProfilePrototype { Rating = 1 };
        var defense = new NCMetaNetDefenseProfilePrototype { Rating = 3, TraceOnFailure = 3 };
        var first = NCMetaNetActionResolver.Resolve(tool, defense, NCMetaNetNodeKind.Defense, 0);
        var second = NCMetaNetActionResolver.Resolve(tool, defense, NCMetaNetNodeKind.Defense, first.Trace);
        Assert.That(first.Trace, Is.EqualTo(3));
        Assert.That(second.Trace, Is.EqualTo(6));
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
