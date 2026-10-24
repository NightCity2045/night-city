// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using System.Text.Json;
using Content.Meta.Hosting;
using Content.Server.Database;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Authority;

public sealed partial class NCMetaAuthoritySystem
{
    /// <summary>Trusted native administration only. No META/owner UI accepts this proof callback.</summary>
    public async Task<NCMetaMutationOutcome> TryAdministrativeRevokeAsync(Guid requestId, Guid installationId,
        string administrator, string reason, Func<bool> stillAuthorized)
    {
        if (requestId == Guid.Empty || installationId == Guid.Empty || administrator.Length is < 1 or > 128 ||
            string.IsNullOrWhiteSpace(reason) || Encoding.UTF8.GetByteCount(reason) > 256 || !await _writes.WaitAsync(0))
            return new(false, "nc-meta-error-input");
        try
        {
            using var mutation = _persistence.TryBeginNativeMutation();
            if (mutation == null) return new(false, "nc-meta-error-busy");
            IAsyncDisposable? lease = null;
            NCSessionEpoch epoch = default;
            NCActiveWorldTime time = default;
            if (!await Main(() =>
                {
                    if (!stillAuthorized() || !_persistence.TryCaptureTime(out epoch, out time) ||
                        _changingPolicies.Count >= 64 || _changingPolicies.ContainsKey(installationId)) return false;
                    var token = Guid.NewGuid();
                    _changingPolicies.Add(installationId, token);
                    InvalidateServicePolicy(installationId);
                    lease = new PolicyCommitLease(this, installationId, token);
                    return true;
                })) return new(false, "nc-meta-error-access");
            await using var policyLease = lease!;
            var previous = await _persistence.ReadServiceGrantPolicyAsync(installationId);
            if (!MetaServiceAuthority.IsValid(previous) || previous!.Revision == long.MaxValue)
                return new(false, "nc-meta-error-access");
            var installation = await _persistence.ReadInstallationAsync(previous.HostDeviceId, previous.Principal, installationId);
            if (installation == null) return new(false, "nc-meta-error-access");
            var policy = await _persistence.PrepareServiceGrantPolicyWriteAsync(previous with
                { Revoked = true, Revision = previous.Revision + 1 }, previous.Revision);
            var attestation = await _persistence.PrepareServiceGrantInstallationAttestationAsync(installation);
            if (policy == null || attestation == null || !await Main(() => stillAuthorized() &&
                    _persistence.TryCaptureTime(out var current, out _) && current == epoch))
                return new(false, "nc-meta-error-access");
            policy = policy with { EventPayload = JsonSerializer.Serialize(new
                { Administrator = administrator, Reason = reason, RequestId = requestId, InstallationId = installationId }) };
            var outcome = new NCMetaMutationOutcome(true, "nc-meta-admin-revoked", previous.Revision + 1);
            // The original canonical issuer/owner remains unchanged. The administrative
            // action has its own actor, scope, reason, journal event and exact image CAS.
            var result = await _database.ApplyNCPersistentBatchMutationAsync(new(requestId, "meta.admin.revoke", "meta.admin",
                administrator, [policy, attestation], JsonSerializer.Serialize(outcome), requestId, null, epoch, time, [],
                RequestFingerprint: NCMetaJournalIntegrity.HashPayload(JsonSerializer.Serialize(new { administrator, installationId, reason }))));
            return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted
                ? JsonSerializer.Deserialize<NCMetaMutationOutcome>(result.ResultPayload ?? "null") ?? new(false, "nc-meta-error-storage")
                : new(false, "nc-meta-error-revision");
        }
        finally { _writes.Release(); }
    }
}
