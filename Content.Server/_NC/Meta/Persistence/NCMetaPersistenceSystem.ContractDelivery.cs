// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using System.Text.Json;
using Content.Meta.Commerce;
using Content.Server.Database;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Persistence;

public sealed record NCMetaDevelopmentClaim(Guid Id, Guid ContractId, string Owner, string Subject,
    string BlobDigest, string ContainerDigest, string TermsHash, bool SourceCopyAndBuild,
    NCMetaRelease Release, Guid[] LicenseIds, Guid SettlementTransactionId, long Revision);
public sealed record NCMetaDevelopmentInbox(string Owner, Guid[] Claims, long Revision);
public sealed record NCMetaPreparedContractDelivery(NCPersistentAggregateWrite[] Writes, Guid ClaimId);

public sealed partial class NCMetaPersistenceSystem
{
    /// <summary>Prepare only; these rights become visible in the SAME ledger settlement as payment.</summary>
    public async Task<NCMetaPreparedContractDelivery?> PrepareContractDeliveryWritesAsync(MetaDevelopmentContract contract,
        NCMetaRelease licensedRelease, Guid[] licenses, Guid transaction)
    {
        if (contract.Delivery is not { } delivery || licensedRelease.Id != delivery.ReleaseId ||
            licensedRelease.Digest != delivery.ContentHash || licensedRelease.CommercialOrigin?.ProductId != delivery.ProductId ||
            transaction == Guid.Empty || licenses.Length is < 1 or > 3 || licenses.Any(id => id == Guid.Empty)) return null;
        var references = (licensedRelease.Sources ?? []).Concat(licensedRelease.Resources ?? []).ToArray();
        if (references.Length is < 1 or > 64 || references.Select(value => value.Path).Distinct(StringComparer.Ordinal).Count() != references.Length ||
            references.Any(value => !Content.Meta.Hosting.MetaPaths.IsValid(value.Path) || value.Bytes is < 0 or > MaximumFileBytes) ||
            references.Sum(value => (long) value.Bytes) > MaximumProjectBytes) return null;
        foreach (var file in references)
        {
            var bytes = await _database.ReadNCMetaBlobAsync(file.Digest);
            if (bytes == null || bytes.Length != file.Bytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Digest) return null;
        }
        var inbox = await ReadDevelopmentInboxAsync(contract.Terms.Customer);
        if (inbox.Claims.Length >= 128) return null;
        var id = StableGuid("development-claim:" + contract.Id);
        if (inbox.Claims.Contains(id)) return null;
        var claim = new NCMetaDevelopmentClaim(id, contract.Id, contract.Terms.Customer, contract.Terms.Subject,
            licensedRelease.Digest, delivery.ContainerDigest, contract.TermsHash,
            contract.Terms.Source == MetaDevelopmentSource.SourceCopyAndBuild, licensedRelease, licenses.ToArray(), transaction, 1);
        return new([
            new(Guid.NewGuid(), "meta.development.delivery-transferred", "meta.development.delivery-claim", id.ToString("D"), 0, 1, JsonSerializer.Serialize(claim, Json), "{}"),
            new(Guid.NewGuid(), "meta.development.inbox-updated", "meta.development.inbox", inbox.Owner, inbox.Revision, 1,
                JsonSerializer.Serialize(inbox with { Claims = inbox.Claims.Append(id).ToArray(), Revision = inbox.Revision + 1 }, Json), "{}")], id);
    }
    public async Task<NCMetaDevelopmentInbox> ReadDevelopmentInboxAsync(string owner)
    {
        var row = await _snapshots.ReadCurrentAsync("meta.development.inbox", owner);
        if (row == null) return new(owner, [], 0);
        var value = JsonSerializer.Deserialize<NCMetaDevelopmentInbox>(row.Payload, Json);
        return value is { Claims.Length: <= 128 } && value.Owner == owner && value.Revision == row.AggregateVersion ? value
            : throw new System.IO.InvalidDataException("Invalid development inbox.");
    }
    public async Task<NCMetaDevelopmentClaim?> ReadDevelopmentClaimAsync(Guid id, string owner)
    {
        var row = await _snapshots.ReadCurrentAsync("meta.development.delivery-claim", id.ToString("D"));
        if (row == null) return null;
        var value = JsonSerializer.Deserialize<NCMetaDevelopmentClaim>(row.Payload, Json);
        return value != null && value.Id == id && value.Owner == owner && value.Revision == row.AggregateVersion &&
            value.BlobDigest == value.Release.Digest && value.SettlementTransactionId != Guid.Empty ? value : null;
    }
    /// <summary>Import the customer's already settled immutable source copy, preserving binary resources and provenance.</summary>
    public async Task<NCMetaMutationOutcome> ImportDevelopmentSourceAsync(Guid requestId, NCMetaDeviceRecord device,
        NCMetaDevelopmentClaim claim, NCSessionEpoch epoch, NCActiveWorldTime time, NCMetaAuthorityConstraints authority)
    {
        using var mutation = TryBeginMutation();
        if (mutation == null) return new(false, "nc-meta-error-busy");
        var fingerprint = Fingerprint(device.Owner, device.DeviceId, claim.Id.ToString("D"), claim.BlobDigest);
        var replay = await _database.GetNCPersistentCommandResultAsync(requestId);
        if (replay != null)
        {
            var verified = await CanReplayCommandAsync(replay, requestId, "meta.development.import", device.Owner, fingerprint);
            return new(verified, verified ? "nc-meta-development-imported" : "nc-meta-error-request");
        }
        if (claim.Owner != device.Owner || !claim.SourceCopyAndBuild || device.Projects.Length >= 64 || authority == null) return new(false, "nc-meta-error-access");
        var files = (claim.Release.Sources ?? []).Concat(claim.Release.Resources ?? []).OrderBy(value => value.Path, StringComparer.Ordinal).ToArray();
        if (files.Length is < 1 or > 64 || files.Sum(value => (long) value.Bytes) > MaximumProjectBytes || files.Any(value => value.Bytes is < 0 or > MaximumFileBytes)) return new(false, "nc-meta-error-quota");
        var id = StableGuid("development-import:" + claim.Owner + ":" + requestId);
        var project = new NCMetaProject(id, device.Owner, claim.Subject, 1,
            files.Select(file => new NCMetaProjectFile(StableGuid("development-file:" + id + ":" + file.Path), file.Path, 1, file.Digest, file.Bytes)).ToArray(), [claim.Release]);
        var next = device with { Projects = device.Projects.Append(id).ToArray(), ActiveProject = id, Revision = device.Revision + 1 };
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new(requestId, "meta.development.import", "meta.owner", device.Owner,
            [ProjectWrite(project, 0), new(Guid.NewGuid(), "meta.device.development-imported", "meta.device", device.DeviceId, device.Revision, 1, JsonSerializer.Serialize(next, Json), "{}")],
            "{}", requestId, null, epoch, time, [], RequestFingerprint: fingerprint, Authority: authority));
        return new(result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted,
            result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted ? "nc-meta-development-imported" : "nc-meta-error-revision");
    }
}
