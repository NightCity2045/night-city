// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using Content.Shared._NC.Meta.Network;

using Content.Shared._NC.Meta;

namespace Content.Server._NC.Meta.Network;

public sealed partial class NCMetaNetworkSystem
{
    /// <summary>Isolated native load-generator input. Uses actual owned physical ports and the normal delayed packet path.</summary>
    public bool TrySendPerformanceStimulus(EntityUid sender, EntityUid actor, EntityUid receiver, string payload)
    {
        if (Environment.GetEnvironmentVariable("NC_DIAGNOSTICS_ISOLATED") != "1" ||
            Environment.GetEnvironmentVariable("NC_DIAGNOSTICS_PROFILE") != "fixture-admin" ||
            !_devices.CanManage(sender, actor, requireRange: false) || !_devices.TryGetEntry(sender, out var sourceDevice) ||
            !_devices.TryGetEntry(receiver, out var targetDevice) || sourceDevice.Owner != targetDevice.Owner ||
            !sourceDevice.Ready || !targetDevice.Ready || !CanRoute(sender, receiver) ||
            !TryComp<NCMetaMessagePortComponent>(sender, out var source) || !TryComp<NCMetaMessagePortComponent>(receiver, out var target) ||
            !TryComp<NCMetaPortComponent>(sender, out var physical) || !_states.TryGetValue(sender, out var outgoing) || !_states.TryGetValue(receiver, out var incoming)) return false;
        if (!_persistence.TryCaptureTime(out var world, out _) || _chains.Count >= 256 || _nextContext == uint.MaxValue) return false;
        var authority = new NativeOutboundAuthorization(this, sender, receiver, actor, sourceDevice.Owner,
            sourceDevice.Id, targetDevice.Id, sourceDevice.DeviceEpoch, targetDevice.DeviceEpoch,
            sourceDevice.AuthorityEpoch, targetDevice.AuthorityEpoch, world, _runtime.OperationalPolicyEpoch);
        if (!authority.IsCurrent()) return false;
        var bytes = Encoding.UTF8.GetByteCount(payload);
        if (bytes > Math.Min(2048, physical.PayloadBytes) || source.AllowedTopics.Count > 0 && !source.AllowedTopics.Contains("load") ||
            target.AllowedTopics.Count > 0 && !target.AllowedTopics.Contains("load") ||
            source.AllowedPeers.Count > 0 && !source.AllowedPeers.Contains(target.Address) || target.AllowedPeers.Count > 0 && !target.AllowedPeers.Contains(source.Address) ||
            _envelopes.Count >= 256 || incoming.Incoming >= Math.Min(16, target.QueueMessages) || incoming.IncomingBytes + bytes + 128 > Math.Min(32768, target.QueueBytes) ||
            outgoing.Outgoing >= 16 || outgoing.OutgoingBytes + bytes + 128 > 32768) return false;
        if (!_ownerRates.TryGetValue(sourceDevice.Owner, out var owner))
        {
            if (_ownerRates.Count >= 4096) return false;
            _ownerRates.Add(sourceDevice.Owner, owner = new Content.Meta.Hosting.MetaRateLimit(20, 10));
        }
        if (!outgoing.Messages.TryConsume(_now) || !owner.TryConsume(_now) || !_routeWork.TryConsume(_now) || !_bytes.TryConsume(_now, bytes + 128)) return false;
        // No guest or synthetic sensor result is created here. OnPacket validates this authenticated
        // envelope after the real DeviceNetwork queue, and constructs each process-owned Message DTO.
        var id = Guid.NewGuid();
        var context = 0x4d45544100000000UL | ++_nextContext;
        var chain = new Chain { Expires = _now + 5000, OriginAuthority = authority, References = 1 };
        _chains.Add(context, chain); _chainExpirations.Enqueue(context, chain.Expires);
        var envelope = new Envelope(id, sender, receiver, source.Address, "load", payload, sourceDevice.DeviceEpoch, targetDevice.DeviceEpoch,
            sourceDevice.AuthorityEpoch, targetDevice.AuthorityEpoch, context, bytes + 128, _now + 1000, authority);
        _envelopes.Add(id, envelope); outgoing.Outgoing++; outgoing.OutgoingBytes += envelope.Bytes;
        incoming.Incoming++; incoming.IncomingBytes += envelope.Bytes;
        _deliveries.Enqueue(id, _now + Math.Clamp(source.LatencyMilliseconds, 1, 500)); _expirations.Enqueue(id, envelope.Expires);
        return true;
    }
}
