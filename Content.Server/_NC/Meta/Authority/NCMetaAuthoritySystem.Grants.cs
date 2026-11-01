// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using System.Threading;
using Content.Meta.Hosting;
using Content.Meta.Language;
using Content.Meta.Verification;
using Content.Server.Database;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta.Authority;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Authority;

public sealed class NCMetaGrantPreparation
{
    internal NCMetaGrantPreparation(MetaServiceGrantPolicy policy, NCPersistentAggregateWrite write,
        NCMetaAuthorityConstraints constraints, NCMetaManagementProof proof, EntityUid terminal, EntityUid host)
    { Policy = policy; Write = write; Constraints = constraints; Proof = proof; Terminal = terminal; Host = host; }
    public MetaServiceGrantPolicy Policy { get; }
    public NCPersistentAggregateWrite Write { get; }
    public NCMetaAuthorityConstraints Constraints { get; }
    internal NCMetaManagementProof Proof { get; }
    internal EntityUid Terminal { get; }
    internal EntityUid Host { get; }
}

/// <summary>A fresh native admission; no runtime epochs or opaque proofs are restored from policy JSON.</summary>
public sealed class NCMetaServiceAdmission
{
    internal NCMetaServiceAdmission(MetaServiceGrantPolicy policy, long cacheVersion, NCSessionEpoch world,
        ulong hostEpoch, Dictionary<string, ulong> targets)
    { Policy = policy; CacheVersion = cacheVersion; World = world; HostEpoch = hostEpoch; Targets = targets;
        ExpiresAtTicks = MetaServiceAuthority.EarliestExpiry(policy); }
    internal MetaServiceGrantPolicy Policy { get; }
    internal long CacheVersion { get; }
    internal NCSessionEpoch World { get; }
    internal ulong HostEpoch { get; }
    internal Dictionary<string, ulong> Targets { get; }
    public Guid InstallationId => Policy.InstallationId;
    public string HostDeviceId => Policy.HostDeviceId;
    public string ArtifactDigest => Policy.ArtifactDigest;
    public string Principal => Policy.Principal;
    public long Revision => Policy.Revision;
    public bool AllowUnattended => Policy.AllowUnattended;
    public string[] GetGrantedScopes() => Policy.Grants.SelectMany(grant => grant.Scopes).Distinct(StringComparer.Ordinal).ToArray();
    public long? ExpiresAtTicks { get; }
    public (Guid GrantId, long Revision)? GetBindingReference(string slot) =>
        Policy.Grants.SingleOrDefault(grant => grant.Slot == slot) is { } grant ? (grant.Id, Policy.Revision) : null;
}

public sealed partial class NCMetaAuthoritySystem
{
    private sealed record CachedPolicy(MetaServiceGrantPolicy Policy, long Version, LinkedListNode<Guid> Node, long Bytes);
    private readonly Dictionary<Guid, CachedPolicy> _policies = [];
    private readonly Dictionary<string, HashSet<Guid>> _policyDevices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<Guid>> _policyPrincipals = new(StringComparer.Ordinal);
    private readonly LinkedList<Guid> _policyOrder = [];
    private readonly SortedSet<(long Tick, Guid Installation, long Revision)> _policyDeadlines = [];
    private readonly SemaphoreSlim _writes = new(8, 8);
    private readonly SemaphoreSlim _grantReads = new(8, 8);
    private readonly Dictionary<Guid, Guid> _changingPolicies = [];
    private long _policyVersion;
    private long _policyBytes;
    private NCSessionEpoch _policyWorld;

