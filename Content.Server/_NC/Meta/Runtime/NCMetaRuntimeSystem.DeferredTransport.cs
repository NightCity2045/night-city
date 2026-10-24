// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Commerce;
using Content.Server._NC.Meta.Authority;
using Content.Server._NC.Meta.Commerce;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Runtime;

/// <summary>Small native context; implementations retain only a system and scalar IDs, never a session or VM.</summary>
public interface INCMetaDeferredInvocationAuthority
{
    bool IsCurrent();
}

/// <summary>Bounded native send authority, independent of the lifetime of a cleanly completed guest stack.</summary>
public sealed class NCMetaOutboundAuthority
{
    internal NCMetaOutboundAuthority(Guid generation, NCMetaDeferredServiceUse service, string semanticDigest,
        long? entitlementDeadline, NCMetaRunContactRequirement? contact, NCMetaManagementProof? attendance,
        INCMetaDeferredInvocationAuthority? actor, string hostProfile, int hostProfileRevision,
        string? targetProfile, int targetProfileRevision, Guid operationalEpoch)
    {
        Generation = generation; Service = service; SemanticDigest = semanticDigest;
        EntitlementDeadline = entitlementDeadline; Contact = contact; Attendance = attendance; Actor = actor;
        HostProfile = hostProfile; HostProfileRevision = hostProfileRevision;
        TargetProfile = targetProfile; TargetProfileRevision = targetProfileRevision;
        OperationalEpoch = operationalEpoch;
    }
    public Guid Generation { get; }
    internal NCMetaDeferredServiceUse Service { get; }
    internal string SemanticDigest { get; }
    internal long? EntitlementDeadline { get; }
    internal NCMetaRunContactRequirement? Contact { get; }
    internal NCMetaManagementProof? Attendance { get; }
    internal INCMetaDeferredInvocationAuthority? Actor { get; }
    internal string HostProfile { get; }
    internal int HostProfileRevision { get; }
    internal string? TargetProfile { get; }
    internal int TargetProfileRevision { get; }
    internal Guid OperationalEpoch { get; }
}

public sealed partial class NCMetaRuntimeSystem
{
    private sealed class OwnerInvocationAuthority(NCMetaRuntimeSystem system, EntityUid host,
        EntityUid actor, string hostId, string owner) : INCMetaDeferredInvocationAuthority
    {
        public bool IsCurrent() => system._devices.TryGetEntry(host, out var device) &&
            device.Id == hostId && device.Owner == owner && system._devices.CanManage(host, actor);
    }

    private INCMetaDeferredInvocationAuthority? CaptureOwnerInvocationAuthority(EntityUid host, EntityUid actor) =>
        _devices.TryGetEntry(host, out var device) && _devices.CanManage(host, actor)
            ? new OwnerInvocationAuthority(this, host, actor, device.Id, device.Owner) : null;

    public bool TryCaptureOutboundAuthority(NCMetaDeviceInvocation invocation, out NCMetaOutboundAuthority authority)
    {
        authority = null!;
        var instance = invocation.Runtime;
        if (!instance.Process.TryGetHostToken(invocation.Request.Capability, out var token) ||
            !instance.Bindings.TryGetValue(token, out var binding) || binding.Device.Id != invocation.DeviceId ||
            binding.Device.Entity != invocation.Device || !CanUse(instance, binding, invocation.Member) ||
            !_serviceAuthority.TryCaptureDeferredUse(instance.ServiceAdmission, binding.GrantId, binding.Slot,
                binding.Device.Id, binding.ContractHash, invocation.Member, out var service) ||
            !TryComp<NCMetaHostComponent>(instance.Host, out var host) ||
            !_prototypes.TryIndex(host.Profile, out var profile)) return false;
        var actor = instance.InvocationDeferredAuthority;
        if (actor == null && instance.Process.CurrentEventAuthorityToken != 0)
            instance.ActorAuthorities.TryGetValue(instance.Process.CurrentEventAuthorityToken, out actor);
        if ((instance.InvocationActor != null || instance.Process.CurrentEventAuthorityToken != 0) && actor == null ||
            !IsDeferredContextCurrent(actor)) return false;
        NCMetaRunContactRequirement? contact = null;
        if (instance.MarketplaceContact is { } original)
        {
            if (!_runContacts.TryGetValue(instance.Generation, out var current)) return false;
            contact = original with { LastOnlineTicks = current.LastOnlineTicks };
        }
        string? targetProfile = null;
        var targetRevision = 0;
        if (TryComp<NCMetaHostComponent>(invocation.Device, out var target))
        {
            if (!_prototypes.TryIndex(target.Profile, out var targetDefinition)) return false;
            targetProfile = target.Profile.Id; targetRevision = targetDefinition.Revision;
        }
        authority = new(instance.Generation, service, instance.Artifact.Digest, instance.EntitlementExpiresAtTicks,
            contact, instance.ServiceAdmission.AllowUnattended ? null : instance.AttendingProof, actor,
            host.Profile.Id, profile.Revision, targetProfile, targetRevision, _operationalPolicyEpoch);
        return true;
    }

    public bool CanUseOutboundAuthority(NCMetaOutboundAuthority authority)
    {
        var service = authority.Service;
        if (authority.OperationalEpoch != _operationalPolicyEpoch || !OperationalEnabled ||
            _revokedHashes.Contains(service.Digest) || _revokedHashes.Contains(authority.SemanticDigest) ||
            !_serviceAuthority.CanUseDeferred(service) || !_persistence.TryCaptureTime(out var world, out var now) ||
            world != service.World || authority.EntitlementDeadline is { } expiry && now.Ticks >= expiry ||
            authority.Attendance != null && !_serviceAuthority.CanAttendService(authority.Attendance) ||
            !IsDeferredContextCurrent(authority.Actor) || !_devices.TryResolve(service.Host, out var host) ||
            !_devices.TryResolve(service.Target, out var target) || !_devices.IsPowered(host.Entity) ||
            !_devices.IsPowered(target.Entity) || !_hardware.CanUseDevice(host.Entity) || !_hardware.CanUseDevice(target.Entity) ||
            !TryComp<NCMetaHostComponent>(host.Entity, out var hardware) || hardware.Disabled ||
            hardware.Profile.Id != authority.HostProfile || !_prototypes.TryIndex(hardware.Profile, out var profile) ||
            profile.Revision != authority.HostProfileRevision || !CanReach(host.Entity, target.Entity)) return false;
        if (authority.TargetProfile is { } targetProfile &&
            (!TryComp<NCMetaHostComponent>(target.Entity, out var endpoint) || endpoint.Disabled ||
                endpoint.Profile.Id != targetProfile || !_prototypes.TryIndex(endpoint.Profile, out var targetDefinition) ||
                targetDefinition.Revision != authority.TargetProfileRevision)) return false;
        if (authority.Contact is not { } contact) return true;
        if (_runLicensing.CanMaintainRunContact(contact)) return true;
        return contact.Policy == MetaOfflinePolicy.GracePeriod && contact.LastOnlineTicks <= now.Ticks &&
            now.Ticks - contact.LastOnlineTicks < contact.GraceTicks;
    }

    internal static bool IsDeferredContextCurrent(INCMetaDeferredInvocationAuthority? authority)
    {
        try { return authority?.IsCurrent() ?? true; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { return false; }
    }
}
