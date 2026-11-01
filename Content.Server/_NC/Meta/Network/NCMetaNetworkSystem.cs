// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using Content.Meta.Hosting;
using Content.Meta.Diagnostics;
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Server.DeviceNetwork.Systems;
using Content.Server.Popups;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Network;
using Content.Shared.DeviceNetwork;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.DeviceNetwork.Events;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Power.EntitySystems;
using Content.Shared.PowerCell;
using Content.Shared.PowerCell.Components;
using Content.Shared.Verbs;

namespace Content.Server._NC.Meta.Network;

/// <summary>Bounded physical routes and authenticated envelopes, admitted before DeviceNetwork's native queue.</summary>
public sealed partial class NCMetaNetworkSystem : EntitySystem, INCMetaDeviceAdapter
{
    private sealed class PortState
    {
        public readonly MetaRateLimit Messages = new(10, 5);
        public int Incoming;
        public int IncomingBytes;
        public int Outgoing;
        public int OutgoingBytes;
    }
    private sealed class Chain
    {
        public int Copies = 64;
        public int Sends = 8;
        public int References;
        public long Expires;
        public INCMetaDeferredInvocationAuthority? OriginAuthority;
    }
    private sealed record Envelope(Guid Id, EntityUid Sender, EntityUid Receiver, string SenderAddress,
        string Topic, string Payload, ulong SenderEpoch, ulong ReceiverEpoch, ulong AuthorityEpoch, ulong ReceiverAuthorityEpoch,
        ulong Context, int Bytes, long Expires, IOutboundAuthorization Authorization);
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private DeviceNetworkSystem _network = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private PowerCellSystem _cells = default!;
    private readonly Dictionary<string, EntityUid> _addresses = new(StringComparer.Ordinal);
    private readonly Dictionary<EntityUid, PortState> _states = [];
    private readonly Dictionary<string, HashSet<EntityUid>> _segments = new(StringComparer.Ordinal);
    private readonly HashSet<EntityUid> _physicalPorts = [];
    private readonly Dictionary<EntityUid, HashSet<EntityUid>> _cablesByPort = [];
    private readonly Dictionary<Guid, Envelope> _envelopes = [];
    private readonly PriorityQueue<Guid, long> _deliveries = new();
    private readonly PriorityQueue<Guid, long> _expirations = new();
    private readonly Dictionary<string, MetaRateLimit> _ownerRates = new(StringComparer.Ordinal);
    private readonly MetaRateLimit _routeWork = new(512, 256);
    private readonly MetaRateLimit _bytes = new(262144, 131072);
    private readonly Dictionary<ulong, Chain> _chains = [];
    private readonly PriorityQueue<ulong, long> _chainExpirations = new();
    private readonly HashSet<MetaProcess> _subscribed = [];
    private uint _nextContext;
    private long _now;
    private Content.Shared._NC.Persistence.Time.NCSessionEpoch _epoch;
    private bool _hasEpoch;
    /// <summary>Trusted bounded accounting only; raised per recipient after native packet-queue admission.</summary>
    public event Action<EntityUid, int>? TransmissionAdmitted;
    public IEnumerable<string> Members => ["meta.net.MessagePort.send"];
    public bool CanBind(EntityUid device, string capability) => capability == "meta.net.MessagePort" && HasComp<NCMetaMessagePortComponent>(device) && HasComp<NCMetaPortComponent>(device);

