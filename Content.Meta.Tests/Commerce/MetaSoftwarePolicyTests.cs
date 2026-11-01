// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Commerce;
using NUnit.Framework;

namespace Content.Meta.Tests.Commerce;

[TestFixture]
public sealed class MetaSoftwarePolicyTests
{
    private static readonly Guid ProductId = Guid.Parse("c73af98b-0e1e-4428-a40e-f9a5a63d374a");
    private static readonly string DeviceId = "189c4ce2-108b-43ac-8984-30bf8ac27760";
    private static readonly Guid Allocation = Guid.Parse("b7a79ea7-e0fa-4b3a-a618-1d776cf8a4c2");
    private static MetaSoftwareRelease Release(string version = "1.0.0", long at = 10, string[]? scopes = null) =>
        new(Guid.NewGuid(), new string('a', 64), "test.product", version, "1.0", new string('b', 64), scopes ?? ["door.read"], ["meta.doors.Door"], 4096, at, "Release notes");
    private static MetaSoftwareProduct Product(MetaSoftwareTerms? terms = null, params MetaSoftwareRelease[] releases) =>
        new(ProductId, "seller", Guid.NewGuid(), "Product", "Description", 1, terms ?? new(), 100, releases, true, 1);
    private static MetaSoftwareLicense License(MetaSoftwareTerms? terms = null) => new(Guid.NewGuid(), ProductId, 1, "buyer",
        MetaLicenseScope.Device, 1, [new(Allocation, DeviceId)], [new(Allocation, DeviceId, "buyer", 5, new string('a', 64), 100, 100)], terms ?? new(), 100, 150, false,
        MetaLicenseProvenance.Purchase, Guid.NewGuid(), null, null, 1) { BoundDeviceId = DeviceId };

    [TestCase("1.0.0", true)] [TestCase("2.99.4", true)]
    [TestCase("0.1.0", false)] [TestCase("1.0", false)] [TestCase("01.0.0", false)]
    [TestCase("1.-1.0", false)] [TestCase("1.0.0-beta", false)] [TestCase("1.0.0 ", false)]
    public void CommercialVersionIsExactAndUnambiguous(string input, bool accepted)
    { Assert.That(MetaSoftwareVersion.TryParse(input, out _), Is.EqualTo(accepted)); }

    [Test]
    public void PerpetualMajorSurvivesSupportExpiryAndDoesNotCoverAnotherMajor()
    {
        var purchased = Release(); var minor = Release("1.1.0", 1000); var major = Release("2.0.0", 1000);
        var product = Product(null, purchased, minor, major); var license = License();
        Assert.That(MetaSoftwarePolicy.Covers(license, product, purchased, 10000), Is.True);
        Assert.That(MetaSoftwarePolicy.Covers(license, product, minor, 10000), Is.True);
        Assert.That(MetaSoftwarePolicy.Covers(license, product, major, 10000), Is.False);
        Assert.That(MetaSoftwarePolicy.CanRunOffline(license, DeviceId, "buyer", 5, new string('a', 64), 10000), Is.True);
    }

    [Test]
    public void SupportedReleasePolicyNeverDisablesAnAlreadyActivatedOfflineController()
    {
        var terms = new MetaSoftwareTerms { Updates = MetaUpdatePolicy.SupportedReleases };
        var installed = Release(); var afterSupport = Release("1.1.0", 200);
        var license = License(terms); var product = Product(terms, installed, afterSupport);
        Assert.That(MetaSoftwarePolicy.Covers(license, product, afterSupport, 10000), Is.False);
        Assert.That(MetaSoftwarePolicy.CanRunOffline(license, DeviceId, "buyer", 5, installed.ArtifactDigest, 10000), Is.True);
    }

