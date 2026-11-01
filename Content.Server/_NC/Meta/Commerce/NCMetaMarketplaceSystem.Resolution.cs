// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Bytecode;
using Content.Meta.Commerce;
using Content.Meta.Language;
using Content.Meta.Verification;

namespace Content.Server._NC.Meta.Commerce;

public sealed partial class NCMetaMarketplaceSystem
{
    /// <summary>Trusted compiler input only. No client endpoint accepts or reads an arbitrary blob digest.</summary>
    public async Task<MetaBinaryLibrary?> ResolveBinaryLibraryAsync(string owner, string deviceId, Guid productId, Guid releaseId)
        => (await ResolveBuildDependencyAsync(owner, deviceId, productId, releaseId))?.Library;
    public async Task<(MetaBinaryLibrary Library, Content.Server._NC.Meta.Persistence.NCMetaDependencyRef Reference)?> ResolveBuildDependencyAsync(string owner, string deviceId, Guid productId, Guid releaseId)
    {
        if (!await Main(() => CanContactMarketplace(owner, deviceId, null))) return null;
        var resolved = await ResolveRelease(owner, deviceId, productId, releaseId, forBuild: true);
        if (resolved?.Release.Exports == null || !await _publicationGate.WaitAsync(0)) return null;
        try
        {
            var bytes = await _database.ReadNCMetaBlobAsync(resolved.Release.Digest);
            if (bytes == null || bytes.Length > 1024 * 1024) return null;
            var artifact = await Task.Run(() => { var module = MetaBytecodeLoader.Load(bytes); return MetaVerifier.Verify(bytes, MetaApiCatalog.Default.CreateVerificationCatalog(module)); });
            if (artifact.ContentHash != resolved.Release.Digest || artifact.Digest != resolved.Descriptor.ContainerDigest || artifact.Manifest.PackageName != resolved.Descriptor.PackageIdentity || artifact.Manifest.PackageVersion != resolved.Descriptor.Version ||
                !await Main(() => _devices.TryResolve(deviceId, out var device) && device.Owner == owner && _persistence.TryCaptureTime(out _, out _))) return null;
            var library = new MetaBinaryLibrary(artifact.Manifest.PackageName, artifact.Manifest.PackageVersion, artifact.Digest, bytes, resolved.Release.Exports, Authorized: true);
            var reference = new Content.Server._NC.Meta.Persistence.NCMetaDependencyRef(productId, releaseId, artifact.ContentHash, artifact.Digest, artifact.Manifest.PackageName, artifact.Manifest.PackageVersion);
            return (library, reference);
        }
        catch (MetaFormatException) { return null; }
        finally { _publicationGate.Release(); }
    }
    public Task<NCMetaLicensedRelease?> ResolveLicensedReleaseAsync(string owner, string deviceId, Guid productId, Guid releaseId) =>
        ResolveRelease(owner, deviceId, productId, releaseId, forBuild: false);
    /// <summary>Cold boot admits an existing exact activation under its declared offline policy, without widening scopes.</summary>
    public async Task<NCMetaLicensedRelease?> ResolveInstalledReleaseAsync(string owner, string deviceId, Guid productId, Guid releaseId)
        => await ResolveActivatedRelease(owner, deviceId, productId, releaseId, online: false, accessDeviceId: null);
    public async Task<NCMetaLicensedRelease?> ResolveOnlineInstalledReleaseAsync(string owner, string deviceId, Guid productId, Guid releaseId, string? accessDeviceId = null)
        => await ResolveActivatedRelease(owner, deviceId, productId, releaseId, online: true, accessDeviceId);
    private async Task<NCMetaLicensedRelease?> ResolveActivatedRelease(string owner, string deviceId, Guid productId, Guid releaseId, bool online, string? accessDeviceId,
        NCMetaLicensedRelease? admitted = null)
    {
        if (online && !await Main(() => CanContactMarketplace(owner, deviceId, accessDeviceId))) return null;
        var resolved = admitted ?? await ResolveRelease(owner, deviceId, productId, releaseId, forBuild: false);
        if (resolved == null || resolved.Entitlement is not { } license) return resolved;
        var capture = await Main(() => _devices.TryResolve(deviceId, out var device) && device.Owner == owner && _persistence.TryCaptureTime(out _, out var time)
            ? (Authority: device.AuthorityEpoch, Ticks: time.Ticks) : (Authority: 0UL, Ticks: -1L));
        if (capture.Authority == 0 || !(online ? MetaSoftwarePolicy.CanRunOnline(license, deviceId, owner, capture.Authority, resolved.Release.Digest, capture.Ticks) :
            MetaSoftwarePolicy.CanRunOffline(license, deviceId, owner, capture.Authority, resolved.Release.Digest, capture.Ticks))) return null;
        var receipt = license.Activations.LastOrDefault(value => value.DeviceId == deviceId && value.ReleaseDigest == resolved.Release.Digest && value.Owner == owner && value.DeviceAuthorityEpoch == capture.Authority);
        if (receipt == null || MetaSoftwarePolicy.ScopeDiff(receipt.ApprovedScopes, resolved.Descriptor.RequiredScopes).RequiresConsent) return null;
        return resolved;
    }
    private async Task<NCMetaLicensedRelease?> ResolveRelease(string owner, string deviceId, Guid productId, Guid releaseId, bool forBuild)
    {
        if (!MetaSoftwarePolicy.Device(deviceId) || productId == Guid.Empty || releaseId == Guid.Empty || !await Main(() => _devices.TryResolve(deviceId, out var device) && device.Owner == owner && _persistence.TryCaptureTime(out _, out _))) return null;
        var locator = await Read<NCMetaSoftwareReleaseLocator>("meta.software.release-index", productId.ToString("D") + ":" + releaseId.ToString("D"));
        if (locator == null || locator.ProductId != productId || locator.ReleaseId != releaseId || locator.Major < 1) return null;
        var product = await ReadProductAsync(productId, locator.Major);
        var identity = await Read<NCMetaSoftwareProductIdentity>("meta.software.identity", productId.ToString("D"));
        if (product == null || identity == null || identity.Publisher != product.Publisher || identity.ProjectId != product.ProjectId) return null;
        var descriptor = product?.Releases.SingleOrDefault(release => release.Id == releaseId);
        var publication = await Read<NCMetaSoftwarePublication>(PublicationType, Key(productId, locator.Major) + ":" + releaseId.ToString("D"));
        if (product == null || descriptor == null || publication == null || publication.ProductId != productId || publication.Major != product.Major || publication.Release.Id != releaseId || publication.Release.Digest != descriptor.ArtifactDigest) return null;
        if (publication.Release.Dependencies is { Length: > 0 } && !publication.RedistributionApproved) return null;
        var deviceIndex = await Read<NCMetaSoftwareDeviceIndex>(DeviceType, deviceId);
        MetaSoftwareLicense? license = null;
        if (deviceIndex?.Licenses.TryGetValue(Key(productId, product.Major), out var licenseId) == true) license = await Read<MetaSoftwareLicense>(LicenseType, licenseId.ToString("D"));
        var time = await Main(() => _persistence.TryCaptureTime(out _, out var now) ? now.Ticks : -1);
        if (time < 0 || product.Publisher != owner && !(forBuild && product.Terms.Source == MetaSourcePolicy.Open && product.Terms.AllowLibraryBuild) &&
            (license == null || license.Owner != owner || !license.Seats.Any(seat => seat.DeviceId == deviceId) || !MetaSoftwarePolicy.Covers(license, product, descriptor, time) || forBuild && !MetaSoftwarePolicy.CanBuild(product, license, owner, time))) return null;
        if (license?.Provenance == MetaLicenseProvenance.NativePiracy && !await ValidatePiracyEvidence(license, deviceId, descriptor, time)) return null;
        if (!await Main(() => _devices.TryResolve(deviceId, out var device) && device.Owner == owner && _persistence.TryCaptureTime(out _, out _))) return null;
        return new(product.ProjectId, publication.Release, descriptor, license) { DependenciesRedistributionApproved = publication.RedistributionApproved };
    }
}