    public override void Initialize()
    {
        base.Initialize();
        InitializeRadio();
        InitializeRoutingNotifications();
        _runtime.RegisterAdapter(this); _runtime.RuntimeStopped += OnRuntimeStopped;
        _devices.RegisterPowerPolicy(uid => HasComp<NCMetaPortComponent>(uid) && HasComp<PowerCellSlotComponent>(uid) ? _cells.HasCharge(uid, 1) : null);
        SubscribeLocalEvent<NCMetaMessagePortComponent, MapInitEvent>(OnPortInit, after: [typeof(NCMetaDeviceRegistrySystem)]);
        SubscribeLocalEvent<NCMetaPortComponent, MapInitEvent>(OnPhysicalPortInit, after: [typeof(NCMetaDeviceRegistrySystem)]);
        SubscribeLocalEvent<NCMetaPortComponent, ComponentShutdown>(OnPhysicalPortShutdown);
        SubscribeLocalEvent<NCMetaPortComponent, GetVerbsEvent<AlternativeVerb>>(OnPhysicalPortVerbs);
        SubscribeLocalEvent<NCMetaMessagePortComponent, ComponentShutdown>(OnPortShutdown);
        SubscribeLocalEvent<NCMetaMessagePortComponent, DeviceNetworkPacketEvent>(OnPacket);
        SubscribeLocalEvent<NCMetaMessagePortComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<NCMetaNetworkCableComponent, MapInitEvent>(OnCableInit, after: [typeof(NCMetaDeviceRegistrySystem)]);
        SubscribeLocalEvent<NCMetaNetworkCableComponent, ComponentShutdown>(OnCableShutdown);
        SubscribeLocalEvent<NCMetaNetworkCableComponent, AfterInteractEvent>(OnCableInteract);
        SubscribeLocalEvent<NCMetaNetworkCableComponent, GetVerbsEvent<AlternativeVerb>>(OnCableVerbs);
    }
    public override void Shutdown()
    {
        _devices.Changed -= OnRoutingDeviceChanged;
        _runtime.RuntimeStopped -= OnRuntimeStopped;
        foreach (var process in _subscribed) process.EventContextReleased -= ReleaseContext;
        _subscribed.Clear(); _envelopes.Clear(); _chains.Clear();
        _outboundByGeneration.Clear();
        base.Shutdown();
    }
    private void OnPhysicalPortInit(Entity<NCMetaPortComponent> ent, ref MapInitEvent args)
    { if (_physicalPorts.Count < 4096) _physicalPorts.Add(ent.Owner); ResolveCables(ent.Owner); RoutingChanged?.Invoke(); }
    private void OnPhysicalPortShutdown(Entity<NCMetaPortComponent> ent, ref ComponentShutdown args)
    { _physicalPorts.Remove(ent.Owner); UnresolveCables(ent.Owner); RoutingChanged?.Invoke(); }
    private void OnPhysicalPortVerbs(Entity<NCMetaPortComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !_devices.CanManage(ent.Owner, args.User) || !_devices.TryGetEntry(ent.Owner, out var device)) return;
        var actor = args.User; var offered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in _physicalPorts.Take(256))
        {
            if (offered.Count >= 8) break;
            if (target == ent.Owner || !_devices.TryGetEntry(target, out var peer) || peer.Owner != device.Owner || !TryComp<NCMetaPortComponent>(target, out var port) || port.NetworkId.Length is < 1 or > 128 || port.NetworkId == ent.Comp.NetworkId || !_interaction.InRangeUnobstructed(ent.Owner, target, range: 8) || !offered.Add(port.NetworkId)) continue;
            var captured = target;
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-network-join-segment", ("segment", port.NetworkId)), Act = () => TryJoinNetwork(ent.Owner, captured, actor) });
        }
    }
    public bool TryJoinNetwork(EntityUid port, EntityUid peer, EntityUid actor)
    {
        if (!_devices.CanManage(port, actor) || !_devices.CanManage(peer, actor, requireRange: false) || !TryComp<NCMetaPortComponent>(port, out var source) || !TryComp<NCMetaPortComponent>(peer, out var target) || target.NetworkId.Length is < 1 or > 128 || !_interaction.InRangeUnobstructed(port, peer, range: 8)) return false;
        if (_segments.TryGetValue(target.NetworkId, out var group) && group.Count >= 64 && !group.Contains(port)) return false;
        if (_segments.TryGetValue(source.NetworkId, out var previous)) { previous.Remove(port); if (previous.Count == 0) _segments.Remove(source.NetworkId); }
        source.NetworkId = target.NetworkId;
        if (HasComp<NCMetaMessagePortComponent>(port))
        {
            if (!_segments.TryGetValue(target.NetworkId, out group)) _segments.Add(target.NetworkId, group = []);
            group.Add(port);
        }
        _devices.TryInvalidate(port); return true;
    }
    public EntityUid[] GetReachablePorts(EntityUid from, int limit = 64)
    {
        if (!_devices.TryGetEntry(from, out var source) || source.Owner.Length == 0) return [];
        var result = new List<EntityUid>();
        foreach (var target in _physicalPorts.Take(256))
        {
            if (result.Count >= Math.Clamp(limit, 0, 64)) break;
            if (_devices.TryGetEntry(target, out var candidate) && candidate.Owner == source.Owner && CanRoute(from, target)) result.Add(target);
        }
        return result.ToArray();
    }
    private void OnPortInit(Entity<NCMetaMessagePortComponent> ent, ref MapInitEvent args)
    {
        if (!_devices.TryGetEntry(ent.Owner, out var device) || _addresses.Count >= 4096) return;
        if (ent.Comp.Address.Length == 0) ent.Comp.Address = "local:" + device.Id;
        if (!ValidAddress(ent.Comp.Address) || !_addresses.TryAdd(ent.Comp.Address, ent.Owner)) return;
        _states.Add(ent.Owner, new PortState());
        if (TryComp<NCMetaPortComponent>(ent.Owner, out var physical) && physical.NetworkId.Length > 0)
        {
            if (!_segments.TryGetValue(physical.NetworkId, out var segment)) _segments.Add(physical.NetworkId, segment = []);
            if (segment.Count < 64) segment.Add(ent.Owner);
        }
        var native = EnsureComp<DeviceNetworkComponent>(ent.Owner);
        if (native.ReceiveFrequency == null) _network.SetReceiveFrequency(ent.Owner, 1945, native);
        _network.ConnectDevice(ent.Owner, native);
    }
    private void OnPortShutdown(Entity<NCMetaMessagePortComponent> ent, ref ComponentShutdown args)
    {
        if (_addresses.TryGetValue(ent.Comp.Address, out var current) && current == ent.Owner) _addresses.Remove(ent.Comp.Address);
        foreach (var envelope in _envelopes.Values.Where(value => value.Sender == ent.Owner || value.Receiver == ent.Owner).ToArray()) Drop(envelope.Id);
        UnresolveCables(ent.Owner);
        _states.Remove(ent.Owner); _cablesByPort.Remove(ent.Owner);
        if (TryComp<NCMetaPortComponent>(ent.Owner, out var physical) && _segments.TryGetValue(physical.NetworkId, out var segment)) { segment.Remove(ent.Owner); if (segment.Count == 0) _segments.Remove(physical.NetworkId); }
    }
    private void OnExamined(Entity<NCMetaMessagePortComponent> ent, ref ExaminedEvent args)
    {
        if (args.IsInDetailsRange && ValidAddress(ent.Comp.Address)) args.PushMarkup(Loc.GetString("nc-meta-network-address", ("address", ent.Comp.Address)));
    }
    private void OnCableInit(Entity<NCMetaNetworkCableComponent> ent, ref MapInitEvent args) => RegisterCable(ent);
    private void OnCableShutdown(Entity<NCMetaNetworkCableComponent> ent, ref ComponentShutdown args) { UnregisterCable(ent); UnindexCable(ent); RoutingChanged?.Invoke(); }
    private void OnCableInteract(Entity<NCMetaNetworkCableComponent> ent, ref AfterInteractEvent args)
    {
        if (args.Handled || args.Target is not { } target || !HasComp<NCMetaPortComponent>(target)) return;
        args.Handled = true;
        var success = TryConnectCable(ent.AsNullable(), args.User, target);
        _popup.PopupEntity(Loc.GetString(success ? "nc-meta-network-cable-connected" : "nc-meta-network-cable-rejected"), ent.Owner, args.User);
    }
    private void OnCableVerbs(Entity<NCMetaNetworkCableComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract) return;
        var user = args.User;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-network-unplug"), Act = () => TryDisconnectCable(ent.AsNullable(), user) });
    }
    public bool TryConnectCable(Entity<NCMetaNetworkCableComponent?> cable, EntityUid user, EntityUid port)
    {
        if (!_registeredCables.Contains(cable.Owner) && _registeredCables.Count >= 4096) return false;
        if (!Resolve(cable.Owner, ref cable.Comp) || !HasComp<NCMetaPortComponent>(port) || !_devices.TryGetEntry(port, out var endpoint) || !_interaction.InRangeUnobstructed(user, cable.Owner, range: 2) || !_interaction.InRangeUnobstructed(user, port, range: 2)) return false;
        if (cable.Comp.DeviceAId.Length == 0) { cable.Comp.DeviceAId = endpoint.Id; cable.Comp.PortA = port; RegisterCable((cable.Owner, cable.Comp)); RoutingChanged?.Invoke(); return true; }
        if (cable.Comp.PortB != null || cable.Comp.PortA is not { } first || first == port || !CanWire(first, port, cable.Comp.MaximumLength)) return false;
        cable.Comp.DeviceBId = endpoint.Id; cable.Comp.PortB = port;
        if (!IndexCable((cable.Owner, cable.Comp))) { cable.Comp.PortB = null; cable.Comp.DeviceBId = string.Empty; return false; }
        RegisterCable((cable.Owner, cable.Comp));
        RoutingChanged?.Invoke();
        return true;
    }
    public bool TryDisconnectCable(Entity<NCMetaNetworkCableComponent?> cable, EntityUid user)
    {
        if (!Resolve(cable.Owner, ref cable.Comp) || !_interaction.InRangeUnobstructed(user, cable.Owner, range: 2)) return false;
        UnregisterCable((cable.Owner, cable.Comp)); UnindexCable((cable.Owner, cable.Comp)); cable.Comp.PortA = cable.Comp.PortB = null; cable.Comp.DeviceAId = cable.Comp.DeviceBId = string.Empty; RoutingChanged?.Invoke(); return true;
    }
    private bool IndexCable(Entity<NCMetaNetworkCableComponent> cable)
    {
        if (cable.Comp.PortA is not { } a || cable.Comp.PortB is not { } b || a == b || _cablesByPort.TryGetValue(a, out var left) && left.Count >= 16 && !left.Contains(cable.Owner) || _cablesByPort.TryGetValue(b, out var right) && right.Count >= 16 && !right.Contains(cable.Owner)) return false;
        foreach (var port in new[] { a, b })
        {
            if (!_cablesByPort.TryGetValue(port, out var cables)) _cablesByPort.Add(port, cables = []);
            if (cables.Count < 16) cables.Add(cable.Owner);
        }
        return true;
    }
    private void UnindexCable(Entity<NCMetaNetworkCableComponent> cable)
    {
        if (cable.Comp.PortA is { } a && _cablesByPort.TryGetValue(a, out var left)) { left.Remove(cable.Owner); if (left.Count == 0) _cablesByPort.Remove(a); }
        if (cable.Comp.PortB is { } b && _cablesByPort.TryGetValue(b, out var right)) { right.Remove(cable.Owner); if (right.Count == 0) _cablesByPort.Remove(b); }
    }
    private bool CanWire(EntityUid a, EntityUid b, float length) => Exists(a) && Exists(b) && float.IsFinite(length) && length is > 0 and <= 32 && _interaction.InRangeUnobstructed(a, b, range: length);
    private bool Online(EntityUid uid) => _devices.IsPowered(uid) && (!TryComp<NCMetaHostComponent>(uid, out var host) || !host.Disabled);
    private bool AllowedSegment(EntityUid a, EntityUid b)
    {
        if (!TryComp<NCMetaPortComponent>(a, out var from) || !TryComp<NCMetaPortComponent>(b, out var to) || from.NetworkId.Length == 0 || to.NetworkId.Length == 0) return false;
        return from.NetworkId == to.NetworkId || TryComp<NCMetaMessagePortComponent>(a, out var gateway) && gateway.GatewayNetworks.Contains(to.NetworkId, StringComparer.Ordinal) || TryComp<NCMetaMessagePortComponent>(b, out var receiverGateway) && receiverGateway.GatewayNetworks.Contains(from.NetworkId, StringComparer.Ordinal);
    }
    public bool CanRoute(EntityUid from, EntityUid to)
    {
        if (from == to) return Online(from);
        if (!Online(from) || !Online(to) || !TryComp<NCMetaPortComponent>(from, out var source) || !TryComp<NCMetaPortComponent>(to, out var destination)) return false;
        if (AllowedSegment(from, to) && CanRadio(from, to, source, destination)) return true;
        var visited = new HashSet<EntityUid> { from }; var queue = new Queue<EntityUid>(); queue.Enqueue(from);
        while (queue.TryDequeue(out var current) && visited.Count <= 64)
        {
            if (!_cablesByPort.TryGetValue(current, out var cables)) continue;
            foreach (var cableId in cables)
            {
                if (!TryComp<NCMetaNetworkCableComponent>(cableId, out var cable) || cable.PortA is not { } a || cable.PortB is not { } b || !CanWire(a, b, cable.MaximumLength)) continue;
                var next = a == current ? b : a;
                if (!Online(next) || !AllowedSegment(current, next) || !visited.Add(next)) continue;
                if (next == to) return true;
                if (visited.Count < 64) queue.Enqueue(next);
            }
        }
        return false;
    }
    public MetaValue Read(NCMetaDeviceInvocation invocation) => Error(invocation, "Unsupported");
    private static MetaValue Error(NCMetaDeviceInvocation invocation, string code) => invocation.Runtime.Process.CreateErrorResult(invocation.Runtime.Process.GetImport(invocation.Request.ImportId).ReturnType!.Value, code);
    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        MetaHostResult Fail(string code) => MetaHostResult.Completed(Error(invocation, code));
        if (!_runtime.TryCaptureOutboundAuthority(invocation, out var captured)) return Fail("PermissionRevoked");
        var authorization = new GuestOutboundAuthorization(_runtime, captured);
        if (!TryComp<NCMetaMessagePortComponent>(invocation.Device, out var port) || !_states.TryGetValue(invocation.Device, out var sourceState)) return Fail("Disconnected");
        var address = process.GetString(invocation.Request.Arguments[0]); var topic = process.GetString(invocation.Request.Arguments[1]); var payload = process.GetString(invocation.Request.Arguments[2]);
        var bytes = Encoding.UTF8.GetByteCount(payload);
        if ((!ValidAddress(address) && address != "*") || Encoding.UTF8.GetByteCount(topic) > 48 || topic.Length == 0 || bytes > 2048 || !TryComp<NCMetaPortComponent>(invocation.Device, out var physical) || bytes > physical.PayloadBytes || port.AllowedTopics.Count > 0 && !port.AllowedTopics.Contains(topic, StringComparer.Ordinal)) return Fail("InvalidArgument");
        if (!_ownerRates.TryGetValue(invocation.Runtime.Owner, out var owner))
        {
            if (_ownerRates.Count >= 4096) return Fail("QuotaExceeded");
            _ownerRates.Add(invocation.Runtime.Owner, owner = new MetaRateLimit(20, 10));
        }
        if (!sourceState.Messages.TryConsume(_now) || !owner.TryConsume(_now)) return Fail("RateLimited");
        EntityUid[] recipients;
        if (address == "*")
        {
            if (!port.AllowBroadcast) return Fail("AccessDenied");
            if (!_segments.TryGetValue(physical.NetworkId, out var segment)) return Fail("Disconnected");
            recipients = segment.Where(uid => uid != invocation.Device && AllowedSegment(invocation.Device, uid)).Take(Math.Clamp(port.MaximumRecipients, 1, 16) + 1).ToArray();
            if (recipients.Length > Math.Clamp(port.MaximumRecipients, 1, 16)) return Fail("QuotaExceeded");
        }
        else if (_addresses.TryGetValue(address, out var receiver)) recipients = [receiver];
        else return Fail("NotFound");
        if (recipients.Length == 0 || _envelopes.Count + recipients.Length > 256 || !_routeWork.TryConsume(_now, recipients.Length) || !_bytes.TryConsume(_now, Math.Max(1, (bytes + 128) * recipients.Length))) return Fail("RateLimited");
        if (!_devices.TryGetEntry(invocation.Device, out var senderDevice)) return Fail("Disconnected");
        foreach (var receiver in recipients)
        {
            if (!TryComp<NCMetaMessagePortComponent>(receiver, out var target) || !_states.TryGetValue(receiver, out var state) || !_devices.TryGetEntry(receiver, out _) || !CanRoute(invocation.Device, receiver) || port.AllowedPeers.Count > 0 && !port.AllowedPeers.Contains(target.Address, StringComparer.Ordinal) || target.AllowedPeers.Count > 0 && !target.AllowedPeers.Contains(port.Address, StringComparer.Ordinal) || target.AllowedTopics.Count > 0 && !target.AllowedTopics.Contains(topic, StringComparer.Ordinal)) return Fail("AccessDenied");
            if (state.Incoming >= Math.Min(16, target.QueueMessages) || state.IncomingBytes + bytes + 128 > Math.Min(32768, target.QueueBytes)) return Fail("Busy");
        }
        if (sourceState.Outgoing + recipients.Length > 16 || sourceState.OutgoingBytes + (bytes + 128) * recipients.Length > 32768) return Fail("Busy");
        var contextId = process.CurrentEventContextToken;
        Chain chain;
        if (contextId == 0)
        {
            if (_chains.Count >= 256 || _nextContext == uint.MaxValue) return Fail("QuotaExceeded");
            contextId = 0x4d45544100000000UL | ++_nextContext;
            chain = new Chain { Expires = _now + 5000, OriginAuthority = captured.Actor };
            _chains.Add(contextId, chain); _chainExpirations.Enqueue(contextId, chain.Expires);
        }
        else if (!_chains.TryGetValue(contextId, out chain!) || chain.Expires <= _now) return Fail("RateLimited");
        if (!NCMetaRuntimeSystem.IsDeferredContextCurrent(chain.OriginAuthority)) return Fail("PermissionRevoked");
        if (chain.Sends <= 0 || chain.Copies < recipients.Length) return Fail("RateLimited");
        chain.Sends--; chain.Copies -= recipients.Length;
        var receipt = Guid.NewGuid();
        foreach (var receiver in recipients)
        {
            _devices.TryGetEntry(receiver, out var receiverDevice);
            var id = Guid.NewGuid(); var envelope = new Envelope(id, invocation.Device, receiver, port.Address, topic, payload, senderDevice.DeviceEpoch, receiverDevice!.DeviceEpoch, senderDevice.AuthorityEpoch, receiverDevice.AuthorityEpoch, contextId, bytes + 128, _now + 1000, authorization);
            _envelopes.Add(id, envelope); chain.References++;
            TrackOutbound(envelope);
            sourceState.Outgoing++; sourceState.OutgoingBytes += envelope.Bytes;
            var targetState = _states[receiver]; targetState.Incoming++; targetState.IncomingBytes += envelope.Bytes;
            _deliveries.Enqueue(id, _now + Math.Clamp(port.LatencyMilliseconds, 1, 500)); _expirations.Enqueue(id, envelope.Expires);
        }
        var receiptType = process.GetTypeId("meta.net.DeliveryReceipt");
        return MetaHostResult.Completed(process.CreateResult(process.GetImport(invocation.Request.ImportId).ReturnType!.Value, process.CreateRecord(receiptType, [process.CreateString(receipt.ToString("D")), MetaValue.Bool(true)])));
    }
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        using var elapsed = MetaMetricScope.Measure(_runtime.Metrics, MetaMetric.NativeNetworkElapsed, _runtime.MetricTick);
        if (!_persistence.TryCaptureTime(out var epoch, out var time)) return;
        if (_hasEpoch && _epoch != epoch)
        {
            foreach (var id in _envelopes.Keys.ToArray()) Drop(id);
            _deliveries.Clear(); _expirations.Clear(); _chains.Clear(); _chainExpirations.Clear(); _ownerRates.Clear();
            _nextRadioDraw = 0;
        }
        _epoch = epoch; _hasEpoch = true;
        _now = time.Ticks / TimeSpan.TicksPerMillisecond;
        UpdateRadio();
        for (var work = 0; work < 32 && _expirations.TryPeek(out _, out var deadline) && deadline <= _now; work++) Drop(_expirations.Dequeue());
        for (var work = 0; work < 32 && _chainExpirations.TryPeek(out _, out var deadline) && deadline <= _now; work++) _chains.Remove(_chainExpirations.Dequeue());
        for (var work = 0; work < 32 && _deliveries.TryPeek(out _, out var due) && due <= _now; work++)
        {
            var id = _deliveries.Dequeue();
            if (!_envelopes.TryGetValue(id, out var envelope)) continue;
            if (!Current(envelope) || !TryComp<DeviceNetworkComponent>(envelope.Sender, out var source) || !TryComp<DeviceNetworkComponent>(envelope.Receiver, out var target) || target.ReceiveFrequency == null || !_network.QueuePacket(envelope.Sender, target.Address, new NetworkPayload { ["nc-meta-envelope"] = id.ToString("D") }, target.ReceiveFrequency, target.DeviceNetId, source)) Drop(id);
            else TransmissionAdmitted?.Invoke(envelope.Sender, envelope.Bytes);
        }
    }
    private bool Current(Envelope e) => e.Expires > _now && IsOutboundCurrent(e) &&
        _devices.TryGetEntry(e.Sender, out var source) && _devices.TryGetEntry(e.Receiver, out var target) &&
        source.DeviceEpoch == e.SenderEpoch && source.AuthorityEpoch == e.AuthorityEpoch &&
        target.DeviceEpoch == e.ReceiverEpoch && target.AuthorityEpoch == e.ReceiverAuthorityEpoch && CanRoute(e.Sender, e.Receiver);
    private void OnPacket(Entity<NCMetaMessagePortComponent> ent, ref DeviceNetworkPacketEvent args)
    {
        if (!args.Data.TryGetValue<string>("nc-meta-envelope", out var text) || !Guid.TryParseExact(text, "D", out var id) || !_envelopes.TryGetValue(id, out var envelope) || envelope.Receiver != ent.Owner || envelope.Sender != args.Sender) return;
        if (!Current(envelope)) { Drop(id); return; }
        MetaMetrics.Record(_runtime.Metrics, MetaMetric.NativeDeliveredBytes, envelope.Bytes, _runtime.MetricTick);
        var accepted = _runtime.TryPublishEvent(ent.Owner, "meta.net.MessagePort.messageReceived", process =>
        {
            if (_subscribed.Add(process)) process.EventContextReleased += ReleaseContext;
            return process.CreateRecord(process.GetTypeId("meta.net.Message"), [process.CreateString(envelope.SenderAddress), process.CreateString(envelope.Topic), process.CreateString(envelope.Payload), _runtime.CreateStamp(process)]);
        }, contextToken: envelope.Context);
        if (_chains.TryGetValue(envelope.Context, out var chain)) chain.References += accepted;
        Drop(id);
    }
    private void ReleaseContext(ulong id)
    {
        if (_chains.TryGetValue(id, out var chain) && --chain.References <= 0) _chains.Remove(id);
    }
    private void Drop(Guid id)
    {
        if (!_envelopes.Remove(id, out var envelope)) return;
        ReleaseOutbound(envelope);
        if (envelope.Expires <= _now) MetaMetrics.Record(_runtime.Metrics, MetaMetric.NativeExpiredBytes, envelope.Bytes, _runtime.MetricTick);
        if (_states.TryGetValue(envelope.Sender, out var from)) { from.Outgoing--; from.OutgoingBytes -= envelope.Bytes; }
        if (_states.TryGetValue(envelope.Receiver, out var to)) { to.Incoming--; to.IncomingBytes -= envelope.Bytes; }
        ReleaseContext(envelope.Context);
    }
    private static bool ValidAddress(string value) => value.Length is > 0 and <= 96 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is ':' or '.' or '_' or '-');
}