    [Test]
    public void ActivationRequiresExactPhysicalAuthorityOwnerAllocationAndDigest()
    {
        var license = License();
        Assert.That(MetaSoftwarePolicy.CanRunOffline(license, DeviceId, "buyer", 6, new string('a', 64), 1000), Is.False);
        Assert.That(MetaSoftwarePolicy.CanRunOffline(license, DeviceId, "seller", 5, new string('a', 64), 1000), Is.False);
        Assert.That(MetaSoftwarePolicy.CanRunOffline(license, DeviceId, "buyer", 5, new string('c', 64), 1000), Is.False);
        Assert.That(MetaSoftwarePolicy.CanRunOffline(license with { Seats = [new(Guid.NewGuid(), DeviceId)] }, DeviceId, "buyer", 5, new string('a', 64), 1000), Is.False);
        Assert.That(MetaSoftwarePolicy.CanRunOffline(license with { Revoked = true }, DeviceId, "buyer", 5, new string('a', 64), 1000), Is.False);
    }

    [Test]
    public void DeclaredOfflineGraceUsesOnlyMonotonicActiveTime()
    {
        var terms = new MetaSoftwareTerms { Offline = MetaOfflinePolicy.GracePeriod, OfflineGraceTicks = 50 };
        var license = License(terms);
        Assert.That(MetaSoftwarePolicy.CanRunOffline(license, DeviceId, "buyer", 5, new string('a', 64), 150), Is.True);
        Assert.That(MetaSoftwarePolicy.CanRunOffline(license, DeviceId, "buyer", 5, new string('a', 64), 151), Is.False);
        Assert.That(MetaSoftwarePolicy.CanRunOffline(license, DeviceId, "buyer", 5, new string('a', 64), 99), Is.False);
        Assert.That(MetaSoftwarePolicy.ValidTerms(terms with { OfflineGraceTicks = 0 }), Is.False);
    }

    [Test]
    public void OnlineStartStillRequiresTheExactPreviouslyApprovedActivation()
    {
        var license = License(new() { Offline = MetaOfflinePolicy.OnlineRequired });
        var digest = new string('a', 64);
        Assert.That(MetaSoftwarePolicy.CanRunOffline(license, DeviceId, "buyer", 5, digest, 1000), Is.False);
        Assert.That(MetaSoftwarePolicy.CanRunOnline(license, DeviceId, "buyer", 5, digest, 1000), Is.True);
        Assert.That(MetaSoftwarePolicy.CanRunOnline(license with { Activations = [] }, DeviceId, "buyer", 5, digest, 1000), Is.False);
        Assert.That(MetaSoftwarePolicy.CanRunOnline(license, DeviceId, "buyer", 6, digest, 1000), Is.False);
        Assert.That(MetaSoftwarePolicy.CanRunOnline(license, DeviceId, "seller", 5, digest, 1000), Is.False);
        Assert.That(MetaSoftwarePolicy.CanRunOnline(license, DeviceId, "buyer", 5, new string('b', 64), 1000), Is.False);
        Assert.That(MetaSoftwarePolicy.CanRunOnline(license with { Seats = [new(Guid.NewGuid(), DeviceId)] }, DeviceId, "buyer", 5, digest, 1000), Is.False);
        Assert.That(MetaSoftwarePolicy.CanRunOnline(license with { Revoked = true }, DeviceId, "buyer", 5, digest, 1000), Is.False);
        Assert.That(MetaSoftwarePolicy.CanRunOnline(license, DeviceId, "buyer", 5, digest, 99), Is.False);
    }

    [Test]
    public void MoreScopesRequireFreshConsentWhileRemovedScopesDoNot()
    {
        var current = Release(scopes: ["door.read", "door.observe"]);
        var expanded = Release("1.1.0", scopes: ["door.control", "door.read"]);
        var reduced = Release("1.2.0", scopes: ["door.read"]);
        var product = Product(null, current, expanded, reduced); var license = License();
        var diff = MetaSoftwarePolicy.ScopeDiff(current.RequiredScopes, expanded.RequiredScopes);
        Assert.That(diff.Added, Is.EqualTo(new[] { "door.control" })); Assert.That(diff.Removed, Is.EqualTo(new[] { "door.observe" }));
        Assert.That(MetaSoftwarePolicy.CanUpdate(license, product, current, expanded, 1000, false), Is.False);
        Assert.That(MetaSoftwarePolicy.CanUpdate(license, product, current, expanded, 1000, true), Is.True);
        Assert.That(MetaSoftwarePolicy.CanUpdate(license, product, current, reduced, 1000, false), Is.True);
    }

