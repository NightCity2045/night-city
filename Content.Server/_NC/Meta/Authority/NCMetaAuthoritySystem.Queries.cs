// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Meta.Verification;
using Content.Server.Database;
using Content.Shared._NC.Meta.Authority;

namespace Content.Server._NC.Meta.Authority;

public sealed partial class NCMetaAuthoritySystem
{
    /// <summary>Capture only on the game thread; every resulting write still checks the DB proof inside its transaction.</summary>
    public NCMetaAuthorityConstraints? CaptureWriteConstraints(NCMetaManagementProof proof, params EntityUid[] devices)
    {
        if (!IsCurrent(proof) || devices.Length is < 1 or > 65) return null;
        var result = new Dictionary<string, NCMetaDeviceAuthorityProof>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            if (!_devices.TryGetEntry(device, out var entry) || !entry.Ready || entry.Owner != proof.Owner.ToString() ||
                entry.AuthorityEpoch < 2) return null;
            result[entry.Id] = new(entry.Id, entry.Owner, entry.AuthorityEpoch);
        }
        return new(proof.Employment, result.Values.OrderBy(value => value.DeviceId, StringComparer.Ordinal).ToArray());
    }

    public async Task<NCMetaGrantPolicySummary?> ReadGrantPolicyAsync(EntityUid terminal, EntityUid actor,
        string hostDeviceId, Guid installationId, VerifiedArtifact artifact)
    {
        if (!await _grantReads.WaitAsync(0)) return null;
        try
        {
            if (await CaptureWorkspaceManagementAsync(terminal, actor) == null) return null;
            var owner = await Main(() => CanUseTerminal(terminal, actor) && _devices.TryResolve(hostDeviceId, out var device) &&
                device.Ready && _network.CanRoute(terminal, device.Entity) ? device.Owner : null);
            if (owner == null) return null;
            var proof = await CaptureManagementAsync(actor, owner);
            if (proof == null) return null;
            var installation = await _persistence.ReadInstallationAsync(hostDeviceId, owner, installationId);
            var policy = await _persistence.ReadServiceGrantPolicyAsync(installationId);
            if (installation == null || installation.Active.Digest != artifact.ContentHash || policy != null &&
                (policy.Principal != owner || policy.HostDeviceId != hostDeviceId)) return null;
            return await Main(() =>
            {
                if (!IsCurrent(proof) || !CanUseTerminal(terminal, actor) || !_devices.TryResolve(hostDeviceId, out var host) ||
                    host.Owner != owner || !host.Ready || !_network.CanRoute(terminal, host.Entity) ||
                    !_persistence.TryCaptureTime(out _, out var now)) return null;
                var links = artifact.GetLinks().Select(link => new NCMetaGrantLink(link.SlotName,
                    installation.Bindings.SingleOrDefault(binding => binding.Slot == link.SlotName)?.DeviceId ?? "",
                    artifact.GetType(link.CapabilityTypeId).Name, link.ContractHash,
                    MetaApiCatalog.Default.Members.Where(member => member.Owner == artifact.GetType(link.CapabilityTypeId).Name &&
                        artifact.Manifest.RequiredScopes.Contains(member.Scope, StringComparer.Ordinal))
                        .Select(member => new NCMetaGrantMember(member.QualifiedName, member.Scope)).ToList())).ToList();
                var grants = policy?.Grants.Select(grant => new NCMetaGrantSummary(grant.Id, installationId, hostDeviceId,
                    owner, grant.Slot, grant.TargetDeviceId,
                    links.SingleOrDefault(link => link.Slot == grant.Slot)?.Capability ?? "",
                    grant.Members.ToList(), grant.Scopes.ToList(), policy.Revision, grant.ExpiresAtTicks,
                    policy.AllowUnattended, policy.Revoked)).ToList() ?? [];
                return links.Sum(link => link.Members.Count) > 512 ? null : new NCMetaGrantPolicySummary(installationId,
                    hostDeviceId, owner, artifact.ContentHash, policy?.Revision ?? 0, policy?.AllowUnattended ?? false,
                    policy?.Revoked ?? false, grants, links, now.Ticks, policy?.ExpiresAtTicks);
            });
        }
        finally { _grantReads.Release(); }
    }
}
