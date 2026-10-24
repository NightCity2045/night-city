// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Content.Meta.Hosting;
using Content.Meta.Verification;
using Content.Server.Database;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta.Authority;

namespace Content.Server._NC.Meta.Authority;

public sealed partial class NCMetaAuthoritySystem
{
    public async Task<NCMetaMutationOutcome> TryIssueInstallationGrantsAsync(EntityUid terminal, EntityUid actor,
        EntityUid host, Guid requestId, NCMetaInstallation installation, VerifiedArtifact artifact,
        NCMetaGrantApprovalRequest approval)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        if (mutation == null || requestId == Guid.Empty || approval == null || !await _writes.WaitAsync(0))
            return new(false, "nc-meta-error-busy");
        try
        {
            if (await CaptureWorkspaceManagementAsync(terminal, actor) == null)
                return new(false, "nc-meta-error-access");
            var proof = await CaptureManagementAsync(actor, installation.Owner);
            if (proof == null || !await Main(() => IsCurrent(proof) && CanUseTerminal(terminal, actor)))
                return new(false, "nc-meta-error-access");
            var fingerprint = Fingerprint(new { Operation = "issue", Actor = proof.Actor.ToString(), installation.Owner,
                installation.Id, installation.HostDeviceId, ArtifactDigest = artifact.ContentHash, Approval = approval });
            var replay = await _persistence.ReadServiceGrantReplayAsync(requestId, installation.Owner, fingerprint, false);
            if (replay != null) return replay;
            var prepared = await PrepareInstallationGrantsAsync(terminal, actor, host, requestId, installation, artifact, approval);
            var attestation = prepared == null ? null : await _persistence.PrepareServiceGrantInstallationAttestationAsync(installation);
            if (prepared == null || attestation == null) return new(false, "nc-meta-error-access");
            return await CommitGrantPolicyAsync(requestId, prepared, fingerprint, false, [attestation]);
        }
        finally { _writes.Release(); }
    }

    public async Task<NCMetaMutationOutcome> TryRevokeGrantAsync(EntityUid terminal, EntityUid actor, EntityUid host,
        Guid requestId, string hostDeviceId, Guid installationId, Guid grantId, long expectedRevision)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        if (mutation == null || requestId == Guid.Empty || installationId == Guid.Empty ||
            expectedRevision < 1 || expectedRevision == long.MaxValue || !await _writes.WaitAsync(0))
            return new(false, "nc-meta-error-busy");
        try
        {
            if (await CaptureWorkspaceManagementAsync(terminal, actor) == null)
                return new(false, "nc-meta-error-access");
            var owner = await Main(() => CanUseTerminal(terminal, actor) && _devices.TryGetEntry(host, out var device) &&
                device.Id == hostDeviceId && device.Ready && _network.CanRoute(terminal, host) ? device.Owner : null);
            if (owner == null) return new(false, "nc-meta-error-access");
            var proof = await CaptureManagementAsync(actor, owner);
            if (proof == null) return new(false, "nc-meta-error-access");
            var fingerprint = Fingerprint(new { Operation = "revoke", Actor = proof.Actor.ToString(), Owner = owner,
                Host = hostDeviceId, Installation = installationId, Grant = grantId, Revision = expectedRevision });
            var replay = await _persistence.ReadServiceGrantReplayAsync(requestId, owner, fingerprint, true);
            if (replay != null) return replay;
            var installation = await _persistence.ReadInstallationAsync(hostDeviceId, owner, installationId);
            var previous = await _persistence.ReadServiceGrantPolicyAsync(installationId);
            if (installation == null || previous == null || previous.Principal != owner || previous.HostDeviceId != hostDeviceId ||
                previous.Revision != expectedRevision || grantId != Guid.Empty && !previous.Grants.Any(grant => grant.Id == grantId))
                return new(false, "nc-meta-error-access");
            var prepared = await Main(() =>
            {
                if (!IsCurrent(proof) || !CanUseTerminal(terminal, actor) || !_devices.TryGetEntry(host, out var device) ||
                    device.Id != hostDeviceId || !device.Ready || device.Owner != owner ||
                    !_persistence.TryCaptureTime(out _, out _)) return null;
                var next = previous with { Grants = grantId == Guid.Empty ? previous.Grants : previous.Grants.Where(grant => grant.Id != grantId).ToArray(),
                    Revoked = previous.Revoked || grantId == Guid.Empty,
                    Revision = expectedRevision + 1, Issuer = proof.Actor.ToString() };
                // Existing shorter deadlines remain unchanged when any other grant is revoked.
                if (!MetaServiceAuthority.IsValid(next)) return null;
                return ((MetaServiceGrantPolicy Policy, NCMetaAuthorityConstraints Constraints)?)(next,
                    new NCMetaAuthorityConstraints(proof.Employment, [new(device.Id, device.Owner, device.AuthorityEpoch)]));
            });
            if (prepared == null) return new(false, "nc-meta-error-access");
            var write = await _persistence.PrepareServiceGrantPolicyWriteAsync(prepared.Value.Policy, expectedRevision);
            var attestation = await _persistence.PrepareServiceGrantInstallationAttestationAsync(installation);
            if (write == null || attestation == null) return new(false, "nc-meta-error-revision");
            return await CommitGrantPolicyAsync(requestId,
                new(prepared.Value.Policy, write, prepared.Value.Constraints, proof, terminal, host), fingerprint, true, [attestation]);
        }
        finally { _writes.Release(); }
    }

    private async Task<NCMetaMutationOutcome> CommitGrantPolicyAsync(Guid requestId, NCMetaGrantPreparation prepared,
        string fingerprint, bool revoke, NCPersistentAggregateWrite[] additionalWrites)
    {
        var captured = await Main(() =>
        {
            if (!_persistence.TryCaptureTime(out var epoch, out var now) ||
                !TryBeginPolicyCommit(prepared, out var lease, revoke))
                return (Valid: false, Epoch: default(Content.Shared._NC.Persistence.Time.NCSessionEpoch),
                    Time: default(Content.Shared._NC.Persistence.Time.NCActiveWorldTime), Lease: (IAsyncDisposable?)null);
            // In-flight native calls are denied before durable revoke/update; failure requires a fresh admission.
            return (Valid: true, Epoch: epoch, Time: now, Lease: lease);
        });
        if (!captured.Valid) return new(false, "nc-meta-error-access");
        try
        {
            var outcome = new NCMetaMutationOutcome(true, revoke ? "nc-meta-grant-revoked" : "nc-meta-grant-issued", prepared.Policy.Revision);
            var result = await _database.ApplyNCPersistentBatchMutationAsync(new(requestId, revoke ? "meta.grants.revoke" : "meta.grants.issue",
                "meta.owner", prepared.Policy.Principal, [prepared.Write, .. additionalWrites], JsonSerializer.Serialize(outcome),
                requestId, null, captured.Epoch, captured.Time, [], RequestFingerprint: fingerprint, Authority: prepared.Constraints));
            return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted
                ? outcome : new(false, "nc-meta-error-revision");
        }
        finally { if (captured.Lease != null) await captured.Lease.DisposeAsync(); }
    }

    public async Task<NCMetaGrantSummary[]> ReadGrantsAsync(EntityUid terminal, EntityUid actor, string hostDeviceId,
        Guid installationId, VerifiedArtifact artifact)
    {
        var policy = await ReadGrantPolicyAsync(terminal, actor, hostDeviceId, installationId, artifact);
        return policy?.Grants.ToArray() ?? [];
    }

    private static string Fingerprint<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
