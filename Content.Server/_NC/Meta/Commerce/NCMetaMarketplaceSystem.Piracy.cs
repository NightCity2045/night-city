// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Bytecode;
using Content.Meta.Commerce;
using Content.Meta.Language;
using Content.Meta.Verification;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.Meta.Persistence;

namespace Content.Server._NC.Meta.Commerce;

public sealed record NCMetaSoftwareCopyAdmission(Guid ActionId, Guid SessionId, string Owner,
    string DeckId, ulong DeckEpoch, ulong DeckAuthority, string SourceId, string SourceOwner,
    ulong SourceEpoch, ulong SourceAuthority, string Architecture, string Node, ulong NodeGeneration,
    Guid InstallationId);
public sealed record NCMetaSoftwareCopyEvidence(Guid Id, Guid ActionId, Guid SessionId, string Owner,
    string DeckId, ulong DeckAuthority, string SourceId, ulong SourceAuthority, Guid SourceInstallationId,
    string Architecture, string Node, ulong NodeGeneration, Guid ProductId, Guid ReleaseId, string ArtifactDigest,
    Guid LicenseId, string Fingerprint, long AtTicks, long ExpiresAtTicks, long Revision)
{
    public required NCMetaSoftwareCopyAdmission Admission { get; init; }
}
public sealed record NCMetaSoftwarePublisherImage(Guid ProjectId, Guid ReleaseId, Guid ProductId, int Major,
    string ArtifactDigest, long Revision);

