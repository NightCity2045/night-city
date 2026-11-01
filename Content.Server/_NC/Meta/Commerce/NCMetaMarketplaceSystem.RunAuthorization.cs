// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Commerce;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Commerce;

/// <summary>Native admission result; null expiry means an authorized image without a time limit.</summary>
public sealed record NCMetaRunAuthorization(long? ExpiresAtTicks)
{
    public NCMetaRunContactRequirement? Contact { get; init; }
}

/// <summary>Server-created contact evidence. A source file or worker cannot supply a licensing bridge.</summary>
public sealed record NCMetaRunContactRequirement(string Owner, string RunDeviceId, ulong RunAuthorityEpoch,
    string AccessDeviceId, ulong AccessDeviceEpoch, ulong AccessAuthorityEpoch, NCSessionEpoch WorldEpoch,
    Guid LicenseId, Guid AllocationId, string ReleaseDigest, MetaOfflinePolicy Policy, long GraceTicks, long LastOnlineTicks);

public sealed partial class NCMetaMarketplaceSystem
{
    /// <summary>Resolves protected provenance and exact activation receipts, never a guest supplied expiry.</summary>
    public async Task<NCMetaRunAuthorization?> ResolveRunAuthorizationAsync(string owner, string deviceId,
        NCMetaRelease release, bool online = false, string? accessDeviceId = null)
    {
        if (!ValidParty(owner) || !MetaSoftwarePolicy.Device(deviceId) || release == null || !ValidPublication(release)) return null;
        var admission = await Main(() => _devices.TryResolve(deviceId, out var device) && device.Ready && device.Owner == owner &&
            _persistence.TryCaptureTime(out var epoch, out _) ? (device.DeviceEpoch, device.AuthorityEpoch, Epoch: epoch) : default);
        if (admission.AuthorityEpoch == 0) return null;
        var durable = await _persistence.ReadDeviceAsync(deviceId);
        if (durable == null || durable.Owner != owner || durable.AuthorityEpoch != admission.AuthorityEpoch) return null;

        long? deadline = null;
        NCMetaRunContactRequirement? contactProof = null;
        if (release.CommercialOrigin is { } origin)
        {
            var candidate = await ResolveRelease(owner, deviceId, origin.ProductId, origin.ReleaseId, forBuild: false);
            if (candidate == null) return null;
            // Online policies may contact CitiNet during cold boot too; no activation or scope widening occurs.
            var needsContact = candidate.Entitlement?.Terms.Offline is MetaOfflinePolicy.OnlineRequired or MetaOfflinePolicy.GracePeriod;
            var contact = (online || needsContact) && await Main(() => CanContactMarketplace(owner, deviceId, accessDeviceId));
            var licensed = await ResolveActivatedRelease(owner, deviceId, origin.ProductId, origin.ReleaseId, contact, accessDeviceId, candidate);
            if (licensed == null || licensed.ProjectId != origin.PublisherProjectId || licensed.Release.Id != release.Id ||
                licensed.Descriptor.Id != origin.ReleaseId || !MetaSoftwareVersion.TryParse(licensed.Descriptor.Version, out var version) ||
                version.Major != origin.Major || licensed.Release.Digest != release.Digest) return null;
            deadline = licensed.Entitlement?.IllegalExpiresAtTicks;
            if (needsContact && licensed.Entitlement is { } license)
            {
                var receipt = license.Activations.Single(value => value.DeviceId == deviceId && value.Owner == owner &&
                    value.DeviceAuthorityEpoch == admission.AuthorityEpoch && value.ReleaseDigest == release.Digest);
                contactProof = await Main(() => _devices.TryResolve(accessDeviceId ?? deviceId, out var access) && access.Ready &&
                    access.Owner == owner && _persistence.TryCaptureTime(out var epoch, out _) && epoch == admission.Epoch
                    ? new NCMetaRunContactRequirement(owner, deviceId, admission.AuthorityEpoch, access.Id, access.DeviceEpoch,
                        access.AuthorityEpoch, epoch, license.Id, receipt.AllocationId, release.Digest, license.Terms.Offline,
                        license.Terms.OfflineGraceTicks, receipt.LastOnlineTicks) : null);
                if (contactProof == null) return null;
            }
            // Publication attests lawful redistribution of linked libraries; consumers need the product entitlement.
            if (release.Dependencies is { Length: > 0 } && !licensed.DependenciesRedistributionApproved) return null;
        }
        else if (release.BuildAuthorization is { } build && build.Owner == owner && build.ArtifactDigest == release.Digest &&
            MetaSoftwarePolicy.Device(build.DeviceId) && build.DependencyFingerprint == NCMetaPersistenceSystem.Fingerprint(
                JsonSerializer.Serialize(release.Dependencies ?? [], Json)))
        {
            // The protected receipt covers this exact private compiled snapshot, even after its build seat is released.
        }
        else
        {
            foreach (var dependency in release.Dependencies ?? [])
            {
                if (!await Main(() => CanContactMarketplace(owner, deviceId, accessDeviceId))) return null;
                var resolved = await ResolveRelease(owner, deviceId, dependency.ProductId, dependency.ReleaseId, forBuild: true);
                if (resolved == null || resolved.Release.Digest != dependency.BlobDigest ||
                    resolved.Descriptor.ContainerDigest != dependency.ArtifactDigest || resolved.Descriptor.PackageIdentity != dependency.PackageIdentity ||
                    resolved.Descriptor.Version != dependency.Version) return null;
                deadline = Earlier(deadline, resolved.Entitlement?.IllegalExpiresAtTicks);
            }
        }
        return await Main(() => _devices.TryResolve(deviceId, out var current) && current.Ready && current.Owner == owner &&
            current.DeviceEpoch == admission.DeviceEpoch && current.AuthorityEpoch == admission.AuthorityEpoch &&
            _persistence.TryCaptureTime(out var epoch, out var time) && epoch == admission.Epoch &&
            (deadline == null || time.Ticks < deadline) && (contactProof == null || CanMaintainRunContact(contactProof) ||
                HasOfflineRunGrace(contactProof, contactProof.LastOnlineTicks, time.Ticks))) ? new(deadline) { Contact = contactProof } : null;
    }

    private static long? Earlier(long? left, long? right) => left == null ? right : right == null ? left : Math.Min(left.Value, right.Value);

}
