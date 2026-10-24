// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Content.Meta.Commerce;

public enum MetaSourcePolicy { Closed, Licensee, Open }
public enum MetaLicenseScope { Device, Organization }
public enum MetaLicenseTransferPolicy { None, WithDevice, Portable }
public enum MetaOfflinePolicy { ActivatedPerpetual, GracePeriod, OnlineRequired }
public enum MetaUpdatePolicy { SameMajor, SupportedReleases }
public enum MetaLicenseProvenance { Purchase, Transfer, NativePiracy }
public readonly record struct MetaSoftwareVersion(int Major, int Minor, int Patch)
{
    public static bool TryParse(string? value, out MetaSoftwareVersion version)
    {
        version = default;
        var parts = value?.Split('.');
        if (parts is not { Length: 3 } || parts.Any(part => part.Length is < 1 or > 6 || part.Length > 1 && part[0] == '0') ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch) || major < 1) return false;
        version = new(major, minor, patch); return true;
    }
    public override string ToString() => FormattableString.Invariant($"{Major}.{Minor}.{Patch}");
}
public sealed record MetaSoftwareTerms
{
    public MetaSourcePolicy Source { get; init; }
    public MetaLicenseTransferPolicy Transfer { get; init; } = MetaLicenseTransferPolicy.WithDevice;
    public MetaOfflinePolicy Offline { get; init; } = MetaOfflinePolicy.ActivatedPerpetual;
    public MetaUpdatePolicy Updates { get; init; } = MetaUpdatePolicy.SameMajor;
    public long OfflineGraceTicks { get; init; }
    public long SupportTicks { get; init; }
    public bool AllowResale { get; init; } = true;
    public bool AllowLibraryBuild { get; init; }
    public bool AllowOrganizationSeats { get; init; } = true;
    public bool AllowRedistribution { get; init; }
}
public sealed record MetaSoftwareProductDraft
{
    public Guid ProductId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Changelog { get; init; } = string.Empty;
    public long Price { get; init; }
    public MetaSoftwareTerms Terms { get; init; } = new();
}
public sealed record MetaSoftwareRelease(Guid Id, string ArtifactDigest, string PackageIdentity, string Version,
    string RuntimeAbi, string ApiCatalogHash, string[] RequiredScopes, string[] RequiredInterfaces,
    long ResidentBytes, long PublishedAtTicks, string Changelog)
{
    public string ContainerDigest { get; init; } = string.Empty;
    public long MinimumProcessMemoryBytes { get; init; }
    public int MaximumConstantStringBytes { get; init; }
    public long PackageBytes { get; init; }
}
public sealed record MetaSoftwareCompatibility(string DeviceId, string HardwareProfile, bool Compatible,
    string[] MissingInterfaces, long RequiredMemoryBytes, long AvailableMemoryBytes,
    long RequiredStorageBytes, long AvailableStorageBytes, string MessageKey)
{
    public long StorageRevision { get; init; }
    public string SlotKind { get; init; } = "User";
}
public sealed record MetaSoftwareProduct(Guid Id, string Publisher, Guid ProjectId, string Name, string Description,
    int Major, MetaSoftwareTerms Terms, long Price, MetaSoftwareRelease[] Releases, bool Offered, long Revision);
public sealed record MetaSoftwareSeat(Guid AllocationId, string DeviceId);
public sealed record MetaSoftwareActivation(Guid AllocationId, string DeviceId, string Owner, ulong DeviceAuthorityEpoch,
    string ReleaseDigest, long ActivatedAtTicks, long LastOnlineTicks)
{
    public string[] ApprovedScopes { get; init; } = [];
}
public sealed record MetaSoftwareLicense(Guid Id, Guid ProductId, int Major, string Owner, MetaLicenseScope Scope,
    int SeatLimit, MetaSoftwareSeat[] Seats, MetaSoftwareActivation[] Activations, MetaSoftwareTerms Terms,
    long PurchasedAtTicks, long SupportUntilTicks, bool Revoked, MetaLicenseProvenance Provenance, Guid PaymentTransactionId,
    Guid? EvidenceId, long? IllegalExpiresAtTicks, long Revision)
{
    public string? BoundDeviceId { get; init; }
    public string? PinnedArtifactDigest { get; init; }
}
public sealed record MetaSoftwareScopeDiff(string[] Added, string[] Removed)
{
    public bool RequiresConsent => Added.Length > 0;
}
public sealed record MetaSoftwareDeviceLicenseTransfer(MetaSoftwareLicense License, bool OwnerChanged);

