// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Hosting;
using NUnit.Framework;

namespace Content.Meta.Tests.Hosting;

[TestFixture]
public sealed class MetaOperationalPolicyTests
{
    [Test]
    public void OperatorDenyListIsBoundedCanonicalAndNeverSilentlyDropsMalformedEntries()
    {
        Assert.That(MetaOperationalPolicy.TryParseRevokedArtifacts("", out var empty), Is.True);
        Assert.That(empty, Is.Empty);
        var lower = new string('a', 64);
        Assert.That(MetaOperationalPolicy.TryParseRevokedArtifacts(lower.ToUpperInvariant(), out var hashes), Is.True);
        Assert.That(hashes.Contains(lower), Is.True);
        Assert.That(MetaOperationalPolicy.TryParseRevokedArtifacts(lower + "," + lower.ToUpperInvariant(), out _), Is.False);
        Assert.That(MetaOperationalPolicy.TryParseRevokedArtifacts(lower + ",", out _), Is.False);
        Assert.That(MetaOperationalPolicy.TryParseRevokedArtifacts("bad", out _), Is.False);
        Assert.That(MetaOperationalPolicy.TryParseRevokedArtifacts(new string('x', 1024 * 1024), out _), Is.False);
        Assert.That(MetaOperationalPolicy.TryParseRevokedArtifacts(string.Join(',', Enumerable.Range(0, 64).Select(value => value.ToString("x64"))), out hashes), Is.True);
        Assert.That(hashes.Count, Is.EqualTo(64));
        Assert.That(MetaOperationalPolicy.TryParseRevokedArtifacts(string.Join(',', Enumerable.Range(0, 65).Select(value => value.ToString("x64"))), out _), Is.False);
    }
}