    [Test]
    public void ClosedSourceAndLibraryBuildRightsAreIndependentOfOwningBytes()
    {
        var closed = new MetaSoftwareTerms { Source = MetaSourcePolicy.Closed, AllowLibraryBuild = true };
        var product = Product(closed, Release()); var license = License(closed);
        Assert.That(MetaSoftwarePolicy.CanReadSource(product, license, "buyer"), Is.False);
        Assert.That(MetaSoftwarePolicy.CanReadSource(product, null, "seller"), Is.True);
        Assert.That(MetaSoftwarePolicy.CanBuild(product, license, "buyer", 1000), Is.True);
        Assert.That(MetaSoftwarePolicy.CanBuild(product, null, "buyer", 1000), Is.False);
        var licensed = product with { Terms = closed with { Source = MetaSourcePolicy.Licensee } };
        Assert.That(MetaSoftwarePolicy.CanReadSource(licensed, license, "buyer"), Is.False, "the purchase terms remain authoritative");
        license = license with { Terms = licensed.Terms };
        Assert.That(MetaSoftwarePolicy.CanReadSource(licensed, license, "buyer"), Is.True);
        Assert.That(MetaSoftwarePolicy.CanReadSource(licensed, license with { Revoked = true }, "buyer"), Is.False);
    }

    [Test]
    public void FingerprintsDistinguishBoundariesAndChangedTerms()
    {
        Assert.That(MetaSoftwarePolicy.Fingerprint("ab", "c"), Is.Not.EqualTo(MetaSoftwarePolicy.Fingerprint("a", "bc")));
        var terms = new MetaSoftwareTerms();
        Assert.That(MetaSoftwarePolicy.TermsFingerprint(terms), Is.Not.EqualTo(MetaSoftwarePolicy.TermsFingerprint(terms with { AllowResale = false })));
    }

    [Test]
    public void OrganizationSeatsDoNotSharePhysicalActivationReceipts()
    {
        const string otherDevice = "792ca988-e49f-4857-b231-7b69fa5f868f";
        var otherAllocation = Guid.Parse("c0372296-d6fd-4f72-8684-2949ac9fd86c");
        var license = License() with { Scope = MetaLicenseScope.Organization, SeatLimit = 2,
            Seats = [new(Allocation, DeviceId), new(otherAllocation, otherDevice)] };
        Assert.That(MetaSoftwarePolicy.CanRunOffline(license, DeviceId, "buyer", 5, new string('a', 64), 1000), Is.True);
        Assert.That(MetaSoftwarePolicy.CanRunOffline(license, otherDevice, "buyer", 5, new string('a', 64), 1000), Is.False);
        var wrongAllocation = license with { Activations = [new(Allocation, otherDevice, "buyer", 5, new string('a', 64), 100, 100)] };
        Assert.That(MetaSoftwarePolicy.CanRunOffline(wrongAllocation, otherDevice, "buyer", 5, new string('a', 64), 1000), Is.False);
    }

    [Test]
    public void ExpiredIllegalEntitlementCannotBuildOrRunAtTheExpiryBoundary()
    {
        var release = Release(); var terms = new MetaSoftwareTerms { AllowLibraryBuild = true };
        var product = Product(terms, release);
        var license = License(terms) with { Provenance = MetaLicenseProvenance.NativePiracy,
            EvidenceId = Guid.NewGuid(), IllegalExpiresAtTicks = 200 };
        Assert.That(MetaSoftwarePolicy.Covers(license, product, release, 200), Is.False);
        Assert.That(MetaSoftwarePolicy.CanBuild(product, license, "buyer", 200), Is.False);
        Assert.That(MetaSoftwarePolicy.CanRunOffline(license, DeviceId, "buyer", 5, release.ArtifactDigest, 200), Is.False);
    }

