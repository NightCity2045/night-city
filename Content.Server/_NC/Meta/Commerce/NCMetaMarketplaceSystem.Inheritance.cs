// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Commerce;
using Content.Server._NC.Meta.Persistence;

namespace Content.Server._NC.Meta.Commerce;

public sealed partial class NCMetaMarketplaceSystem
{
    private const string InheritanceType = "meta.software.inheritance";

    private async Task<NCMetaInheritedSoftwareGrant[]?> PrepareInheritedGrants(string deviceId, string oldOwner,
        ulong oldAuthority, long atTicks, Dictionary<string, Guid> retained, Dictionary<Guid, MetaSoftwareLicense> previous,
        Dictionary<Guid, MetaSoftwareLicense> updated, NCMetaSoftwareInheritance? oldProof)
    {
        var installed = await _persistence.ReadInstallationsAsync(deviceId, oldOwner);
        var dormant = await _persistence.ReadInheritedImagesAsync(deviceId, oldOwner);
        if (dormant == null) return null;
        var images = new List<NCMetaInheritedSoftwareGrant>();
        foreach (var installation in installed)
        {
            var release = installation.Active;
            if (release.CommercialOrigin is not { } origin || !retained.TryGetValue(Key(origin.ProductId, origin.Major), out var id) ||
                !previous.TryGetValue(id, out var license) || !updated.TryGetValue(id, out var transferred)) continue;
            var product = await ReadProductAsync(origin.ProductId, origin.Major);
            var descriptor = product?.Releases.SingleOrDefault(value => value.Id == origin.ReleaseId && value.ArtifactDigest == release.Digest);
            if (product == null || descriptor == null || product.ProjectId != origin.PublisherProjectId || release.Id != origin.ReleaseId ||
                !MetaSoftwarePolicy.Covers(license, product, descriptor, atTicks)) continue;
            var receipt = license.Activations.SingleOrDefault(value => value.DeviceId == deviceId && value.Owner == oldOwner &&
                value.DeviceAuthorityEpoch == oldAuthority && value.ReleaseDigest == release.Digest && value.LastOnlineTicks <= atTicks);
            images.Add(new(installation.Id, id, transferred.Seats.Single().AllocationId, product.Id, product.Major,
                release.Id, release.Digest, receipt?.ActivatedAtTicks, receipt?.LastOnlineTicks));
        }
        if (oldProof != null && oldProof.Owner == oldOwner && oldProof.AuthorityEpoch == oldAuthority)
        {
            foreach (var physical in dormant)
            {
                var evidence = oldProof.Images.SingleOrDefault(value => value.InstallationId == physical.InstallationId &&
                    value.ReleaseId == physical.ReleaseId && value.ArtifactDigest == physical.Digest);
                if (evidence == null || !retained.TryGetValue(Key(evidence.ProductId, evidence.Major), out var id) || id != evidence.LicenseId ||
                    !previous.TryGetValue(id, out var license) || !updated.TryGetValue(id, out var transferred)) continue;
                var product = await ReadProductAsync(evidence.ProductId, evidence.Major);
                var descriptor = product?.Releases.SingleOrDefault(value => value.Id == evidence.ReleaseId && value.ArtifactDigest == evidence.ArtifactDigest);
                if (product == null || descriptor == null || !MetaSoftwarePolicy.Covers(license, product, descriptor, atTicks)) continue;
                var preservesActivation = license.Seats.Any(value => value.DeviceId == deviceId && value.AllocationId == evidence.AllocationId);
                images.Add(evidence with { AllocationId = transferred.Seats.Single().AllocationId,
                    ActivatedAtTicks = preservesActivation ? evidence.ActivatedAtTicks : null,
                    LastOnlineTicks = preservesActivation ? evidence.LastOnlineTicks : null });
            }
        }
        return images.Count <= 64 && images.Select(value => value.InstallationId).Distinct().Count() == images.Count
            ? images.ToArray() : null;
    }

    private async Task<(MetaSoftwareActivation Receipt, Guid? InheritedInstallation)?> PrepareOfflineActivation(
        NCMetaLicensedRelease resolved, string owner, string deviceId, ulong authority, long now, bool consent)
    {
        var license = resolved.Entitlement;
        if (license == null || license.Terms.Offline == MetaOfflinePolicy.OnlineRequired) return null;
        var current = license.Activations.SingleOrDefault(value => value.DeviceId == deviceId && value.Owner == owner &&
            value.DeviceAuthorityEpoch == authority && value.ReleaseDigest == resolved.Release.Digest);
        if (current != null && MetaSoftwarePolicy.CanRunOffline(license, deviceId, owner, authority, resolved.Release.Digest, now))
        {
            var physical = await _persistence.ReadInstallationsAsync(deviceId, owner);
            if (physical.Any(value => value.Active.Id == resolved.Release.Id && value.Active.Digest == resolved.Release.Digest &&
                value.Active.CommercialOrigin?.ProductId == license.ProductId)) return (current, null);
        }
        if (!consent || license.Provenance != MetaLicenseProvenance.Transfer || !MetaSoftwarePolicy.CanTransferWithDevice(license, deviceId)) return null;
        var inherited = await Read<NCMetaSoftwareInheritance>(InheritanceType, deviceId);
        if (inherited == null || inherited.Owner != owner || inherited.AuthorityEpoch != authority) return null;
        var seat = license.Seats.SingleOrDefault(value => value.DeviceId == deviceId);
        if (seat == null) return null;
        foreach (var image in inherited.Images.Where(value => value.LicenseId == license.Id && value.AllocationId == seat.AllocationId &&
                     value.ProductId == license.ProductId && value.Major == license.Major && value.ReleaseId == resolved.Release.Id &&
                     value.ArtifactDigest == resolved.Release.Digest && value.ActivatedAtTicks != null && value.LastOnlineTicks != null))
        {
            var lastOnline = image.LastOnlineTicks!.Value;
            if (now < lastOnline || license.Terms.Offline == MetaOfflinePolicy.GracePeriod && now - lastOnline > license.Terms.OfflineGraceTicks ||
                !await _persistence.HasInheritedImageAsync(deviceId, owner, image.InstallationId, image.ReleaseId, image.ArtifactDigest)) continue;
            var receipt = new MetaSoftwareActivation(seat.AllocationId, deviceId, owner, authority, image.ArtifactDigest,
                image.ActivatedAtTicks!.Value, lastOnline);
            return (receipt, image.InstallationId);
        }
        return null;
    }
}
