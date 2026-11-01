// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using System.Text.Json;
using Content.Meta.Hosting;
using Content.Server.Database;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.Meta.Persistence;

public sealed partial class NCMetaPersistenceSystem
{
    public const string ServiceGrantType = "meta.service-grants";

    public async Task<MetaServiceGrantPolicy?> ReadServiceGrantPolicyAsync(Guid installationId)
    {
        if (installationId == Guid.Empty) return null;
        var snapshot = await _snapshots.ReadCurrentAsync(ServiceGrantType, installationId.ToString("D"));
        if (snapshot == null || snapshot.FormatVersion != 1 || snapshot.Payload.Length > MetaServiceAuthority.MaximumPolicyBytes ||
            Encoding.UTF8.GetByteCount(snapshot.Payload) > MetaServiceAuthority.MaximumPolicyBytes) return null;
        try
        {
            var policy = JsonSerializer.Deserialize<MetaServiceGrantPolicy>(snapshot.Payload, Json);
            return MetaServiceAuthority.IsValid(policy) && policy!.InstallationId == installationId &&
                policy.Revision == snapshot.AggregateVersion && NCPartyRef.TryParse(policy.Principal, out var owner) &&
                owner.ToString() == policy.Principal && NCPartyRef.TryParse(policy.Issuer, out var issuer) &&
                issuer.ToString() == policy.Issuer ? policy : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task<NCPersistentAggregateWrite?> PrepareServiceGrantPolicyWriteAsync(MetaServiceGrantPolicy policy,
        long expectedRevision)
    {
        if (!MetaServiceAuthority.IsValid(policy) || expectedRevision < 0 || expectedRevision == long.MaxValue ||
            policy.Revision != expectedRevision + 1) return null;
        var previous = await ReadServiceGrantPolicyAsync(policy.InstallationId);
        // A corrupt existing record must not be re-created as revision zero.
        var raw = await _snapshots.ReadCurrentAsync(ServiceGrantType, policy.InstallationId.ToString("D"));
        if (raw != null && previous == null || (previous?.Revision ?? 0) != expectedRevision) return null;
        var payload = JsonSerializer.Serialize(policy, Json);
        if (Encoding.UTF8.GetByteCount(payload) > MetaServiceAuthority.MaximumPolicyBytes) return null;
        return new(Guid.NewGuid(), policy.Revoked ? "meta.grants.revoked" : "meta.grants.approved", ServiceGrantType,
            policy.InstallationId.ToString("D"), expectedRevision, 1, payload,
            JsonSerializer.Serialize(new { policy.InstallationId, policy.Revision, policy.Issuer, policy.Revoked }, Json));
    }

    public Task<NCMetaMutationOutcome?> ReadServiceGrantReplayAsync(Guid requestId, string owner, string fingerprint,
        bool revoke) => ReadReplayAsync(requestId, owner, fingerprint, revoke ? "meta.grants.revoke" : "meta.grants.issue");

    /// <summary>Attests the exact installed image while a standalone policy edit commits.</summary>
    public async Task<NCPersistentAggregateWrite?> PrepareServiceGrantInstallationAttestationAsync(NCMetaInstallation installation)
    {
        var current = await ReadInstallationAsync(installation.HostDeviceId, installation.Owner, installation.Id);
        var key = current == null ? null : await InstallationKeyAsync(current);
        if (key == null || current == null || current.Revision != installation.Revision ||
            current.Active.Digest != installation.Active.Digest || current.StateVolumeId != installation.StateVolumeId ||
            current.Revision == long.MaxValue) return null;
        return new(Guid.NewGuid(), "meta.installation.grants-attested", InstallationType, key, current.Revision, 1,
            JsonSerializer.Serialize(current with { Revision = current.Revision + 1 }, Json), "{}");
    }
}
