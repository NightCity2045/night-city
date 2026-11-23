// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using Content.Server._NC.Meta.Devices;
using NUnit.Framework;

namespace Content.Tests._NC.Meta;

[TestFixture]
[TestOf(typeof(NCMetaDeviceIds))]
public sealed class NCMetaDeviceIdsTest
{
    [Test]
    public void ARandomCodeHasSixSymbolsOfTheAlphabet()
    {
        for (var i = 0; i < 200; i++)
            Assert.That(NCMetaDeviceIds.IsShort(NCMetaDeviceIds.Random()), Is.True);
    }

    [Test]
    public void ACodeFromAKeyIsStableAndDependsOnTheKey()
    {
        Assert.That(NCMetaDeviceIds.FromKey("meta-device:item:1"), Is.EqualTo(NCMetaDeviceIds.FromKey("meta-device:item:1")));
        Assert.That(NCMetaDeviceIds.FromKey("meta-device:item:1"), Is.Not.EqualTo(NCMetaDeviceIds.FromKey("meta-device:item:2")));
    }

    [Test]
    public void LegacyGuidsStayValidButShortCodesAreNotGuids()
    {
        var guid = Guid.NewGuid().ToString("D");
        Assert.That(NCMetaDeviceIds.IsValid(guid), Is.True);
        Assert.That(NCMetaDeviceIds.IsShort(guid), Is.False);
        Assert.That(NCMetaDeviceIds.IsValid("not-an-id"), Is.False);
        Assert.That(NCMetaDeviceIds.IsValid("K7Q4M"), Is.False, "Too short.");
        Assert.That(NCMetaDeviceIds.IsValid("K7Q4MU"), Is.False, "U is not in the alphabet.");
    }

    [Test]
    public void PeopleMayTypeCodesLooselyAndGetTheCanonicalForm()
    {
        Assert.That(NCMetaDeviceIds.TryNormalize(" k7q-4md ", out var id), Is.True);
        Assert.That(id, Is.EqualTo("K7Q4MD"));
        Assert.That(NCMetaDeviceIds.Format(id), Is.EqualTo("K7Q-4MD"));
        Assert.That(NCMetaDeviceIds.TryNormalize("O0I1L1", out var lookalike), Is.True);
        Assert.That(lookalike, Is.EqualTo("001111"));
        Assert.That(NCMetaDeviceIds.TryNormalize("K7Q", out _), Is.False);
    }

    [Test]
    public void EveryCodeMapsToAStableGuidForGuidKeyedSystems()
    {
        Assert.That(NCMetaDeviceIds.TryToGuid("K7Q4MD", out var first), Is.True);
        Assert.That(NCMetaDeviceIds.TryToGuid("K7Q4MD", out var second), Is.True);
        Assert.That(first, Is.EqualTo(second));
        Assert.That(NCMetaDeviceIds.TryToGuid("K7Q4ME", out var other), Is.True);
        Assert.That(other, Is.Not.EqualTo(first));
        var legacy = Guid.NewGuid();
        Assert.That(NCMetaDeviceIds.TryToGuid(legacy.ToString("D"), out var same), Is.True);
        Assert.That(same, Is.EqualTo(legacy));
    }
}
