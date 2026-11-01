// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Native scalar proof for admitted messages which may outlive a cleanly completed VM.
using Content.Meta.Hosting;
using Content.Meta.Language;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Authority;

/// <summary>No policy, VM, artifact, heap value or user-session object is retained.</summary>
public sealed class NCMetaDeferredServiceUse
{
    internal NCMetaDeferredServiceUse(Guid installation, long cacheVersion, long revision, NCSessionEpoch world,
        string host, string principal, string digest, ulong hostEpoch, ulong hostAuthority, string target,
        ulong targetEpoch, ulong targetAuthority, Guid grant, string slot, string contract, string member, string scope,
        long? expiresAtTicks)
    {
        Installation = installation; CacheVersion = cacheVersion; Revision = revision; World = world;
        Host = host; Principal = principal; Digest = digest; HostEpoch = hostEpoch; HostAuthority = hostAuthority;
        Target = target; TargetEpoch = targetEpoch; TargetAuthority = targetAuthority; Grant = grant;
        Slot = slot; Contract = contract; Member = member; Scope = scope;
        ExpiresAtTicks = expiresAtTicks;
    }
    internal Guid Installation { get; }
    internal long CacheVersion { get; }
    internal long Revision { get; }
    internal NCSessionEpoch World { get; }
    internal string Host { get; }
    internal string Principal { get; }
    internal string Digest { get; }
    internal ulong HostEpoch { get; }
    internal ulong HostAuthority { get; }
    internal string Target { get; }
    internal ulong TargetEpoch { get; }
    internal ulong TargetAuthority { get; }
    internal Guid Grant { get; }
    internal string Slot { get; }
    internal string Contract { get; }
    internal string Member { get; }
    internal string Scope { get; }
    internal long? ExpiresAtTicks { get; }
}

public sealed partial class NCMetaAuthoritySystem
{
    public bool TryCaptureDeferredUse(NCMetaServiceAdmission admission, Guid grantId, string slot,
        string targetId, string contract, MetaApiMember member, out NCMetaDeferredServiceUse use)
    {
        use = null!;
        if (!CanUse(admission, grantId, slot, targetId, contract, member) ||
            !_devices.TryResolve(targetId, out var target)) return false;
        use = new(admission.InstallationId, admission.CacheVersion, admission.Revision, admission.World,
            admission.HostDeviceId, admission.Principal, admission.ArtifactDigest, admission.HostEpoch,
            admission.Policy.HostAuthorityEpoch, targetId, target.DeviceEpoch, target.AuthorityEpoch,
            grantId, slot, contract, member.QualifiedName, member.Scope, admission.ExpiresAtTicks);
        return true;
    }

    // Game-thread bounded read. Cache eviction, changed policy or any unknown principal is fail-closed.
    // The original issuer is deliberately absent: an unattended organization service survives issuer logout.
    public bool CanUseDeferred(NCMetaDeferredServiceUse use)
    {
        if (!_policies.TryGetValue(use.Installation, out var cached) || cached.Version != use.CacheVersion ||
            _changingPolicies.ContainsKey(use.Installation) || cached.Policy.Revision != use.Revision ||
            cached.Policy.HostDeviceId != use.Host || cached.Policy.Principal != use.Principal ||
            cached.Policy.ArtifactDigest != use.Digest || cached.Policy.HostAuthorityEpoch != use.HostAuthority ||
            !_persistence.TryCaptureTime(out var world, out var now) || world != use.World ||
            use.ExpiresAtTicks is { } expiry && now.Ticks >= expiry ||
            !_devices.TryResolve(use.Host, out var host) || !host.Ready || host.Owner != use.Principal ||
            host.DeviceEpoch != use.HostEpoch || host.AuthorityEpoch != use.HostAuthority ||
            !_devices.TryResolve(use.Target, out var target) || !target.Ready || target.Owner != use.Principal ||
            target.DeviceEpoch != use.TargetEpoch || target.AuthorityEpoch != use.TargetAuthority ||
            !NCPartyRef.TryParse(use.Principal, out var principal) || !IsPrincipalActive(principal)) return false;
        return MetaServiceAuthority.Allows(cached.Policy, use.Grant, use.Revision, use.Slot,
            use.Target, use.Contract, use.Member, use.Scope, target.AuthorityEpoch, now.Ticks);
    }
}
