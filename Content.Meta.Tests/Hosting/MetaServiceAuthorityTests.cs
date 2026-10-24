// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Hosting;
using NUnit.Framework;

namespace Content.Meta.Tests.Hosting;

[TestFixture]
public sealed class MetaServiceAuthorityTests
{
    [Test]
    public void PolicyExpiryAlsoBoundsAZeroLinkUnattendedProcess()
    {
        var policy = Policy() with { Grants = [], ExpiresAtTicks = 800 };
        Assert.That(MetaServiceAuthority.IsValid(policy), Is.True);
        Assert.That(MetaServiceAuthority.EarliestExpiry(policy), Is.EqualTo(800));
        Assert.That(MetaServiceAuthority.IsValid(policy with { ExpiresAtTicks = 100 }), Is.False);
        var linked = Policy() with { ExpiresAtTicks = 800 };
        Assert.That(MetaServiceAuthority.EarliestExpiry(linked), Is.EqualTo(800));
        Assert.That(MetaServiceAuthority.Allows(linked, Grant, 3, "door", Target, new('b', 64),
            "meta.doors.Door.setOpen", "Door.Control", 12, 800), Is.False);
    }
    private static readonly Guid Installation = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Grant = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private const string Host = "00000000-0000-0000-0000-000000000003";
    private const string Target = "00000000-0000-0000-0000-000000000004";
    private const string Principal = "organization:00000000-0000-0000-0000-000000000005";
    private static MetaServiceGrantPolicy Policy() => new(Installation, Host, Principal, new('a', 64), 8, true,
        [new(Grant, "door", Target, new('b', 64), ["meta.doors.Door.setOpen"], ["Door.Control"], 12, 1000)],
        "character:7", 100, 3);

    [Test]
    public void AServiceGrantBindsInstallationPrincipalImageHostAndUnattendedConsent()
    {
        var policy = Policy();
        Assert.That(MetaServiceAuthority.IsValid(policy), Is.True);
        Assert.That(MetaServiceAuthority.MatchesInstallation(policy, Installation, Host, Principal, new('a', 64), 8, true), Is.True);
        Assert.That(MetaServiceAuthority.MatchesInstallation(policy, Guid.NewGuid(), Host, Principal, new('a', 64), 8, true), Is.False);
        Assert.That(MetaServiceAuthority.MatchesInstallation(policy, Installation, Target, Principal, new('a', 64), 8, true), Is.False);
        Assert.That(MetaServiceAuthority.MatchesInstallation(policy, Installation, Host, "character:7", new('a', 64), 8, true), Is.False);
        Assert.That(MetaServiceAuthority.MatchesInstallation(policy, Installation, Host, Principal, new('c', 64), 8, true), Is.False);
        Assert.That(MetaServiceAuthority.MatchesInstallation(policy, Installation, Host, Principal, new('a', 64), 9, true), Is.False);
        Assert.That(MetaServiceAuthority.MatchesInstallation(policy with { AllowUnattended = false }, Installation,
            Host, Principal, new('a', 64), 8, true), Is.False);
    }

    [Test]
    public void MatchingScopeAloneCannotAuthorizeAnotherMemberTargetContractOrRevision()
    {
        var policy = Policy();
        bool Allows(string member = "meta.doors.Door.setOpen", string target = Target, long revision = 3,
            string? contract = null, ulong authority = 12, Guid? grant = null, string slot = "door", string scope = "Door.Control") =>
            MetaServiceAuthority.Allows(policy, grant ?? Grant, revision, slot, target, contract ?? new('b', 64), member, scope, authority, 500);
        Assert.That(Allows(), Is.True);
        Assert.That(Allows(member: "meta.doors.Door.someOtherCommand"), Is.False);
        Assert.That(Allows(target: Host), Is.False);
        Assert.That(Allows(revision: 2), Is.False);
        Assert.That(Allows(contract: new('c', 64)), Is.False);
        Assert.That(Allows(authority: 13), Is.False);
        Assert.That(Allows(grant: Guid.NewGuid()), Is.False);
        Assert.That(Allows(slot: "another-door"), Is.False);
        Assert.That(Allows(scope: "Door.Read"), Is.False);
    }

    [TestCase(99, false)]
    [TestCase(100, true)]
    [TestCase(999, true)]
    [TestCase(1000, false)]
    public void ActiveWorldExpiryAndIssueBoundaryAreExact(long now, bool allowed) =>
        Assert.That(MetaServiceAuthority.Allows(Policy(), Grant, 3, "door", Target, new('b', 64),
            "meta.doors.Door.setOpen", "Door.Control", 12, now), Is.EqualTo(allowed));

    [Test]
    public void RevocationAndMalformedOrOversizedPolicyFailClosed()
    {
        var policy = Policy();
        Assert.That(MetaServiceAuthority.Allows(policy with { Revoked = true }, Grant, 3, "door", Target,
            new('b', 64), "meta.doors.Door.setOpen", "Door.Control", 12, 500), Is.False);
        Assert.That(MetaServiceAuthority.IsValid(policy with { Grants = [policy.Grants[0], policy.Grants[0]] }), Is.False);
        Assert.That(MetaServiceAuthority.IsValid(policy with { HostAuthorityEpoch = 1 }), Is.False);
        Assert.That(MetaServiceAuthority.IsValid(policy with { Grants = [policy.Grants[0] with { ExpiresAtTicks = 100 }] }), Is.False);
        Assert.That(MetaServiceAuthority.IsValid(policy with { Grants = [policy.Grants[0] with
            { Members = Enumerable.Range(0, 129).Select(index => "Member" + index).ToArray() }] }), Is.False);
    }
}