/// <summary>Commercial entitlements never grant native capabilities or bypass bytecode verification.</summary>
public static class MetaSoftwarePolicy
{
    public const long MaximumPiracyDurationTicks = 30 * TimeSpan.TicksPerMinute;
    public static MetaSoftwareLicense? CreatePiratedLicense(Guid id, Guid product, int major, string owner,
        string device, Guid allocation, Guid evidence, string digest, long now, long expires)
    {
        if (id == Guid.Empty || product == Guid.Empty || major < 1 || string.IsNullOrEmpty(owner) || !Device(device) ||
            allocation == Guid.Empty || evidence == Guid.Empty || !Digest(digest) || now < 0 || expires <= now || expires - now > MaximumPiracyDurationTicks) return null;
        return new(id, product, major, owner, MetaLicenseScope.Device, 1, [new(allocation, device)], [],
            new() { Source = MetaSourcePolicy.Closed, Transfer = MetaLicenseTransferPolicy.None, Offline = MetaOfflinePolicy.ActivatedPerpetual,
                Updates = MetaUpdatePolicy.SupportedReleases, AllowResale = false, AllowOrganizationSeats = false },
            now, now, false, MetaLicenseProvenance.NativePiracy, Guid.Empty, evidence, expires, 1)
            { BoundDeviceId = device, PinnedArtifactDigest = digest };
    }
    public static MetaSoftwareDeviceLicenseTransfer? PrepareDeviceLicenseTransfer(MetaSoftwareLicense license,
        string device, string newOwner, Guid newAllocation)
    {
        if (!ValidLicense(license) || !Device(device) || string.IsNullOrEmpty(newOwner) || license.Owner == newOwner ||
            newAllocation == Guid.Empty || license.Revision == long.MaxValue) return null;
        var transfer = CanTransferWithDevice(license, device);
        var updated = transfer ? license with { Owner = newOwner, Seats = [new(newAllocation, device)], Activations = [],
            Provenance = MetaLicenseProvenance.Transfer, Revision = license.Revision + 1 } : license with
            { Seats = license.Seats.Where(value => value.DeviceId != device).ToArray(),
                Activations = license.Activations.Where(value => value.DeviceId != device).ToArray(), Revision = license.Revision + 1 };
        return new(updated, transfer);
    }
    public static bool ValidTerms(MetaSoftwareTerms terms) => Enum.IsDefined(terms.Source) && Enum.IsDefined(terms.Transfer) && Enum.IsDefined(terms.Offline) && Enum.IsDefined(terms.Updates) &&
        terms.SupportTicks is >= 0 and <= 315360000000000 && terms.OfflineGraceTicks is >= 0 and <= 25920000000000 &&
        (terms.Offline == MetaOfflinePolicy.GracePeriod ? terms.OfflineGraceTicks > 0 : terms.OfflineGraceTicks == 0);
    public static bool Digest(string? text) => text is { Length: 64 } && text.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool Device(string? text) => Guid.TryParseExact(text, "D", out var value) && value != Guid.Empty && text == value.ToString("D");
    public static MetaSoftwareScopeDiff ScopeDiff(IEnumerable<string> current, IEnumerable<string> proposed)
    {
        var before = current.ToHashSet(StringComparer.Ordinal); var after = proposed.ToHashSet(StringComparer.Ordinal);
        return new(after.Except(before).Order(StringComparer.Ordinal).ToArray(), before.Except(after).Order(StringComparer.Ordinal).ToArray());
    }
    public static bool Covers(MetaSoftwareLicense license, MetaSoftwareProduct product, MetaSoftwareRelease release, long nowTicks) =>
        nowTicks >= 0 && !license.Revoked && license.ProductId == product.Id && license.Major == product.Major && MetaSoftwareVersion.TryParse(release.Version, out var version) && version.Major == license.Major &&
        product.Releases.Any(known => known.Id == release.Id && known.ArtifactDigest == release.ArtifactDigest) &&
        (license.Provenance != MetaLicenseProvenance.NativePiracy || license.EvidenceId is { } evidence && evidence != Guid.Empty && license.IllegalExpiresAtTicks != null && license.PinnedArtifactDigest == release.ArtifactDigest) &&
        (license.IllegalExpiresAtTicks == null || nowTicks < license.IllegalExpiresAtTicks) &&
        (license.Terms.Updates == MetaUpdatePolicy.SameMajor || release.PublishedAtTicks <= license.SupportUntilTicks);
    public static bool CanReadSource(MetaSoftwareProduct product, MetaSoftwareLicense? license, string principal, long nowTicks = -1) =>
        principal == product.Publisher || product.Terms.Source == MetaSourcePolicy.Open ||
        product.Terms.Source == MetaSourcePolicy.Licensee && license is { Revoked: false } && license.Terms.Source is MetaSourcePolicy.Licensee or MetaSourcePolicy.Open && license.Owner == principal && license.ProductId == product.Id && license.Major == product.Major &&
        (license.IllegalExpiresAtTicks == null || nowTicks >= 0 && nowTicks < license.IllegalExpiresAtTicks);
    public static bool CanBuild(MetaSoftwareProduct product, MetaSoftwareLicense? license, string principal, long nowTicks) =>
        principal == product.Publisher || product.Terms.Source == MetaSourcePolicy.Open && product.Terms.AllowLibraryBuild ||
        license is { Revoked: false, Provenance: not MetaLicenseProvenance.NativePiracy } && nowTicks >= 0 && license.Owner == principal && license.ProductId == product.Id && license.Major == product.Major && license.Terms.AllowLibraryBuild &&
        (license.IllegalExpiresAtTicks == null || nowTicks < license.IllegalExpiresAtTicks);
    public static bool CanRunOffline(MetaSoftwareLicense license, string deviceId, string owner, ulong authorityEpoch, string digest, long nowTicks)
    {
        var receipt = FindActivation(license, deviceId, owner, authorityEpoch, digest, nowTicks);
        return receipt != null && (license.Terms.Offline switch
        {
            MetaOfflinePolicy.ActivatedPerpetual => true,
            MetaOfflinePolicy.GracePeriod => nowTicks - receipt.LastOnlineTicks <= license.Terms.OfflineGraceTicks,
            _ => false
        });
    }
    public static bool CanRunOnline(MetaSoftwareLicense license, string deviceId, string owner, ulong authorityEpoch, string digest, long nowTicks) =>
        FindActivation(license, deviceId, owner, authorityEpoch, digest, nowTicks) != null;
    private static MetaSoftwareActivation? FindActivation(MetaSoftwareLicense license, string deviceId, string owner, ulong authorityEpoch, string digest, long nowTicks)
    {
        if (nowTicks < 0 || license.Revoked || license.Owner != owner || license.IllegalExpiresAtTicks is { } expires && nowTicks >= expires) return null;
        if (license.Provenance == MetaLicenseProvenance.NativePiracy && (license.EvidenceId is not { } evidence || evidence == Guid.Empty || license.IllegalExpiresAtTicks == null || license.PinnedArtifactDigest != digest)) return null;
        var seats = license.Seats.Where(seat => seat.DeviceId == deviceId).Take(2).ToArray();
        if (seats.Length != 1) return null;
        var seat = seats[0];
        return license.Activations.FirstOrDefault(receipt => receipt.AllocationId == seat.AllocationId && receipt.DeviceId == deviceId && receipt.Owner == owner && receipt.DeviceAuthorityEpoch == authorityEpoch && receipt.ReleaseDigest == digest && receipt.ActivatedAtTicks >= 0 && receipt.ActivatedAtTicks <= receipt.LastOnlineTicks && receipt.LastOnlineTicks <= nowTicks);
    }
    public static bool CanUpdate(MetaSoftwareLicense license, MetaSoftwareProduct product, MetaSoftwareRelease current,
        MetaSoftwareRelease proposed, long nowTicks, bool explicitlyGrantedNewScopes) => Covers(license, product, proposed, nowTicks) &&
        (!ScopeDiff(current.RequiredScopes, proposed.RequiredScopes).RequiresConsent || explicitlyGrantedNewScopes);
    public static string Fingerprint(params string[] parts) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
    public static string TermsFingerprint(MetaSoftwareTerms terms) => Fingerprint(JsonSerializer.Serialize(terms));
    public static bool CanAssignSeat(MetaSoftwareLicense license, string deviceId, long nowTicks) => Device(deviceId) && nowTicks >= 0 && !license.Revoked &&
        (license.IllegalExpiresAtTicks == null || nowTicks < license.IllegalExpiresAtTicks) && license.Seats.Length < license.SeatLimit && !license.Seats.Any(seat => seat.DeviceId == deviceId) &&
        (license.Scope != MetaLicenseScope.Device || license.Terms.Transfer == MetaLicenseTransferPolicy.Portable || license.BoundDeviceId == deviceId);
    public static bool CanResell(MetaSoftwareLicense license) => !license.Revoked && license.Terms.AllowResale && license.Terms.Transfer == MetaLicenseTransferPolicy.Portable && license.Provenance != MetaLicenseProvenance.NativePiracy;
    public static bool CanTransferWithDevice(MetaSoftwareLicense license, string deviceId) => !license.Revoked && license.Scope == MetaLicenseScope.Device &&
        license.Terms.Transfer == MetaLicenseTransferPolicy.WithDevice && license.BoundDeviceId == deviceId && license.Provenance != MetaLicenseProvenance.NativePiracy;
    public static bool ValidLicense(MetaSoftwareLicense license) => license.Id != Guid.Empty && license.ProductId != Guid.Empty && license.Major > 0 && license.Revision > 0 &&
        Enum.IsDefined(license.Scope) && Enum.IsDefined(license.Provenance) && license.Terms != null && ValidTerms(license.Terms) &&
        license.SeatLimit is >= 1 and <= 64 && (license.Scope != MetaLicenseScope.Device || license.SeatLimit == 1) &&
        license.Seats is { Length: <= 64 } && license.Seats.Length <= license.SeatLimit && license.Seats.All(seat => seat != null && seat.AllocationId != Guid.Empty && Device(seat.DeviceId)) &&
        license.Seats.Select(seat => seat.DeviceId).Distinct(StringComparer.Ordinal).Count() == license.Seats.Length && license.Seats.Select(seat => seat.AllocationId).Distinct().Count() == license.Seats.Length &&
        (license.Scope != MetaLicenseScope.Device || license.Terms.Transfer == MetaLicenseTransferPolicy.Portable || Device(license.BoundDeviceId) && license.Seats.All(seat => seat.DeviceId == license.BoundDeviceId)) &&
        license.PurchasedAtTicks >= 0 && license.SupportUntilTicks >= license.PurchasedAtTicks &&
        license.Activations is { Length: <= 128 } && license.Activations.All(receipt => receipt != null && receipt.Owner == license.Owner && receipt.DeviceAuthorityEpoch > 0 && Digest(receipt.ReleaseDigest) &&
            receipt.ActivatedAtTicks >= 0 && receipt.LastOnlineTicks >= receipt.ActivatedAtTicks && receipt.ApprovedScopes is { Length: <= 64 } && receipt.ApprovedScopes.All(scope => scope is { Length: > 0 and <= 128 }) &&
            receipt.ApprovedScopes.Distinct(StringComparer.Ordinal).Count() == receipt.ApprovedScopes.Length && license.Seats.Any(seat => seat.AllocationId == receipt.AllocationId && seat.DeviceId == receipt.DeviceId)) &&
        license.Activations.Select(receipt => (receipt.AllocationId, receipt.ReleaseDigest)).Distinct().Count() == license.Activations.Length &&
        (license.Provenance == MetaLicenseProvenance.NativePiracy ? license.EvidenceId is { } evidence && evidence != Guid.Empty && license.IllegalExpiresAtTicks > license.PurchasedAtTicks && Digest(license.PinnedArtifactDigest) &&
            !license.Terms.AllowLibraryBuild && !license.Terms.AllowRedistribution && !license.Terms.AllowResale && license.Terms.Transfer == MetaLicenseTransferPolicy.None :
            license.EvidenceId == null && license.IllegalExpiresAtTicks == null && license.PinnedArtifactDigest == null);
}
