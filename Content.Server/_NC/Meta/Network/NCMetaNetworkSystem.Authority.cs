// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Network;

public sealed partial class NCMetaNetworkSystem
{
    private interface IOutboundAuthorization
    {
        Guid? Generation { get; }
        INCMetaDeferredInvocationAuthority? InvocationAuthority { get; }
        bool IsCurrent();
    }
    private sealed class GuestOutboundAuthorization(NCMetaRuntimeSystem runtime, NCMetaOutboundAuthority authority)
        : IOutboundAuthorization
    {
        public Guid? Generation => authority.Generation;
        public INCMetaDeferredInvocationAuthority? InvocationAuthority => authority.Actor;
        public bool IsCurrent() => runtime.CanUseOutboundAuthority(authority);
    }
    // Dedicated native fixture-admin ingress, never a fabricated guest policy or a null-proof bypass.
    private sealed class NativeOutboundAuthorization(NCMetaNetworkSystem system, EntityUid sender, EntityUid receiver,
        EntityUid actor, string owner, string senderId, string receiverId, ulong senderEpoch, ulong receiverEpoch,
        ulong senderAuthority, ulong receiverAuthority, NCSessionEpoch world, Guid operationalEpoch)
        : IOutboundAuthorization, INCMetaDeferredInvocationAuthority
    {
        public Guid? Generation => null;
        public INCMetaDeferredInvocationAuthority InvocationAuthority => this;
        public bool IsCurrent() => system._runtime.OperationalEnabled && system._runtime.OperationalPolicyEpoch == operationalEpoch &&
            system._persistence.TryCaptureTime(out var epoch, out _) && epoch == world &&
            system._devices.TryGetEntry(sender, out var from) && from.Ready && from.Id == senderId &&
            from.Owner == owner && from.DeviceEpoch == senderEpoch && from.AuthorityEpoch == senderAuthority &&
            system._devices.TryGetEntry(receiver, out var to) && to.Ready && to.Id == receiverId &&
            to.Owner == owner && to.DeviceEpoch == receiverEpoch && to.AuthorityEpoch == receiverAuthority &&
            system._devices.CanManage(sender, actor, requireRange: false) && system.CanRoute(sender, receiver);
    }
    private readonly Dictionary<Guid, HashSet<Guid>> _outboundByGeneration = [];

    private void TrackOutbound(Envelope envelope)
    {
        if (envelope.Authorization.Generation is not { } generation) return;
        if (!_outboundByGeneration.TryGetValue(generation, out var pending))
            _outboundByGeneration.Add(generation, pending = []);
        pending.Add(envelope.Id);
    }
    private void ReleaseOutbound(Envelope envelope)
    {
        if (envelope.Authorization.Generation is not { } generation ||
            !_outboundByGeneration.TryGetValue(generation, out var pending)) return;
        pending.Remove(envelope.Id);
        if (pending.Count == 0) _outboundByGeneration.Remove(generation);
    }

    private void OnRuntimeStopped(NCMetaRuntimeInstance runtime)
    {
        if (_subscribed.Remove(runtime.Process)) runtime.Process.EventContextReleased -= ReleaseContext;
        if (runtime.NativeStopReason == MetaStopReason.Completed ||
            !_outboundByGeneration.TryGetValue(runtime.Generation, out var pending)) return;
        foreach (var id in pending.ToArray()) Drop(id);
    }

    private bool IsOutboundCurrent(Envelope envelope)
    {
        try
        {
            return envelope.Authorization.IsCurrent() && (envelope.Context == 0 ||
                _chains.TryGetValue(envelope.Context, out var chain) && chain.Expires > _now &&
                NCMetaRuntimeSystem.IsDeferredContextCurrent(chain.OriginAuthority));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { return false; }
    }
}