    [Test]
    public void CanonicalPhysicalIdsAndTermsRejectAmbiguousInputs()
    {
        Assert.That(MetaSoftwarePolicy.Device(DeviceId), Is.True);
        Assert.That(MetaSoftwarePolicy.Device(DeviceId.ToUpperInvariant()), Is.False);
        Assert.That(MetaSoftwarePolicy.Device(Guid.Empty.ToString("D")), Is.False);
        Assert.That(MetaSoftwarePolicy.Digest(new string('a', 64)), Is.True);
        Assert.That(MetaSoftwarePolicy.Digest(new string('A', 64)), Is.False);
        Assert.That(MetaSoftwarePolicy.ValidTerms(new() { Transfer = (MetaLicenseTransferPolicy) 999 }), Is.False);
        Assert.That(MetaSoftwarePolicy.ValidTerms(new() { Offline = MetaOfflinePolicy.ActivatedPerpetual, OfflineGraceTicks = 1 }), Is.False);
        Assert.That(MetaSoftwarePolicy.ValidTerms(new() { SupportTicks = -1 }), Is.False);
    }

    [Test]
    public void DeviceBoundSeatCannotBeMovedThroughReleaseAndReassignment()
    {
        const string otherDevice = "792ca988-e49f-4857-b231-7b69fa5f868f";
        var original = License(); var released = original with { Seats = [], Activations = [] };
        Assert.That(MetaSoftwarePolicy.CanAssignSeat(released, DeviceId, 1000), Is.True);
        Assert.That(MetaSoftwarePolicy.CanAssignSeat(released, otherDevice, 1000), Is.False);
        Assert.That(MetaSoftwarePolicy.CanTransferWithDevice(original, DeviceId), Is.True);
        Assert.That(MetaSoftwarePolicy.CanTransferWithDevice(original, otherDevice), Is.False);
        Assert.That(MetaSoftwarePolicy.CanResell(original), Is.False);
        var portable = released with { Terms = released.Terms with { Transfer = MetaLicenseTransferPolicy.Portable } };
        Assert.That(MetaSoftwarePolicy.CanAssignSeat(portable, otherDevice, 1000), Is.True);
        Assert.That(MetaSoftwarePolicy.CanResell(portable), Is.True);
    }

    [Test]
    public void PiracyGrantIsEvidenceBoundToOneDigestAndCannotGainBuildOrResaleRights()
    {
        var terms = new MetaSoftwareTerms { Transfer = MetaLicenseTransferPolicy.None, AllowResale = false };
        var first = Release(); var different = Release("1.1.0") with { ArtifactDigest = new string('c', 64) };
        var product = Product(terms, first, different);
        var license = License(terms) with { Provenance = MetaLicenseProvenance.NativePiracy, EvidenceId = Guid.NewGuid(),
            IllegalExpiresAtTicks = 200, PinnedArtifactDigest = first.ArtifactDigest };
        Assert.That(MetaSoftwarePolicy.ValidLicense(license), Is.True);
        Assert.That(MetaSoftwarePolicy.Covers(license, product, first, 150), Is.True);
        Assert.That(MetaSoftwarePolicy.Covers(license, product, different, 150), Is.False);
        Assert.That(MetaSoftwarePolicy.Covers(license with { EvidenceId = null }, product, first, 150), Is.False);
        Assert.That(MetaSoftwarePolicy.CanBuild(product, license, "buyer", 150), Is.False);
        Assert.That(MetaSoftwarePolicy.CanResell(license), Is.False);
        Assert.That(MetaSoftwarePolicy.ValidLicense(license with { Terms = terms with { AllowRedistribution = true } }), Is.False);
    }

    [Test]
    public void DeviceTransferCreatesFreshAllocationAndDropsEveryFormerActivation()
    {
        var license = License(); var newAllocation = Guid.NewGuid();
        var transfer = MetaSoftwarePolicy.PrepareDeviceLicenseTransfer(license, DeviceId, "next-owner", newAllocation);
        Assert.That(transfer, Is.Not.Null);
        Assert.That(transfer!.OwnerChanged, Is.True);
        Assert.That(transfer.License.Owner, Is.EqualTo("next-owner"));
        Assert.That(transfer.License.Seats, Is.EqualTo(new[] { new MetaSoftwareSeat(newAllocation, DeviceId) }));
        Assert.That(transfer.License.Activations, Is.Empty);
        Assert.That(transfer.License.BoundDeviceId, Is.EqualTo(DeviceId));
        Assert.That(transfer.License.Revision, Is.EqualTo(license.Revision + 1));
        Assert.That(MetaSoftwarePolicy.ValidLicense(transfer.License), Is.True);
        Assert.That(MetaSoftwarePolicy.CanRunOnline(transfer.License, DeviceId, "next-owner", 6, new string('a', 64), 1000), Is.False);
    }