public sealed partial class NCMetaMarketplaceSystem
{
    /// <summary>Native NET resolver only; this admission is never deserialized from a client request.</summary>
    internal async Task<NCMetaSoftwareOutcome> TryCopySoftwareAsync(NCMetaSoftwareCopyAdmission admission, Func<bool> physicalGuard,
        NCMetaAuthorityConstraints? authority = null)
    {
        if (authority == null && Content.Shared._NC.Persistence.Identity.NCPartyRef.TryParse(admission.Owner, out var principal) &&
            principal.Kind == Content.Shared._NC.Persistence.Identity.NCPartyKind.Organization) return Failure("access");
        using var mutation = _persistence.TryBeginNativeMutation();
        if (mutation == null || !await _publicationGate.WaitAsync(0)) return Failure("busy");
        try
        {
            var context = await Main(() =>
            {
                if (!physicalGuard() || !_devices.TryResolve(admission.DeckId, out var deck) || !deck.Ready || deck.Owner != admission.Owner ||
                    deck.DeviceEpoch != admission.DeckEpoch || deck.AuthorityEpoch != admission.DeckAuthority ||
                    !_devices.TryResolve(admission.SourceId, out var source) || source.Owner != admission.SourceOwner ||
                    source.DeviceEpoch != admission.SourceEpoch || source.AuthorityEpoch != admission.SourceAuthority ||
                    !_persistence.TryCaptureTime(out var epoch, out var time)) return null;
                return new NativeCopyContext(epoch, time);
            });
            if (context == null || admission.ActionId == Guid.Empty || admission.SessionId == Guid.Empty ||
                admission.NodeGeneration == 0 || admission.Owner == admission.SourceOwner || !ValidParty(admission.Owner) ||
                !ValidParty(admission.SourceOwner) || admission.Architecture is not { Length: > 0 and <= 128 } ||
                admission.Node is not { Length: > 0 and <= 128 }) return Failure("access");
            var sourceInstallations = await _persistence.ReadInstallationsAsync(admission.SourceId, admission.SourceOwner);
            var source = sourceInstallations.SingleOrDefault(value => value.Id == admission.InstallationId);
            if (source == null) return Failure("access");
            var origin = source.Active.CommercialOrigin;
            if (origin == null)
            {
                var original = await Read<NCMetaSoftwarePublisherImage>("meta.software.publisher-image", source.ProjectId.ToString("D") + ":" + source.Active.Id.ToString("D"));
                if (original == null || original.ArtifactDigest != source.Active.Digest) return Failure("access");
                origin = new(original.ProductId, original.Major, original.ProjectId, original.ReleaseId);
            }
            var product = await ReadProductAsync(origin.ProductId, origin.Major);
            var release = product?.Releases.SingleOrDefault(value => value.Id == origin.ReleaseId);
            var publication = product == null ? null : await Read<NCMetaSoftwarePublication>(PublicationType,
                Key(product.Id, product.Major) + ":" + origin.ReleaseId.ToString("D"));
            if (product == null || release == null || publication == null || release.PublishedAtTicks > context.Time.Ticks || origin.PublisherProjectId != product.ProjectId ||
                source.Active.Digest != release.ArtifactDigest || publication.Release.Digest != source.Active.Digest) return Failure("access");
            var bytes = await _database.ReadNCMetaBlobAsync(release.ArtifactDigest);
            if (bytes == null || bytes.Length > 1024 * 1024) return Failure("storage");
            var verified = await Task.Run(() => { var module = MetaBytecodeLoader.Load(bytes); return MetaVerifier.Verify(bytes, MetaApiCatalog.Default.CreateVerificationCatalog(module)); });
            if (verified.ContentHash != release.ArtifactDigest || verified.Digest != release.ContainerDigest) return Failure("invalid-artifact");
            var fingerprint = MetaSoftwarePolicy.Fingerprint(JsonSerializer.Serialize(admission, Json), release.ArtifactDigest);
            var evidenceId = NCMetaPersistenceSystem.StableGuid("software-copy-evidence:" + admission.ActionId);
            var replay = await Read<NCMetaSoftwareCopyEvidence>("meta.software.copy-evidence", evidenceId.ToString("D"));
            if (replay != null)
            {
                if (replay.Fingerprint != fingerprint)
                    return Failure("conflict");
                var granted = await Read<MetaSoftwareLicense>(LicenseType, replay.LicenseId.ToString("D"));
                return granted != null && granted.Owner == admission.Owner && !granted.Revoked && context.Time.Ticks < replay.ExpiresAtTicks
                    ? new(true, "nc-meta-software-copied", Product: product, License: granted) : Failure("conflict");
            }
            var index = await Read<NCMetaSoftwareOwnerIndex>(OwnerType, admission.Owner) ?? new(admission.Owner, [], 0);
            var deviceIndex = await Read<NCMetaSoftwareDeviceIndex>(DeviceType, admission.DeckId) ?? new(admission.DeckId, [], 0);
            if (index.Licenses.Length >= 128) return Failure("quota");
            var productKey = Key(product.Id, product.Major);
            if (!deviceIndex.Licenses.ContainsKey(productKey) && deviceIndex.Licenses.Count >= 64) return Failure("quota");
            if (deviceIndex.Licenses.TryGetValue(productKey, out var previousId))
            {
                var previous = await Read<MetaSoftwareLicense>(LicenseType, previousId.ToString("D"));
                if (previous is { Revoked: false } && previous.Owner == admission.Owner && previous.Seats.Any(value => value.DeviceId == admission.DeckId) &&
                    (previous.IllegalExpiresAtTicks == null || context.Time.Ticks < previous.IllegalExpiresAtTicks)) return Failure("already-owned");
            }
            var storage = await Main(() => _devices.TryResolve(admission.DeckId, out var deck) &&
                TryComp<Content.Shared._NC.Meta.NCMetaStorageComponent>(deck.Entity, out var volume) &&
                TryComp<Content.Shared._NC.Meta.NCMetaHostComponent>(deck.Entity, out var host) && _prototypes.TryIndex(host.Profile, out var profile)
                ? Math.Min(volume.CapacityBytes, profile.StorageBytes) : 0);
            var reserved = await _persistence.PrepareSoftwareCopyAsync(admission.DeckId, admission.Owner, release.PackageBytes, source, evidenceId, storage);
            if (reserved == null) return Failure("quota");
            var licenseId = NCMetaPersistenceSystem.StableGuid("software-copy-license:" + admission.ActionId);
            var expires = checked(context.Time.Ticks + MetaSoftwarePolicy.MaximumPiracyDurationTicks);
            var grant = MetaSoftwarePolicy.CreatePiratedLicense(licenseId, product.Id, product.Major, admission.Owner,
                admission.DeckId, NCMetaPersistenceSystem.StableGuid("software-copy-allocation:" + admission.ActionId), evidenceId,
                release.ArtifactDigest, context.Time.Ticks, expires);
            if (grant == null) return Failure("input");
            var deckAuthority = await ReadTargetAuthorityWrite(admission.Owner, admission.DeckId);
            var sourceAuthority = await ReadTargetAuthorityWrite(admission.SourceOwner, admission.SourceId);
            if (deckAuthority == null || sourceAuthority == null) return Failure("access");
            var evidence = new NCMetaSoftwareCopyEvidence(evidenceId, admission.ActionId, admission.SessionId, admission.Owner,
                admission.DeckId, admission.DeckAuthority, admission.SourceId, admission.SourceAuthority, source.Id,
                admission.Architecture, admission.Node, admission.NodeGeneration, product.Id, release.Id, release.ArtifactDigest,
                licenseId, fingerprint, context.Time.Ticks, expires, 1) { Admission = admission };
            var targets = new Dictionary<string, Guid>(deviceIndex.Licenses, StringComparer.Ordinal) { [productKey] = grant.Id };
            var writes = new NCAtomicLedgerParticipantWrite[]
            {
                Write("meta.software.copy-evidence", evidenceId.ToString("D"), 0, evidence),
                Write(LicenseType, grant.Id.ToString("D"), 0, grant),
                Write(OwnerType, admission.Owner, index.Revision, AddOwnerLicense(index, grant)),
                Write(DeviceType, admission.DeckId, deviceIndex.Revision, deviceIndex with { Licenses = targets, Revision = checked(deviceIndex.Revision + 1) }),
                deckAuthority, sourceAuthority
            };
            if (!await Main(() => physicalGuard() && _persistence.TryCaptureTime(out var epoch, out _) && epoch == context.Epoch)) return Failure("access");
            await BeforeAuthorityCommitAsync(admission.ActionId);
            var result = await _database.ApplyNCPersistentBatchMutationAsync(new(admission.ActionId, "meta.software.copy",
                "meta.owner", admission.Owner, writes.Select(value => new NCPersistentAggregateWrite(value.EventId, value.EventType,
                    value.AggregateType, value.AggregateId, value.ExpectedAggregateVersion, value.SnapshotFormatVersion,
                    value.SnapshotPayload, value.EventPayload)).Concat(reserved.Writes).ToArray(), JsonSerializer.Serialize(new { EvidenceId = evidenceId, LicenseId = grant.Id }, Json),
                admission.ActionId, null, context.Epoch, context.Time, [], RequestFingerprint: fingerprint, Authority: authority));
            return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted
                ? new(true, "nc-meta-software-copied", Product: product, License: grant) : Failure("conflict");
        }
        catch (MetaFormatException) { return Failure("invalid-artifact"); }
        finally { _publicationGate.Release(); }
    }
    private sealed record NativeCopyContext(Content.Shared._NC.Persistence.Time.NCSessionEpoch Epoch,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime Time);