    public async Task<NCMetaGrantPreparation?> PrepareInstallationGrantsAsync(EntityUid terminal, EntityUid actor,
        EntityUid host, Guid requestId, NCMetaInstallation installation, VerifiedArtifact artifact,
        NCMetaGrantApprovalRequest approval)
    {
        if (requestId == Guid.Empty || approval == null || !approval.ScopeConsent || approval.ExpectedRevision < 0 ||
            approval.ExpectedRevision == long.MaxValue || approval.Bindings is not { Count: <= 64 } ||
            installation.Id == Guid.Empty || installation.Active.Digest != artifact.ContentHash ||
            installation.Bindings.Length != artifact.GetLinks().Length) return null;
        // Copy client collections before any subsequent asynchronous work.
        if (approval.Bindings.Any(value => value == null || value.Members is not { Count: <= 128 } ||
                value.Scopes is not { Count: <= 64 } || value.Members.Any(member => member == null) ||
                value.Scopes.Any(scope => scope == null))) return null;
        var consents = approval.Bindings.Select(value => new NCMetaGrantConsent(value.Slot, value.TargetDeviceId,
            value.Members.ToList(), value.Scopes.ToList())).ToArray();
        if (await CaptureWorkspaceManagementAsync(terminal, actor) == null) return null;
        var proof = await CaptureManagementAsync(actor, installation.Owner);
        if (proof == null) return null;
        var prepared = await Main(() =>
        {
            if (!CanUseTerminal(terminal, actor) || !IsCurrent(proof) || !_devices.TryGetEntry(host, out var hardware) ||
                !hardware.Ready || hardware.Owner != installation.Owner || hardware.Id != installation.HostDeviceId ||
                !_devices.IsPowered(host) || !_network.CanRoute(terminal, host) ||
                !_persistence.TryCaptureTime(out _, out var now) ||
                approval.ExpiresAtTicks is { } expires && expires <= now.Ticks ||
                consents.Length != installation.Bindings.Length ||
                consents.Select(value => value.Slot).Distinct(StringComparer.Ordinal).Count() != consents.Length) return null;
            var rules = new List<MetaServiceGrant>(consents.Length);
            var devices = new Dictionary<string, NCMetaDeviceAuthorityProof>(StringComparer.Ordinal)
                { [hardware.Id] = new(hardware.Id, hardware.Owner, hardware.AuthorityEpoch) };
            foreach (var link in artifact.GetLinks())
            {
                var binding = installation.Bindings.SingleOrDefault(value => value.Slot == link.SlotName);
                var consent = consents.SingleOrDefault(value => value.Slot == link.SlotName);
                var capability = artifact.GetType(link.CapabilityTypeId).Name;
                if (binding == null || consent == null || consent.TargetDeviceId != binding.DeviceId ||
                    binding.ContractHash != link.ContractHash || !_devices.TryResolve(binding.DeviceId, out var target) ||
                    !target.Ready || target.Owner != installation.Owner || !_devices.IsPowered(target.Entity) ||
                    !_network.CanRoute(host, target.Entity)) return null;
                var members = MetaApiCatalog.Default.Members.Where(member => member.Owner == capability &&
                    artifact.Manifest.RequiredScopes.Contains(member.Scope, StringComparer.Ordinal)).ToArray();
                if (consent.Members.Any(name => !members.Any(member => member.QualifiedName == name &&
                        consent.Scopes.Contains(member.Scope, StringComparer.Ordinal))) ||
                    consent.Scopes.Any(scope => !binding.Scopes.Contains(scope, StringComparer.Ordinal) ||
                        !members.Any(member => member.Scope == scope && consent.Members.Contains(member.QualifiedName, StringComparer.Ordinal)))) return null;
                rules.Add(new(NCMetaPersistenceSystem.StableGuid("meta-service-grant:" + requestId + ":" + installation.Id + ":" + link.SlotName),
                    link.SlotName, target.Id, link.ContractHash, consent.Members.Order(StringComparer.Ordinal).ToArray(),
                    consent.Scopes.Order(StringComparer.Ordinal).ToArray(), target.AuthorityEpoch, approval.ExpiresAtTicks));
                devices[target.Id] = new(target.Id, target.Owner, target.AuthorityEpoch);
            }
            var policy = new MetaServiceGrantPolicy(installation.Id, hardware.Id, hardware.Owner, artifact.ContentHash,
                hardware.AuthorityEpoch, approval.AllowUnattended, rules.ToArray(), proof.Actor.ToString(), now.Ticks,
                checked(approval.ExpectedRevision + 1), ExpiresAtTicks: approval.ExpiresAtTicks);
            return MetaServiceAuthority.IsValid(policy) ? (Policy: policy,
                Constraints: new NCMetaAuthorityConstraints(proof.Employment, devices.Values.OrderBy(value => value.DeviceId, StringComparer.Ordinal).ToArray())) :
                ((MetaServiceGrantPolicy Policy, NCMetaAuthorityConstraints Constraints)?)null;
        });
        if (prepared == null) return null;
        var write = await _persistence.PrepareServiceGrantPolicyWriteAsync(prepared.Value.Policy, approval.ExpectedRevision);
        if (write == null) return null;
        var result = new NCMetaGrantPreparation(prepared.Value.Policy, write, prepared.Value.Constraints, proof, terminal, host);
        return await Main(() => CanCommit(result) ? result : null);
    }

