// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Observes ordinary native events, never manufactures a VM result or grant.
using System;
using System.Collections.Generic;
using Content.Server._NC.Meta.Network;
using Content.Shared._NC.Meta;
using Content.Shared.DeviceNetwork;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.DeviceNetwork.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Reflection;

namespace Content.IntegrationTests.Tests._NC.Meta;

[Reflect(false)]
public sealed class NCMetaNativePacketObserverSystem : EntitySystem
{
    public sealed record ObservedPacket(EntityUid Receiver, EntityUid Sender, int Network, uint Frequency,
        string SenderAddress, string? Address, string EnvelopeId);
    private readonly HashSet<EntityUid> _receivers = [];
    private readonly HashSet<EntityUid> _lostReceivers = [];
    private readonly List<ObservedPacket> _packets = [];
    public IReadOnlyList<ObservedPacket> Packets => _packets;
    public int Dropped;
    public int SamplesDropped;
    public EntityUid? SubstituteSenderOnce;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCMetaPortComponent, DeviceNetworkPacketEvent>(OnPacket,
            before: [typeof(NCMetaNetworkSystem)]);
        SubscribeLocalEvent<NCMetaPortComponent, BeforePacketSentEvent>(OnBeforePacket);
    }

    public void Observe(EntityUid receiver)
    {
        if (!HasComp<NCMetaPortComponent>(receiver) || !HasComp<DeviceNetworkComponent>(receiver))
            throw new InvalidOperationException("Native packet observation requires a real META physical/native-network endpoint.");
        if (_receivers.Count >= 64) throw new InvalidOperationException("Native packet test observer bound.");
        _receivers.Add(receiver);
    }

    public void SetLoss(EntityUid receiver, bool lost)
    {
        if (!_receivers.Contains(receiver)) throw new InvalidOperationException("Unowned test receiver.");
        if (lost) _lostReceivers.Add(receiver); else _lostReceivers.Remove(receiver);
    }

    public void Clear()
    {
        _receivers.Clear(); _lostReceivers.Clear(); _packets.Clear();
        Dropped = 0; SamplesDropped = 0; SubstituteSenderOnce = null;
    }

    private void OnBeforePacket(EntityUid uid, NCMetaPortComponent component, BeforePacketSentEvent args)
    {
        if (!_lostReceivers.Contains(uid)) return;
        Dropped++; args.Cancel();
    }

    private void OnPacket(EntityUid uid, NCMetaPortComponent component, DeviceNetworkPacketEvent args)
    {
        if (!_receivers.Contains(uid) || !args.Data.TryGetValue<string>("nc-meta-envelope", out var envelope)) return;
        if (_packets.Count >= 128) SamplesDropped++;
        else _packets.Add(new(uid, args.Sender, args.NetId, args.Frequency, args.SenderAddress, args.Address, envelope));
        // A separate adversarial case observes a genuine pending envelope but forges its native sender.
        // The META handler must reject it even though the opaque envelope ID is otherwise genuine.
        if (SubstituteSenderOnce is { } forged)
        { args.Sender = forged; SubstituteSenderOnce = null; }
    }

    public static NetworkPayload ReplayPayload(ObservedPacket packet) =>
        new() { ["nc-meta-envelope"] = packet.EnvelopeId };
}