    private async Task<bool> ValidatePiracyEvidence(MetaSoftwareLicense license, string deviceId, MetaSoftwareRelease release, long now)
    {
        if (license.EvidenceId is not { } id) return false;
        var evidence = await Read<NCMetaSoftwareCopyEvidence>("meta.software.copy-evidence", id.ToString("D"));
        if (evidence == null || evidence.LicenseId != license.Id || evidence.Owner != license.Owner || evidence.DeckId != deviceId ||
            evidence.ProductId != license.ProductId || evidence.ReleaseId != release.Id || evidence.ArtifactDigest != release.ArtifactDigest ||
            evidence.ExpiresAtTicks != license.IllegalExpiresAtTicks || evidence.AtTicks > now || now >= evidence.ExpiresAtTicks) return false;
        var receipt = await _database.GetNCPersistentCommandResultAsync(evidence.ActionId);
        if (receipt == null || receipt.Status != NCPersistentMutationStatus.DuplicateCompleted ||
            receipt.CommandType != "meta.software.copy" || receipt.ScopeType != "meta.owner" ||
            receipt.ScopeId != evidence.Owner || receipt.RequestFingerprint != evidence.Fingerprint || receipt.ResultPayload is not { Length: < 2048 }) return false;
        try
        {
            var proof = JsonSerializer.Deserialize<NativeCopyReceipt>(receipt.ResultPayload, Json);
            if (proof == null || proof.EvidenceId != evidence.Id || proof.LicenseId != license.Id) return false;
        }
        catch (JsonException) { return false; }
        if (!await _persistence.HasReservedSoftwareCopyAsync(deviceId, license.Owner, evidence.Id, release.PackageBytes)) return false;
        return await Main(() => _devices.TryResolve(deviceId, out var deck) && deck.Owner == license.Owner &&
            deck.AuthorityEpoch == evidence.DeckAuthority && TrackIllegalExpiry(license, deviceId, deck.AuthorityEpoch));
    }
    private sealed record NativeCopyReceipt(Guid EvidenceId, Guid LicenseId);

