// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Commerce;
using Content.Server.Database;
using Content.Server._NC.Meta.Commerce;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Persistence;

public sealed partial class NCMetaPersistenceSystem
{
    /// <summary>The customer owns the installation; the publisher retains its project and immutable image provenance.</summary>
    public async Task<NCMetaMutationOutcome> InstallLicensedAsync(Guid requestId, string hostId, string owner,
        Guid productId, NCMetaSoftwareActivationAdmission admission, NCMetaBinding[] bindings,
        NCSessionEpoch epoch, NCActiveWorldTime time, int storageBytes = 256 * 1024 * 1024,
        int maximumInstallations = MaximumInstallations, Func<Task<bool>>? activationGuard = null,
        string slotKind = "User", bool autoStart = false, long? memoryLimitBytes = null,
        Guid? expectedInstallationId = null, long? expectedInstallationRevision = null, string? consentFingerprint = null,
        NCMetaAuthorityConstraints? authority = null)
    {
        using var lease = TryBeginMutation();
        if (lease == null) return new(false, "nc-meta-error-busy");
        var licensed = admission.LicensedRelease;
        var release = licensed.Release;
        if (requestId == Guid.Empty || productId == Guid.Empty || !MetaSoftwarePolicy.Device(hostId) ||
            !MetaSoftwareVersion.TryParse(licensed.Descriptor.Version, out var version) ||
            release.Digest != licensed.Descriptor.ArtifactDigest || release.Id != licensed.Descriptor.Id ||
            !ValidRelease(release) || slotKind is not ("User" or "Firmware") || bindings.Length > 64 ||
            licensed.Entitlement is { } entitlement && (entitlement.ProductId != productId || entitlement.Owner != owner ||
                entitlement.Major != version.Major || admission.ActivationWrite == null ||
                admission.ActivationWrite.AggregateType != "meta.software.license" ||
                admission.ActivationWrite.AggregateId != entitlement.Id.ToString("D")))
            return new(false, "nc-meta-error-access");
        var fingerprint = LicensedInstallationFingerprint(hostId, owner, productId, release, bindings,
            slotKind, autoStart, memoryLimitBytes, expectedInstallationId, expectedInstallationRevision, consentFingerprint);
        var previous = await _database.GetNCPersistentCommandResultAsync(requestId);
        if (previous != null)
            return await CanReplayCommandAsync(previous, requestId, "meta.installation.licensed", owner, fingerprint)
                ? JsonSerializer.Deserialize<NCMetaMutationOutcome>(previous.ResultPayload!, Json) ?? new(false, "nc-meta-error-storage")
                : new(false, "nc-meta-error-request");
        var device = await ReadDeviceAsync(hostId);
        if (device == null || device.Owner != owner || device.AuthorityEpoch != admission.DeviceAuthorityEpoch)
            return new(false, "nc-meta-error-access");
        var image = release with { CommercialOrigin = new(productId, version.Major, licensed.ProjectId, release.Id) };
        PreparedInstallation? prepared;
        try { prepared = await PrepareInstallationAsync(requestId, hostId, owner, licensed.ProjectId, image,
            bindings, autoStart, storageBytes, maximumInstallations, slotKind, memoryLimitBytes,
            requiredInheritedInstallationId: admission.RequiredInheritedInstallationId,
            expectedInstallationId: expectedInstallationId, expectedInstallationRevision: expectedInstallationRevision); }
        catch (StateMigrationRequiredException) { return new(false, "nc-meta-error-state-migration"); }
        catch (InstallationRevisionException) { return new(false, "nc-meta-error-revision"); }
        if (prepared == null) return new(false, "nc-meta-error-quota");
        var outcome = new NCMetaMutationOutcome(true, "nc-meta-installed", prepared.Installation.Revision);
        var writes = prepared.Writes;
        writes.Add(new(Guid.NewGuid(), "meta.device.license-attested", "meta.device", hostId, device.Revision, 1,
            JsonSerializer.Serialize(device with { Revision = checked(device.Revision + 1) }, Json), "{}"));
        if (admission.ActivationWrite != null) writes.Add(admission.ActivationWrite);
        if (activationGuard != null && !await activationGuard()) return new(false, "nc-meta-error-access");
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new(requestId,
            "meta.installation.licensed", "meta.owner", owner, writes, JsonSerializer.Serialize(outcome, Json),
            requestId, null, epoch, time, [], RequestFingerprint: fingerprint, Authority: authority));
        return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted && result.ResultPayload != null
            ? JsonSerializer.Deserialize<NCMetaMutationOutcome>(result.ResultPayload, Json) ?? new(false, "nc-meta-error-storage")
            : new(false, result.Status == NCPersistentMutationStatus.VersionConflict ? "nc-meta-error-revision" : "nc-meta-error-storage");
    }
}