    /// <summary>Main-thread final guard; the returned DB constraints must accompany the enclosing atomic batch.</summary>
    public bool CanCommit(NCMetaGrantPreparation preparation, bool revocation = false) => IsCurrent(preparation.Proof) &&
        CanUseTerminal(preparation.Terminal, preparation.Proof.ActorEntity) &&
        _network.CanRoute(preparation.Terminal, preparation.Host) && preparation.Constraints.Devices.All(proof =>
            _devices.TryResolve(proof.DeviceId, out var device) && device.Ready && device.Owner == proof.Owner &&
            device.AuthorityEpoch == proof.AuthorityEpoch && _devices.IsPowered(device.Entity) &&
            _network.CanRoute(preparation.Host, device.Entity)) &&
        _persistence.TryCaptureTime(out _, out var now) && (revocation ||
            MetaServiceAuthority.EarliestExpiry(preparation.Policy) is not { } expiry || now.Ticks < expiry);

    private sealed class PolicyCommitLease(NCMetaAuthoritySystem system, Guid installation, Guid token) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            return new ValueTask(system.Main(() => { system.EndPolicyCommit(installation, token); return true; }));
        }
    }

    /// <summary>Main thread. Hold through the outer atomic installation/policy transaction; release before new admission.</summary>
    public bool TryBeginPolicyCommit(NCMetaGrantPreparation preparation, out IAsyncDisposable? lease, bool revocation = false)
    {
        lease = null;
        if (_changingPolicies.Count >= 64 || _changingPolicies.ContainsKey(preparation.Policy.InstallationId) ||
            !CanCommit(preparation, revocation)) return false;
        var token = Guid.NewGuid();
        _changingPolicies.Add(preparation.Policy.InstallationId, token);
        InvalidateServicePolicy(preparation.Policy.InstallationId);
        lease = new PolicyCommitLease(this, preparation.Policy.InstallationId, token);
        return true;
    }

    private void EndPolicyCommit(Guid installation, Guid token)
    {
        if (_changingPolicies.TryGetValue(installation, out var current) && current == token)
        { _changingPolicies.Remove(installation); InvalidateServicePolicy(installation); }
    }

    public async Task<NCMetaServiceAdmission?> ResolveServiceAdmissionAsync(NCMetaInstallation installation,
        VerifiedArtifact artifact, bool unattended)
    {
        if (!await _reads.WaitAsync(0)) return null;
        try
        {
            var version = await Main(() => _policyVersion);
            var policy = await _persistence.ReadServiceGrantPolicyAsync(installation.Id);
            if (!MetaServiceAuthority.IsValid(policy)) return null;
            if (!(await _partyResolver.ResolveAsync(NCPartyRef.TryParse(installation.Owner, out var party) ? party : default,
                    requireActive: true)).IsResolved) return null;
            return await Main(() =>
            {
                if (version != _policyVersion || _changingPolicies.ContainsKey(installation.Id) ||
                    !_devices.TryResolve(installation.HostDeviceId, out var host) || !host.Ready ||
                    !MetaServiceAuthority.MatchesInstallation(policy!, installation.Id, host.Id, host.Owner,
                        artifact.ContentHash, host.AuthorityEpoch, unattended) || host.Owner != installation.Owner ||
                    !_persistence.TryCaptureTime(out var world, out var now) || installation.Active.Digest != artifact.ContentHash ||
                    policy!.ExpiresAtTicks is { } policyExpiry && now.Ticks >= policyExpiry ||
                    policy!.Grants.Length != installation.Bindings.Length) return null;
                var targets = new Dictionary<string, ulong>(StringComparer.Ordinal);
                foreach (var link in artifact.GetLinks())
                {
                    var rule = policy.Grants.SingleOrDefault(value => value.Slot == link.SlotName);
                    var binding = installation.Bindings.SingleOrDefault(value => value.Slot == link.SlotName);
                    var capability = artifact.GetType(link.CapabilityTypeId).Name;
                    if (rule == null || binding == null || rule.TargetDeviceId != binding.DeviceId || rule.ContractHash != link.ContractHash ||
                        rule.Scopes.Any(scope => !binding.Scopes.Contains(scope, StringComparer.Ordinal) ||
                            !artifact.Manifest.RequiredScopes.Contains(scope, StringComparer.Ordinal) ||
                            !MetaApiCatalog.Default.Members.Any(member => member.Owner == capability && member.Scope == scope &&
                                rule.Members.Contains(member.QualifiedName, StringComparer.Ordinal))) ||
                        rule.Members.Any(name => !MetaApiCatalog.Default.Members.Any(member => member.QualifiedName == name &&
                            member.Owner == capability && rule.Scopes.Contains(member.Scope, StringComparer.Ordinal))) ||
                        rule.ExpiresAtTicks is { } expiry && now.Ticks >= expiry ||
                        !_devices.TryResolve(rule.TargetDeviceId, out var target) || !target.Ready ||
                        target.Owner != policy.Principal || target.AuthorityEpoch != rule.TargetAuthorityEpoch) return null;
                    targets[target.Id] = target.DeviceEpoch;
                }
                var entry = CachePolicy(policy, world);
                return entry == null ? null : new NCMetaServiceAdmission(policy, entry.Version, world, host.DeviceEpoch, targets);
            });
        }
        finally { _reads.Release(); }
    }

    public bool CanContinue(NCMetaServiceAdmission admission) => _policies.TryGetValue(admission.InstallationId, out var cached) &&
        cached.Version == admission.CacheVersion && !cached.Policy.Revoked &&
        _persistence.TryCaptureTime(out var world, out var now) && world == admission.World &&
        _devices.TryResolve(admission.Policy.HostDeviceId, out var host) && host.Ready && host.Owner == admission.Principal &&
        host.DeviceEpoch == admission.HostEpoch && host.AuthorityEpoch == admission.Policy.HostAuthorityEpoch &&
        (admission.ExpiresAtTicks == null || now.Ticks < admission.ExpiresAtTicks) &&
        NCPartyRef.TryParse(admission.Principal, out var owner) && IsPrincipalActive(owner);

    public bool CanUse(NCMetaServiceAdmission admission, Guid grantId, string slot, string targetId, string contract,
        MetaApiMember member) => CanContinue(admission) && _devices.TryResolve(targetId, out var target) && target.Ready &&
        target.Owner == admission.Principal && admission.Targets.TryGetValue(targetId, out var epoch) && target.DeviceEpoch == epoch &&
        _persistence.TryCaptureTime(out _, out var now) && MetaServiceAuthority.Allows(admission.Policy, grantId,
            admission.Revision, slot, targetId, contract, member.QualifiedName, member.Scope, target.AuthorityEpoch, now.Ticks);

    private CachedPolicy? CachePolicy(MetaServiceGrantPolicy policy, NCSessionEpoch world)
    {
        if (_policyWorld != world) { ClearServicePolicies(); _policyWorld = world; }
        if (_policies.TryGetValue(policy.InstallationId, out var known) && known.Policy.Revision == policy.Revision) return known;
        InvalidateServicePolicy(policy.InstallationId);
        var bytes = MetaServiceAuthority.EstimateBytes(policy);
        if (bytes > 32 * 1024 * 1024) return null;
        while ((_policies.Count >= 4096 || _policyBytes + bytes > 32 * 1024 * 1024) && _policyOrder.First is { } oldest)
            InvalidateServicePolicy(oldest.Value);
        var entry = new CachedPolicy(policy, checked(++_policyVersion), _policyOrder.AddLast(policy.InstallationId), bytes);
        _policies.Add(policy.InstallationId, entry); _policyBytes += bytes;
        if (!_policyPrincipals.TryGetValue(policy.Principal, out var principalPolicies))
            _policyPrincipals.Add(policy.Principal, principalPolicies = []);
        principalPolicies.Add(policy.InstallationId);
        foreach (var id in policy.Grants.Select(grant => grant.TargetDeviceId).Append(policy.HostDeviceId).Distinct(StringComparer.Ordinal))
        {
            if (!_policyDevices.TryGetValue(id, out var installations)) _policyDevices.Add(id, installations = []);
            installations.Add(policy.InstallationId);
        }
        if (MetaServiceAuthority.EarliestExpiry(policy) is { } due)
            _policyDeadlines.Add((due, policy.InstallationId, policy.Revision));
        return entry;
    }

    public void InvalidateServicePolicy(Guid installationId)
    {
        _policyVersion = checked(_policyVersion + 1);
        if (!_policies.Remove(installationId, out var cached)) return;
        _policyBytes -= cached.Bytes; _policyOrder.Remove(cached.Node);
        if (_policyPrincipals.TryGetValue(cached.Policy.Principal, out var principalPolicies) &&
            principalPolicies.Remove(installationId) && principalPolicies.Count == 0) _policyPrincipals.Remove(cached.Policy.Principal);
        if (MetaServiceAuthority.EarliestExpiry(cached.Policy) is { } due)
            _policyDeadlines.Remove((due, installationId, cached.Policy.Revision));
        foreach (var id in cached.Policy.Grants.Select(grant => grant.TargetDeviceId).Append(cached.Policy.HostDeviceId).Distinct(StringComparer.Ordinal))
            if (_policyDevices.TryGetValue(id, out var installations) && installations.Remove(installationId) && installations.Count == 0)
                _policyDevices.Remove(id);
        if (ServiceAuthorityChanged == null) return;
        foreach (Action<Guid> subscriber in ServiceAuthorityChanged.GetInvocationList())
            try { subscriber(installationId); }
            catch (Exception exception) { Log.Error($"META service-grant invalidation failed: {exception}"); }
    }
    private void ClearServicePolicies()
    {
        _policyVersion = checked(_policyVersion + 1);
        foreach (var id in _policies.Keys.ToArray()) InvalidateServicePolicy(id);
    }
    private void OnDeviceChanged(NCMetaDeviceEntry entry)
    {
        if (_policyDevices.TryGetValue(entry.Id, out var policies))
            foreach (var id in policies.ToArray()) InvalidateServicePolicy(id);
    }
    private void UpdateServiceExpiry()
    {
        if (!_persistence.TryCaptureTime(out var world, out var now)) return;
        if (_policyWorld != world) { ClearServicePolicies(); _policyWorld = world; }
        for (var work = 0; work < 32 && _policyDeadlines.Count != 0 && _policyDeadlines.Min.Tick <= now.Ticks; work++)
            InvalidateServicePolicy(_policyDeadlines.Min.Installation);
    }
}