    public async Task<NCMetaSoftwareOutcome> TryDiscardSoftwareCopyAsync(EntityUid terminal, EntityUid user, Guid requestId, Guid licenseId)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        var context = await Main(() => Capture(terminal, user));
        if (mutation == null || context == null || requestId == Guid.Empty || licenseId == Guid.Empty) return Failure("access");
        var license = await Read<MetaSoftwareLicense>(LicenseType, licenseId.ToString("D"));
        if (license is not { Provenance: MetaLicenseProvenance.NativePiracy, EvidenceId: { } evidenceId }) return Failure("access");
        var evidence = await Read<NCMetaSoftwareCopyEvidence>("meta.software.copy-evidence", evidenceId.ToString("D"));
        if (evidence == null || evidence.Owner != license.Owner || evidence.LicenseId != license.Id) return Failure("access");
        var fingerprint = MetaSoftwarePolicy.Fingerprint(context.Actor, licenseId.ToString("D"), evidenceId.ToString("D"));
        var replay = await ReadCommand(context, requestId, "copy-discard", fingerprint, licenseId);
        if (replay.Conflict) return Failure("conflict");
        if (replay.Found) return new(true, "nc-meta-software-copy-discarded");
        if (!await CanActFor(context, license.Owner)) return Failure("access");
        var release = await _persistence.PrepareDiscardSoftwareCopyAsync(evidence.DeckId, license.Owner, evidenceId);
        var owner = await Read<NCMetaSoftwareOwnerIndex>(OwnerType, license.Owner);
        var device = await Read<NCMetaSoftwareDeviceIndex>(DeviceType, evidence.DeckId);
        if (release == null || owner == null || device == null || !owner.Licenses.Contains(licenseId)) return Failure("conflict");
        var entries = new Dictionary<string, Guid>(device.Licenses, StringComparer.Ordinal);
        if (entries.GetValueOrDefault(Key(license.ProductId, license.Major)) == licenseId) entries.Remove(Key(license.ProductId, license.Major));
        var writes = new[]
        {
            Write(LicenseType, licenseId.ToString("D"), license.Revision, license with { Revoked = true, Seats = [], Activations = [], Revision = checked(license.Revision + 1) }),
            Write(OwnerType, license.Owner, owner.Revision, RemoveOwnerLicense(owner, license)),
            Write(DeviceType, evidence.DeckId, device.Revision, device with { Licenses = entries, Revision = checked(device.Revision + 1) }),
            CommandWrite(context, requestId, "copy-discard", fingerprint, licenseId)
        }.Concat(release.Select(value => new Content.Server._NC.LivingCity.Economy.NCAtomicLedgerParticipantWrite(value.EventId,
            value.EventType, value.AggregateType, value.AggregateId, value.ExpectedAggregateVersion, value.SnapshotFormatVersion,
            value.SnapshotPayload, value.EventPayload))).ToArray();
        if (!await CanActFor(context, license.Owner) || !await Batch(context, requestId, "copy-discard", fingerprint, writes)) return Failure("conflict");
        await Main(() => { _illegalExpiry.Remove(license.Id); RevokeDevice(evidence.DeckId); EntitlementChanged?.Invoke(license.ProductId, license.Major, license.Owner); return true; });
        return new(true, "nc-meta-software-copy-discarded");
    }
}
