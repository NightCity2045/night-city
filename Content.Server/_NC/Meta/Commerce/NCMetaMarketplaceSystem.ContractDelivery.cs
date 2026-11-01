// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Commerce;
using Content.Meta.Verification;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.Meta.Persistence;

namespace Content.Server._NC.Meta.Commerce;

public sealed record NCMetaDevelopmentEntitlements(NCAtomicLedgerParticipantWrite[] Writes, Guid[] LicenseIds,
    NCMetaRelease Release, Guid ProductId);

public sealed partial class NCMetaMarketplaceSystem
{
    /// <summary>Trusted native escrow preparation only. Every write must share the settlement ledger transaction.</summary>
    public async Task<NCMetaDevelopmentEntitlements?> PrepareDevelopmentEntitlementsAsync(MetaDevelopmentContract contract,
        NCMetaRelease release, VerifiedArtifact artifact, Guid transaction, long now)
    {
        if (contract.Delivery is not { } delivery || release.Id != delivery.ReleaseId || release.Digest != artifact.ContentHash ||
            artifact.Digest != delivery.ContainerDigest || delivery.ContentHash != release.Digest || transaction == Guid.Empty ||
            !MetaSoftwareVersion.TryParse(artifact.Manifest.PackageVersion, out var version) ||
            contract.Terms.Targets.Length is < 1 or > 3) return null;
        var productId = delivery.ProductId;
        if (await ReadProductAsync(productId, version.Major) != null) return null;
        foreach (var dependency in release.Dependencies ?? [])
        {
            var dependencyProduct = await ReadProductAsync(dependency.ProductId,
                MetaSoftwareVersion.TryParse(dependency.Version, out var parsed) ? parsed.Major : 0);
            if (dependencyProduct == null || dependencyProduct.Publisher != contract.Terms.Developer && !dependencyProduct.Terms.AllowRedistribution) return null;
            if (contract.Terms.Source == MetaDevelopmentSource.SourceCopyAndBuild)
                foreach (var target in contract.Terms.Targets)
                    if (await ResolveBuildDependencyAsync(contract.Terms.Customer, target.DeviceId, dependency.ProductId, dependency.ReleaseId) == null) return null;
        }
        var terms = new MetaSoftwareTerms { Source = contract.Terms.Source == MetaDevelopmentSource.SourceCopyAndBuild ? MetaSourcePolicy.Licensee : MetaSourcePolicy.Closed,
            AllowLibraryBuild = contract.Terms.Source == MetaDevelopmentSource.SourceCopyAndBuild, AllowResale = false,
            AllowRedistribution = false, SupportTicks = contract.Terms.SupportTicks, Offline = MetaOfflinePolicy.ActivatedPerpetual,
            Transfer = MetaLicenseTransferPolicy.WithDevice, Updates = MetaUpdatePolicy.SameMajor };
        var image = await _database.ReadNCMetaBlobAsync(release.Digest);
        if (image == null || !MetaSoftwarePolicy.ValidTerms(terms)) return null;
        var descriptor = new MetaSoftwareRelease(release.Id, release.Digest, artifact.Manifest.PackageName, artifact.Manifest.PackageVersion,
            artifact.Manifest.RuntimeAbi, artifact.Manifest.ApiCatalogHash, artifact.Manifest.RequiredScopes,
            artifact.GetLinks().Select(link => artifact.GetType(link.CapabilityTypeId).Name).Distinct(StringComparer.Ordinal).ToArray(),
            artifact.ResidentMemoryBytes, now, "")
        { ContainerDigest = artifact.Digest, MinimumProcessMemoryBytes = artifact.MinimumProcessMemoryBytes,
            MaximumConstantStringBytes = artifact.MaximumConstantStringBytes,
            PackageBytes = image.LongLength + (release.Resources ?? []).Sum(value => (long) value.Bytes) };
        var product = new MetaSoftwareProduct(productId, contract.Terms.Developer, delivery.DeveloperProjectId,
            contract.Terms.Subject, "", version.Major, terms, 0, [descriptor], false, 1);
        var key = Key(productId, version.Major);
        var writes = new List<NCAtomicLedgerParticipantWrite>
        {
            Write(ProductType, key, 0, product),
            Write("meta.software.identity", productId.ToString("D"), 0, new NCMetaSoftwareProductIdentity(productId, product.Publisher, product.ProjectId, 1)),
            Write(PublicationType, key + ":" + release.Id.ToString("D"), 0, new NCMetaSoftwarePublication(productId, version.Major, release, 1) { RedistributionApproved = true }),
            Write("meta.software.release-index", productId.ToString("D") + ":" + release.Id.ToString("D"), 0, new NCMetaSoftwareReleaseLocator(productId, release.Id, version.Major, 1)),
        };
        var owner = await Read<NCMetaSoftwareOwnerIndex>(OwnerType, contract.Terms.Customer) ?? new(contract.Terms.Customer, [], 0);
        if (owner.Licenses.Length + contract.Terms.Targets.Length > 128 || owner.ProductLicenses.ContainsKey(key)) return null;
        var licenses = new List<Guid>();
        foreach (var target in contract.Terms.Targets)
        {
            var compatible = await Compatibility(contract.Terms.Customer, target.DeviceId, descriptor, target.Slot);
            if (compatible is not { Compatible: true } || compatible.HardwareProfile != target.Profile ||
                !await Main(() => _devices.TryResolve(target.DeviceId, out var physical) && physical.Ready &&
                    physical.Owner == contract.Terms.Customer && physical.AuthorityEpoch == target.AuthorityEpoch)) return null;
            var authority = await ReadTargetAuthorityWrite(contract.Terms.Customer, target.DeviceId);
            if (authority == null) return null;
            var storage = await _persistence.PrepareSoftwareStorageAttestationAsync(target.DeviceId, contract.Terms.Customer, compatible.StorageRevision);
            if (storage == null) return null;
            var device = await Read<NCMetaSoftwareDeviceIndex>(DeviceType, target.DeviceId) ?? new(target.DeviceId, [], 0);
            if (device.Licenses.Count >= 64 || device.Licenses.ContainsKey(key)) return null;
            var licenseId = NCMetaPersistenceSystem.StableGuid("development-license:" + contract.Id + ":" + target.DeviceId);
            var license = new MetaSoftwareLicense(licenseId, productId, version.Major, contract.Terms.Customer, MetaLicenseScope.Device,
                1, [new(NCMetaPersistenceSystem.StableGuid("development-seat:" + licenseId), target.DeviceId)], [], terms,
                now, checked(now + terms.SupportTicks), false, MetaLicenseProvenance.Purchase, transaction, null, null, 1)
                { BoundDeviceId = target.DeviceId };
            if (!MetaSoftwarePolicy.ValidLicense(license)) return null;
            licenses.Add(licenseId);
            writes.Add(Write(LicenseType, licenseId.ToString("D"), 0, license));
            writes.Add(Write(DeviceType, target.DeviceId, device.Revision, device with
            { Licenses = new Dictionary<string, Guid>(device.Licenses, StringComparer.Ordinal) { [key] = licenseId }, Revision = device.Revision + 1 }));
            writes.Add(authority);
            writes.Add(new(storage.EventId, storage.EventType, storage.AggregateType, storage.AggregateId,
                storage.ExpectedAggregateVersion, storage.SnapshotFormatVersion, storage.SnapshotPayload, storage.EventPayload));
        }
        var buckets = new Dictionary<string, Guid[]>(owner.ProductLicenses, StringComparer.Ordinal) { [key] = licenses.ToArray() };
        writes.Add(Write(OwnerType, owner.Owner, owner.Revision, owner with
        { Licenses = owner.Licenses.Concat(licenses).ToArray(), ProductLicenses = buckets, Revision = owner.Revision + 1 }));
        return new(writes.ToArray(), licenses.ToArray(), release with
        { CommercialOrigin = new(productId, version.Major, delivery.DeveloperProjectId, release.Id) }, productId);
    }
}