    [Test]
    public void OrganizationAndPortableEntitlementsDoNotTransferWithThePhysicalDevice()
    {
        const string otherDevice = "792ca988-e49f-4857-b231-7b69fa5f868f";
        var otherAllocation = Guid.NewGuid();
        var license = License() with { Scope = MetaLicenseScope.Organization, SeatLimit = 2,
            Seats = [new(Allocation, DeviceId), new(otherAllocation, otherDevice)],
            Activations = [.. License().Activations, new(otherAllocation, otherDevice, "buyer", 8, new string('a', 64), 100, 100)] };
        var transfer = MetaSoftwarePolicy.PrepareDeviceLicenseTransfer(license, DeviceId, "next-owner", Guid.NewGuid());
        Assert.That(transfer, Is.Not.Null);
        Assert.That(transfer!.OwnerChanged, Is.False);
        Assert.That(transfer.License.Owner, Is.EqualTo("buyer"));
        Assert.That(transfer.License.Seats.Select(value => value.DeviceId), Is.EqualTo(new[] { otherDevice }));
        Assert.That(transfer.License.Activations.Select(value => value.DeviceId), Is.EqualTo(new[] { otherDevice }));
        var portable = License(new() { Transfer = MetaLicenseTransferPolicy.Portable });
        var retained = MetaSoftwarePolicy.PrepareDeviceLicenseTransfer(portable, DeviceId, "next-owner", Guid.NewGuid());
        Assert.That(retained!.OwnerChanged, Is.False);
        Assert.That(retained.License.Seats, Is.Empty);
        Assert.That(retained.License.Owner, Is.EqualTo("buyer"));
    }

    [Test]
    public void NativePiracyFactoryPinsOneDeviceDigestAndBoundedActiveTimeWithoutCommercialRights()
    {
        var digest = new string('a', 64);
        var grant = MetaSoftwarePolicy.CreatePiratedLicense(Guid.NewGuid(), ProductId, 1, "buyer", DeviceId,
            Allocation, Guid.NewGuid(), digest, 100, 200);
        Assert.That(grant, Is.Not.Null);
        Assert.That(MetaSoftwarePolicy.ValidLicense(grant!), Is.True);
        Assert.That(grant!.Activations, Is.Empty);
        Assert.That(grant.Terms.Source, Is.EqualTo(MetaSourcePolicy.Closed));
        Assert.That(grant.Terms.AllowLibraryBuild || grant.Terms.AllowRedistribution || grant.Terms.AllowResale, Is.False);
        Assert.That(MetaSoftwarePolicy.CanTransferWithDevice(grant, DeviceId), Is.False);
        Assert.That(MetaSoftwarePolicy.CreatePiratedLicense(Guid.NewGuid(), ProductId, 1, "buyer", DeviceId,
            Allocation, Guid.NewGuid(), digest, 100, 101 + MetaSoftwarePolicy.MaximumPiracyDurationTicks), Is.Null);
        Assert.That(MetaSoftwarePolicy.CreatePiratedLicense(Guid.NewGuid(), ProductId, 1, "buyer", DeviceId,
            Allocation, Guid.Empty, digest, 100, 200), Is.Null);
    }

    [Test]
    public void MalformedSeatAndActivationSnapshotsFailClosed()
    {
        var license = License();
        Assert.That(MetaSoftwarePolicy.ValidLicense(license), Is.True);
        var duplicated = license with { Seats = [license.Seats[0], license.Seats[0]] };
        Assert.That(MetaSoftwarePolicy.ValidLicense(duplicated), Is.False);
        Assert.That(MetaSoftwarePolicy.CanRunOffline(duplicated, DeviceId, "buyer", 5, new string('a', 64), 1000), Is.False);
        Assert.That(MetaSoftwarePolicy.ValidLicense(license with { Activations = [license.Activations[0] with { Owner = "intruder" }] }), Is.False);
        Assert.That(MetaSoftwarePolicy.ValidLicense(license with { BoundDeviceId = null }), Is.False);
    }
}
