// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Concurrent;
using System.Text;
using Content.Meta.Hosting;
using Content.Meta.Runtime;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Server._NC.Medical.Cyberware;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Network;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.Damage.Systems;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Interaction;
using Content.Shared.Power.EntitySystems;
using Content.Shared.PowerCell;
using Content.Shared.PowerCell.Components;
using Content.Shared.UserInterface;
using Content.Shared.Verbs;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Meta.Network;

/// <summary>Native physical NET sessions and deterministic tool/defense resolution. Guest execution never owns threat timers.</summary>
public sealed partial class NCMetaNetrunSystem : EntitySystem, INCMetaDeviceAdapter
{
    private sealed class Session
    {
        public required Guid Id;
        public required EntityUid Deck;
        public required EntityUid Actor;
        public required EntityUid AccessPoint;
        public required string Architecture;
        public required string Owner;
        public required string ActorPrincipal;
        public required Content.Server._NC.Meta.Authority.NCMetaManagementProof DeckManagement;
        public Content.Server._NC.Meta.Authority.NCMetaManagementProof? AccessManagement;
        public required string DeckId;
        public required ulong DeckEpoch;
        public required ulong DeckAuthority;
        public required ulong AccessEpoch;
        public required ulong AccessAuthority;
        public required SecurityPolicy Security;
        public long Revision = 1;
        public int Trace;
        public int Spent;
        public readonly HashSet<string> Known = new(StringComparer.Ordinal);
        public readonly HashSet<string> Probed = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Grants = new(StringComparer.Ordinal);
        public readonly Dictionary<Guid, Dictionary<string, (ulong Token, MetaValue Value)>> References = [];
        public readonly Dictionary<Guid, MetaProcess> ReferenceProcesses = [];
        public readonly MetaRateLimit Observe = new(1, 1);
        public Pending? Pending;
        public string Status = "nc-meta-net-attached";
        public string Preview = string.Empty;
        public bool Ice;
        public bool BlackIce;
        public long LastThreat;
        public NCMetaNetDefenseProfilePrototype? Threat;
    }
    private sealed record Pending(Guid Id, NCMetaNetOperation Operation, string Node, ulong NodeGeneration,
        long Due, NCMetaRuntimeInstance? Runtime, long OperationId, int ReturnType, int IoState = 0, bool AuditReserved = false);
    private sealed record NodeEntry(EntityUid Entity, NCMetaNetNodeComponent Component)
    {
        public ulong Generation = 1;
    }
    private sealed record NodeReference(Guid Session, Guid Runtime, string Node, ulong Generation);
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaNetworkSystem _network = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedDoorSystem _doors = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private PowerCellSystem _cells = default!;
    private readonly Dictionary<Guid, Session> _sessions = [];
    private readonly Dictionary<EntityUid, Guid> _deckSessions = [];
    private readonly Dictionary<(string Architecture, string Node), NodeEntry> _nodes = [];
    private readonly Dictionary<string, HashSet<(string Architecture, string Node)>> _controlledNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, NodeReference> _references = [];
    private readonly Dictionary<string, Guid> _actorActions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MetaRateLimit> _budgets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MetaRateLimit> _actorBudgets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MetaRateLimit> _deckBudgets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _actorCooldowns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _cooldowns = new(StringComparer.Ordinal);
    private readonly PriorityQueue<(Guid Session, Guid Action), long> _actions = new();
    private readonly PriorityQueue<Guid, long> _threats = new();
    private readonly MetaRateLimit _requests = new(128, 64);
    private readonly ConcurrentQueue<Action> _completed = new();
    private ulong _nextReference;
    private long _now;
    private Content.Shared._NC.Persistence.Time.NCSessionEpoch _epoch;
    private bool _hasEpoch;
    private int _pendingFileOperations;
    public IEnumerable<string> Members => ["meta.netrun.NetSession.listVisibleNodes", "meta.netrun.NetSession.probe"];
    public bool CanBind(EntityUid device, string capability) => capability == "meta.netrun.NetSession" && HasComp<NCMetaCyberdeckComponent>(device) && HasComp<NCMetaPortComponent>(device);

    public override void Initialize()
    {
        base.Initialize();
        _runtime.RegisterAdapter(this); _runtime.RuntimeStopped += OnRuntimeStopped; _devices.Changed += OnDeviceChanged;
        SubscribeLocalEvent<NCMetaNetNodeComponent, MapInitEvent>(OnNodeInit, after: [typeof(NCMetaDeviceRegistrySystem)]);
        SubscribeLocalEvent<NCMetaNetNodeComponent, ComponentShutdown>(OnNodeShutdown);
        SubscribeLocalEvent<NCMetaNetNodeComponent, GetVerbsEvent<AlternativeVerb>>(OnNodeVerbs);
        SubscribeLocalEvent<NCMetaNetAccessPointComponent, GetVerbsEvent<AlternativeVerb>>(OnAccessPointVerbs);
        SubscribeLocalEvent<NCMetaCyberdeckComponent, ComponentShutdown>(OnDeckShutdown);
        SubscribeLocalEvent<NCMetaCyberdeckComponent, AfterInteractEvent>(OnDeckInteract);
        SubscribeLocalEvent<NCMetaCyberdeckComponent, GetVerbsEvent<AlternativeVerb>>(OnDeckVerbs);
        SubscribeLocalEvent<NCMetaCyberdeckComponent, BoundUIOpenedEvent>(OnOpened);
        SubscribeLocalEvent<NCMetaCyberdeckComponent, NCMetaNetRequest>(OnRequest);
    }
    public override void Shutdown()
    {
        _runtime.RuntimeStopped -= OnRuntimeStopped; _devices.Changed -= OnDeviceChanged;
        QuiesceSessions();
        base.Shutdown();
    }
    private void OnNodeInit(Entity<NCMetaNetNodeComponent> ent, ref MapInitEvent args)
    {
        if (_devices.TryGetEntry(ent.Owner, out var device))
        {
            if (ent.Comp.NodeId.Length == 0) ent.Comp.NodeId = device.Id;
            if (ent.Comp.Kind == NCMetaNetNodeKind.Data && ent.Comp.ControlledDeviceId.Length == 0) ent.Comp.ControlledDeviceId = device.Id;
        }
        if (ent.Comp.ArchitectureId.Length is > 0 and <= 128 && ent.Comp.NodeId.Length is > 0 and <= 128 && ent.Comp.Neighbors.Count <= 64 && _nodes.Count < 4096)
        {
            var key = (ent.Comp.ArchitectureId, ent.Comp.NodeId);
            if (_nodes.TryAdd(key, new NodeEntry(ent.Owner, ent.Comp))) AddControlledNode(ent.Comp.ControlledDeviceId, key);
        }
    }
    private void OnNodeShutdown(Entity<NCMetaNetNodeComponent> ent, ref ComponentShutdown args)
    {
        var key = (ent.Comp.ArchitectureId, ent.Comp.NodeId);
        if (_nodes.TryGetValue(key, out var entry) && entry.Entity == ent.Owner)
        {
            InvalidateNode(key, entry); RemoveControlledNode(ent.Comp.ControlledDeviceId, key); _nodes.Remove(key);
        }
    }
    private void OnDeviceChanged(NCMetaDeviceEntry device)
    {
        var affected = _controlledNodes.TryGetValue(device.Id, out var controlled) ? new HashSet<(string Architecture, string Node)>(controlled) : [];
        if (TryComp<NCMetaNetNodeComponent>(device.Entity, out var node)) affected.Add((node.ArchitectureId, node.NodeId));
        foreach (var key in affected) if (_nodes.TryGetValue(key, out var entry)) InvalidateNode(key, entry);
        foreach (var session in _sessions.Values.Where(session => session.Deck == device.Entity || session.AccessPoint == device.Entity).ToArray())
            if (!Current(session)) Disconnect(session, "nc-meta-net-connection-lost");
    }

    private void AddControlledNode(string target, (string Architecture, string Node) key)
    {
        if (target.Length == 0) return;
        if (!_controlledNodes.TryGetValue(target, out var nodes)) _controlledNodes.Add(target, nodes = []);
        nodes.Add(key);
    }
    private void RemoveControlledNode(string target, (string Architecture, string Node) key)
    {
        if (!_controlledNodes.TryGetValue(target, out var nodes)) return;
        nodes.Remove(key); if (nodes.Count == 0) _controlledNodes.Remove(target);
    }
    private void InvalidateNode((string Architecture, string Node) key, NodeEntry node)
    {
        node.Generation = node.Generation is 0 or ulong.MaxValue ? 0 : node.Generation + 1;
        foreach (var session in _sessions.Values.Where(session => session.Architecture == key.Architecture).ToArray())
        {
            session.Probed.Remove(key.Node); session.Grants.Remove(key.Node);
            foreach (var pair in session.References)
                if (pair.Value.Remove(key.Node, out var reference)) { _references.Remove(reference.Token); session.ReferenceProcesses[pair.Key].TryReleaseOpaque(reference.Value); }
            if (session.Pending is { } pending && pending.Node == key.Node) CompleteError(session, pending, "PermissionRevoked");
            session.Revision++; Publish(session.Deck, session.Actor);
        }
    }
    private void OnNodeVerbs(Entity<NCMetaNetNodeComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || ent.Comp.Kind is not (NCMetaNetNodeKind.Control or NCMetaNetNodeKind.Data) || !_devices.CanManage(ent.Owner, args.User)) return;
        var actor = args.User;
        var software = ent.Comp.Kind == NCMetaNetNodeKind.Data;
        foreach (var target in _network.GetReachablePorts(ent.Owner).Where(target => software ? HasComp<NCMetaHostComponent>(target) : HasComp<DoorComponent>(target)).Take(16))
        {
            var captured = target;
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString(software ? "nc-meta-net-bind-software" : "nc-meta-net-bind-control", ("device", Name(target))),
                Act = () => { if (software) TryBindSoftwareNode(ent.Owner, captured, actor); else TryBindControlNode(ent.Owner, captured, actor); } });
        }
    }
    public bool TryBindControlNode(EntityUid node, EntityUid target, EntityUid actor)
    {
        if (!_devices.CanManage(node, actor) || !_devices.CanManage(target, actor, requireRange: false) || !TryComp<NCMetaNetNodeComponent>(node, out var component) || component.Kind != NCMetaNetNodeKind.Control || !HasComp<DoorComponent>(target) || !TryComp<NCMetaDeviceComponent>(target, out var capability) || !capability.AllowServiceControl || !_network.CanRoute(node, target) || !_devices.TryGetEntry(target, out var device)) return false;
        var key = (component.ArchitectureId, component.NodeId);
        if (!_nodes.TryGetValue(key, out var entry) || entry.Entity != node) return false;
        return TrySaveControlBinding(entry, device, actor);
    }
    private void OnDeckShutdown(Entity<NCMetaCyberdeckComponent> ent, ref ComponentShutdown args)
    {
        _connecting.Remove(ent.Owner);
        if (_deckSessions.TryGetValue(ent.Owner, out var id) && _sessions.TryGetValue(id, out var session)) Disconnect(session, "nc-meta-net-disconnected");
    }
    private void OnRuntimeStopped(NCMetaRuntimeInstance runtime)
    {
        foreach (var session in _sessions.Values)
        {
            if (!session.References.Remove(runtime.Generation, out var refs)) continue;
            session.ReferenceProcesses.Remove(runtime.Generation);
            foreach (var reference in refs.Values) { _references.Remove(reference.Token); runtime.Process.TryReleaseOpaque(reference.Value); }
        }
        // The physical session and native ICE survive the loss or pause of helper software.
    }
    private void OnDeckInteract(Entity<NCMetaCyberdeckComponent> ent, ref AfterInteractEvent args)
    {
        if (args.Handled || args.Target is not { } target || !HasComp<NCMetaNetAccessPointComponent>(target)) return;
        args.Handled = TryConnect(ent.Owner, args.User, target);
        if (args.Handled) _ui.TryOpenUi(ent.Owner, NCMetaNetUiKey.Console, args.User);
    }
    private void OnDeckVerbs(Entity<NCMetaCyberdeckComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract) return;
        var actor = args.User;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-net-console"), Act = () => { if (_devices.CanManage(ent.Owner, actor)) _ui.TryOpenUi(ent.Owner, NCMetaNetUiKey.Console, actor); } });
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-net-emergency-exit"), Act = () => TryDisconnect(ent.Owner, actor) });
    }
    private void OnOpened(Entity<NCMetaCyberdeckComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (args.Actor is { } actor) Publish(ent.Owner, actor);
    }
    public bool TryConnect(EntityUid deck, EntityUid actor, EntityUid accessPoint)
    {
        if (!_nativeAccepting || !_persistence.TryCaptureTime(out var epoch, out var time)) return false;
        EnsurePolicyBoundary(epoch);
        _now = time.Ticks / TimeSpan.TicksPerMillisecond;
        if (!_requests.TryConsume(_now) || !_devices.CanManage(deck, actor) || !TryComp<NCMetaCyberdeckComponent>(deck, out _) || !TryComp<NCMetaNetAccessPointComponent>(accessPoint, out var point) || point.ArchitectureId.Length == 0 || point.EntryNodes.Count > 64 || !point.PublicEntry && !_devices.CanManage(accessPoint, actor, requireRange: false) || !_network.CanRoute(deck, accessPoint) || !_devices.TryGetEntry(deck, out var deckDevice) || !_devices.TryGetEntry(accessPoint, out var accessDevice)) return false;
        if (!_configurationAuthority.TryGetManagementProof(actor, deckDevice.Owner, out var management)) return false;
        Content.Server._NC.Meta.Authority.NCMetaManagementProof? accessManagement = null;
        if (!point.PublicEntry)
        {
            if (!_configurationAuthority.TryGetManagementProof(actor, accessDevice.Owner, out var accessProof)) return false;
            accessManagement = accessProof;
        }
        if (!TryGetPolicy(accessDevice, point, out var policy)) return TryQueueConnection(deck, actor, accessDevice, management, accessManagement);
        if (_deckSessions.TryGetValue(deck, out var old) && _sessions.TryGetValue(old, out var existing)) Disconnect(existing, "nc-meta-net-disconnected");
        if (_sessions.Count >= 64 || !_parties.TryResolve(actor, out var actorParty)) return false;
        var session = new Session { Id = Guid.NewGuid(), Deck = deck, Actor = actor, AccessPoint = accessPoint, Architecture = point.ArchitectureId, Owner = deckDevice.Owner, ActorPrincipal = actorParty.ToString(), DeckManagement = management, AccessManagement = accessManagement, DeckId = deckDevice.Id, DeckEpoch = deckDevice.DeviceEpoch, DeckAuthority = deckDevice.AuthorityEpoch, AccessEpoch = accessDevice.DeviceEpoch, AccessAuthority = accessDevice.AuthorityEpoch, LastThreat = _now, Security = policy };
        foreach (var entry in point.EntryNodes) if (_nodes.ContainsKey((session.Architecture, entry))) session.Known.Add(entry);
        _sessions.Add(session.Id, session); _deckSessions.Add(deck, session.Id); _threats.Enqueue(session.Id, _now + 1000);
        Publish(deck, actor);
        Log.Info($"NET session {session.Id} attached by {ToPrettyString(actor)} through {accessDevice.Id}.");
        return true;
    }
    public bool TryDisconnect(EntityUid deck, EntityUid actor)
    {
        if (_connecting.TryGetValue(deck, out var connection) && connection.Actor == actor && _interaction.InRangeUnobstructed(actor, deck, range: 2))
        { _connecting.Remove(deck); Publish(deck, actor); return true; }
        if (!_interaction.InRangeUnobstructed(actor, deck, range: 2) || !_deckSessions.TryGetValue(deck, out var id) || !_sessions.TryGetValue(id, out var session) || session.Actor != actor && !_devices.CanManage(deck, actor)) return false;
        Disconnect(session, "nc-meta-net-disconnected"); return true;
    }
    private bool Current(Session session) => _nativeAccepting && Exists(session.Actor) && _parties.TryResolve(session.Actor, out var party) && party.ToString() == session.ActorPrincipal && CanUseSessionDeck(session) && _devices.TryGetEntry(session.Deck, out var deck) && deck.Id == session.DeckId && deck.Owner == session.Owner && deck.DeviceEpoch == session.DeckEpoch && deck.AuthorityEpoch == session.DeckAuthority && _devices.TryGetEntry(session.AccessPoint, out var access) && access.Owner == session.Security.Owner && access.DeviceEpoch == session.AccessEpoch && access.AuthorityEpoch == session.AccessAuthority && _policies.TryGetValue(access.Id, out var policy) && policy.Policy?.Revision == session.Security.Revision && !policy.Writing && _network.CanRoute(session.Deck, session.AccessPoint);
    private bool TryNode(Session session, string id, out NodeEntry entry)
    {
        entry = default!;
        return session.Known.Contains(id) && _nodes.TryGetValue((session.Architecture, id), out entry!) && entry.Generation != 0 && Exists(entry.Entity) && PolicyAllows(session, entry) && _power.IsPowered(entry.Entity) && (!HasComp<NCMetaPortComponent>(entry.Entity) || _network.CanRoute(session.AccessPoint, entry.Entity));
    }
    private void Discover(Session session)
    {
        foreach (var known in session.Known.ToArray())
        {
            if (!TryNode(session, known, out var node) || !session.Probed.Contains(known) && _prototypes.Index(node.Component.Defense).Rating != 0) continue;
            foreach (var neighbor in node.Component.Neighbors)
            {
                if (session.Known.Count >= 64) break;
                if (_nodes.ContainsKey((session.Architecture, neighbor))) session.Known.Add(neighbor);
            }
        }
    }
    public MetaValue Read(NCMetaDeviceInvocation invocation) => Failure(invocation, "Unsupported");
    private static MetaValue Failure(NCMetaDeviceInvocation invocation, string code) => invocation.Runtime.Process.CreateErrorResult(invocation.Runtime.Process.GetImport(invocation.Request.ImportId).ReturnType!.Value, code);
    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        MetaHostResult Error(string code) => MetaHostResult.Completed(Failure(invocation, code));
        if (!_deckSessions.TryGetValue(invocation.Device, out var id) || !_sessions.TryGetValue(id, out var session) || session.Owner != invocation.Runtime.Owner || !Current(session)) return Error("Disconnected");
        var op = invocation.Member.Name == "listVisibleNodes" ? NCMetaNetOperation.Scan : NCMetaNetOperation.Probe;
        var nodeId = string.Empty;
        if (op == NCMetaNetOperation.Probe)
        {
            if (!invocation.Runtime.Process.TryGetOpaqueToken(invocation.Request.Arguments[0], out var token) || !_references.TryGetValue(token, out var reference) || reference.Session != session.Id || reference.Runtime != invocation.Runtime.Generation || !TryNode(session, reference.Node, out var node) || reference.Generation != node.Generation) return Error("PermissionRevoked");
            nodeId = reference.Node;
        }
        if (!TryBegin(session, op, nodeId, invocation.Runtime, invocation.OperationId, invocation.Runtime.Process.GetImport(invocation.Request.ImportId).ReturnType!.Value, out var code)) return Error(code);
        return MetaHostResult.Awaiting(invocation.OperationId);
    }
    private bool TryBegin(Session session, NCMetaNetOperation operation, string nodeId, NCMetaRuntimeInstance? runtime, long operationId, int returnType, out string error)
    {
        error = "Busy";
        if (operation is not (NCMetaNetOperation.Scan or NCMetaNetOperation.Probe or NCMetaNetOperation.Read or NCMetaNetOperation.CopySoftware or NCMetaNetOperation.AcquireControl or NCMetaNetOperation.Defend or NCMetaNetOperation.Activate or NCMetaNetOperation.Deactivate))
        { error = "Unsupported"; return false; }
        if (!Current(session) || !TryComp<NCMetaCyberdeckComponent>(session.Deck, out var deck)) { error = "Disconnected"; return false; }
        if (session.Pending != null || _actorActions.ContainsKey(session.ActorPrincipal) ||
            _actorCooldowns.GetValueOrDefault(session.ActorPrincipal) > _now || _cooldowns.GetValueOrDefault(session.DeckId) > _now) return false;
        if (operation == NCMetaNetOperation.Scan && !session.Observe.TryConsume(_now)) { error = "RateLimited"; return false; }
        if (session.Ice && operation != NCMetaNetOperation.Defend) { error = "Interlock"; return false; }
        var tool = _prototypes.Index(deck.ToolProfile);
        NodeEntry? node = null;
        if (operation != NCMetaNetOperation.Scan && !TryNode(session, nodeId, out node!)) { error = "NotFound"; return false; }
        if (node != null && operation is NCMetaNetOperation.Read or NCMetaNetOperation.CopySoftware or NCMetaNetOperation.Activate or NCMetaNetOperation.Deactivate && session.Grants.GetValueOrDefault(nodeId) <= _now) { error = "AccessDenied"; return false; }
        if (node != null && (operation is NCMetaNetOperation.Read or NCMetaNetOperation.CopySoftware && node.Component.Kind != NCMetaNetNodeKind.Data || operation is NCMetaNetOperation.AcquireControl or NCMetaNetOperation.Activate or NCMetaNetOperation.Deactivate && node.Component.Kind != NCMetaNetNodeKind.Control || operation == NCMetaNetOperation.AcquireControl && !session.Probed.Contains(nodeId) || operation == NCMetaNetOperation.Defend && node.Component.Kind != NCMetaNetNodeKind.Defense)) { error = "Incompatible"; return false; }
        if (node != null && !tool.CompatibleKinds.Contains(node.Component.Kind)) { error = "Incompatible"; return false; }
        if (!TryGetActionBudget(_budgets, session.Owner, out var budget) ||
            !TryGetActionBudget(_actorBudgets, session.ActorPrincipal, out var actorBudget) ||
            !TryGetActionBudget(_deckBudgets, session.DeckId, out var deckBudget))
        { error = "QuotaExceeded"; return false; }
        var cost = Math.Clamp(tool.Cost, 1, 10);
        if (session.Spent + cost > Math.Clamp(tool.Budget, 1, 100) ||
            !MetaRateLimit.TryConsumeAll(_now, cost, [budget, actorBudget, deckBudget])) { error = "RateLimited"; return false; }
        var duration = Math.Clamp(tool.DurationMilliseconds, 100, 4000);
        var pending = new Pending(Guid.NewGuid(), operation, nodeId, node?.Generation ?? 0, _now + 1000, runtime, operationId, returnType, IoState: 3, AuditReserved: true);
        if (!TryBeginAudit(session, pending, duration)) { error = "Busy"; return false; }
        session.Pending = pending; session.Spent += cost; session.Revision++;
        _actorActions[session.ActorPrincipal] = session.Id;
        var cooldown = _now + duration + 1000 + Math.Clamp(tool.CooldownMilliseconds, 0, 5000);
        _cooldowns[session.DeckId] = cooldown;
        _actorCooldowns[session.ActorPrincipal] = cooldown;
        _actions.Enqueue((session.Id, pending.Id), pending.Due); Publish(session.Deck, session.Actor);
        return true;
    }

    private static bool TryGetActionBudget(Dictionary<string, MetaRateLimit> budgets, string key, out MetaRateLimit budget)
    {
        if (budgets.TryGetValue(key, out budget!)) return true;
        if (budgets.Count >= 4096) return false;
        budgets.Add(key, budget = new MetaRateLimit(10, 1));
        return true;
    }
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_persistence.TryCaptureTime(out var epoch, out var time)) return;
        if (_hasEpoch && _epoch != epoch)
        {
            foreach (var session in _sessions.Values.ToArray()) Disconnect(session, "nc-meta-net-disconnected");
            _actions.Clear(); _threats.Clear(); _budgets.Clear(); _actorBudgets.Clear(); _deckBudgets.Clear();
            _actorCooldowns.Clear(); _cooldowns.Clear();
            _connecting.Clear(); _connectionTimeouts.Clear();
        }
        _epoch = epoch; _hasEpoch = true;
        EnsurePolicyBoundary(epoch);
        _now = time.Ticks / TimeSpan.TicksPerMillisecond;
        CheckConnectionTimeouts();
        for (var count = 0; count < 32 && _completed.TryDequeue(out var completed); count++) completed();
        for (var count = 0; count < 32 && _actions.TryPeek(out _, out var due) && due <= _now; count++)
        {
            var work = _actions.Dequeue();
            if (_sessions.TryGetValue(work.Session, out var session) && session.Pending is { } pending && pending.Id == work.Action && pending.Due <= _now) Finish(session, pending);
        }
        for (var count = 0; count < 32 && _threats.TryPeek(out _, out var due) && due <= _now; count++)
        {
            var id = _threats.Dequeue();
            if (!_sessions.TryGetValue(id, out var session)) continue;
            if (!Current(session)) { Disconnect(session, "nc-meta-net-connection-lost"); continue; }
            var powerCost = Math.Clamp((_now - session.LastThreat) * 0.005f, 0, 100000);
            session.LastThreat = _now;
            if (HasComp<PowerCellSlotComponent>(session.Deck) && powerCost > 0 && !_cells.TryUseCharge(session.Deck, powerCost)) { Disconnect(session, "nc-meta-net-connection-lost"); continue; }
            if (session.Ice && session.Threat is { } threat)
            {
                session.Trace = Math.Min(100, session.Trace + 1);
                session.BlackIce = session.Trace >= threat.BlackIceThreshold;
                if (session.BlackIce) _damage.TryChangeDamage(session.Deck, threat.BlackIceDamage, origin: session.AccessPoint);
                if (session.BlackIce && TryComp<NCMetaCyberdeckComponent>(session.Deck, out var deck) && deck.NeuralInterface && HasNeuralConnection(session.Actor))
                    _damage.TryChangeDamage(session.Actor, threat.BlackIceDamage, origin: session.AccessPoint);
                session.Status = session.BlackIce ? "nc-meta-net-black-ice" : "nc-meta-net-ice"; session.Revision++; Publish(session.Deck, session.Actor);
            }
            _threats.Enqueue(id, _now + Math.Clamp(session.Threat?.ThreatIntervalMilliseconds ?? 1000, 250, 5000));
        }
    }
    private bool HasNeuralConnection(EntityUid actor)
    {
        foreach (var uid in _cyberware.GetInstalledModuleEntities(actor, 64))
            if (TryComp<NCCyberwareComponent>(uid, out var module) && module.Operational && module.PowerGranted && module.NeuralState is NCCyberwareNeuralState.Linked or NCCyberwareNeuralState.Degraded) return true;
        return false;
    }
    private void Finish(Session session, Pending pending)
    {
        if (!Current(session)) { Disconnect(session, "nc-meta-net-connection-lost"); return; }
        if (pending.IoState != 0) { CompleteError(session, pending, pending.IoState == 2 ? "OutcomeUnknown" : "Timeout"); return; }
        if (pending.Operation == NCMetaNetOperation.Scan)
        {
            Discover(session);
            if (pending.Runtime is { } runtime) _runtime.TryComplete(runtime, pending.OperationId, process => process.CreateResult(pending.ReturnType, BuildNodes(session, runtime)));
            CompleteAction(session, "nc-meta-net-scan-complete"); return;
        }
        if (!TryNode(session, pending.Node, out var node) || node.Generation != pending.NodeGeneration)
        {
            CompleteError(session, pending, "StaleState"); return;
        }
        var tool = _prototypes.Index(Comp<NCMetaCyberdeckComponent>(session.Deck).ToolProfile); var defense = _prototypes.Index(node.Component.Defense);
        var resolution = NCMetaNetActionResolver.Resolve(tool, defense, node.Component.Kind, session.Trace);
        if (!resolution.Admitted) { CompleteError(session, pending, "Incompatible"); return; }
        var success = resolution.Success;
        session.Trace = resolution.Trace;
        session.Threat = defense; session.Ice |= resolution.Ice; session.BlackIce |= resolution.BlackIce;
        if (success)
        {
            session.Probed.Add(pending.Node);
            if (node.Component.Kind != NCMetaNetNodeKind.Control || pending.Operation != NCMetaNetOperation.Probe) session.Grants[pending.Node] = _now + 30000;
            Discover(session);
        }
        var key = success ? "nc-meta-net-probe-success" : "nc-meta-net-probe-defended";
        if (pending.Runtime is { } guest) _runtime.TryComplete(guest, pending.OperationId, process => process.CreateResult(pending.ReturnType, process.CreateRecord(process.GetTypeId("meta.netrun.ProbeOutcome"), [MetaValue.Bool(success), MetaValue.Int(session.Trace), process.CreateString(Loc.GetString(key))])));
        if (success && pending.Operation is NCMetaNetOperation.Activate or NCMetaNetOperation.Deactivate) key = ApplyControl(session, node, pending.Operation == NCMetaNetOperation.Activate);
        if (success && pending.Operation == NCMetaNetOperation.Defend && _devices.CanManage(session.AccessPoint, session.Actor, requireRange: false))
        {
            foreach (var other in _sessions.Values.Where(value => value.Architecture == session.Architecture && value.Id != session.Id).ToArray()) Disconnect(other, "nc-meta-net-defended");
            session.Trace = 0; session.Ice = session.BlackIce = false;
        }
        if (success && pending.Operation == NCMetaNetOperation.Read)
        {
            if (!BeginRead(session, pending, node)) CompleteError(session, pending, "Unsupported");
            return;
        }
        if (success && pending.Operation == NCMetaNetOperation.CopySoftware)
        {
            if (!BeginCopySoftware(session, pending, node)) CompleteError(session, pending, "Unsupported");
            return;
        }
        CompleteAction(session, key);
        Log.Info($"NET action {pending.Id} actor {ToPrettyString(session.Actor)} node {pending.Node} success {success} trace {session.Trace}.");
    }
    private string ApplyControl(Session session, NodeEntry node, bool open)
    {
        // Explicit native NET service grant: successful profile resolution, exact target, lease and physical route.
        // Null user reaches the domain only after that authorization, and never disables mechanical door interlocks.
        if (node.Component.Kind != NCMetaNetNodeKind.Control || !session.Grants.TryGetValue(node.Component.NodeId, out var lease) || lease <= _now || !_devices.TryResolve(node.Component.ControlledDeviceId, out var target) || !_devices.TryGetEntry(session.AccessPoint, out var access) || target.Owner != access.Owner || !_network.CanRoute(session.AccessPoint, target.Entity) || !TryComp<NCMetaDeviceComponent>(target.Entity, out var device) || !device.AllowServiceControl || !TryComp<DoorComponent>(target.Entity, out var door)) return "nc-meta-net-control-rejected";
        var accepted = open ? _doors.TryOpen(target.Entity, door, user: null, quiet: true) : _doors.TryClose(target.Entity, door, user: null);
        return accepted ? "nc-meta-net-control-applied" : "nc-meta-net-control-rejected";
    }
    private MetaValue BuildNodes(Session session, NCMetaRuntimeInstance runtime)
    {
        var process = runtime.Process; var values = new List<MetaValue>();
        if (!session.References.TryGetValue(runtime.Generation, out var refs))
        {
            if (session.References.Count >= 8) throw new InvalidOperationException("NET runtime reference quota.");
            session.References.Add(runtime.Generation, refs = new Dictionary<string, (ulong, MetaValue)>(StringComparer.Ordinal));
            session.ReferenceProcesses.Add(runtime.Generation, process);
        }
        foreach (var id in session.Known.Take(64))
        {
            if (!TryNode(session, id, out var node)) continue;
            if (!refs.TryGetValue(id, out var reference) || !_references.TryGetValue(reference.Token, out var native) || native.Generation != node.Generation)
            {
                if (reference.Token != 0) { process.TryReleaseOpaque(reference.Value); _references.Remove(reference.Token); }
                if (_references.Count >= 4096 || _nextReference == ulong.MaxValue) break;
                var token = ++_nextReference; var value = process.CreateOpaque(process.GetTypeId("meta.netrun.NetNodeRef"), token);
                reference = (token, value); refs[id] = reference; _references.Add(token, new NodeReference(session.Id, runtime.Generation, id, node.Generation));
            }
            values.Add(process.CreateRecord(process.GetTypeId("meta.netrun.NetNode"), [reference.Value, process.CreateString(Truncate(Loc.GetString(node.Component.Label), 64)), process.CreateEnum(process.GetTypeId("meta.netrun.NetNodeKind"), (int) node.Component.Kind)]));
        }
        var arrayType = Enumerable.Range(0, runtime.Artifact.TypeCount).First(id => { var type = runtime.Artifact.GetType(id); return type.Kind == Content.Meta.Bytecode.MetaTypeKind.Array && type.Args.Length == 1 && type.Args[0] == process.GetTypeId("meta.netrun.NetNode"); });
        return process.CreateArray(arrayType, values);
    }
    private void CompleteError(Session session, Pending pending, string error)
    {
        if (pending.Runtime is { } runtime) _runtime.TryComplete(runtime, pending.OperationId, process => process.CreateErrorResult(pending.ReturnType, error));
        CompleteAction(session, "nc-meta-net-action-rejected");
    }
    private void CompleteAction(Session session, string status)
    {
        if (session.Pending is { } completed) RecordOutcome(session, completed, status);
        session.Pending = null; _actorActions.Remove(session.ActorPrincipal); session.Status = status; session.Revision++; Publish(session.Deck, session.Actor);
    }
    private void Disconnect(Session session, string reason)
    {
        if (session.Pending is { } unfinished) RecordOutcome(session, unfinished, reason);
        if (session.Pending is { Runtime: { } runtime } pending) _runtime.TryComplete(runtime, pending.OperationId, process => process.CreateErrorResult(pending.ReturnType, "Disconnected"));
        foreach (var pair in session.References)
            foreach (var reference in pair.Value.Values) { _references.Remove(reference.Token); session.ReferenceProcesses[pair.Key].TryReleaseOpaque(reference.Value); }
        session.Grants.Clear(); _sessions.Remove(session.Id); _deckSessions.Remove(session.Deck);
        if (_actorActions.GetValueOrDefault(session.ActorPrincipal) == session.Id) _actorActions.Remove(session.ActorPrincipal);
        Publish(session.Deck, session.Actor, reason);
        Log.Info($"NET session {session.Id} disconnected: {reason}.");
    }
    private void OnRequest(Entity<NCMetaCyberdeckComponent> ent, ref NCMetaNetRequest args)
    {
        var actor = args.Actor;
        if (!_requests.TryConsume(_now) || !_ui.IsUiOpen(ent.Owner, NCMetaNetUiKey.Console, actor) || !_devices.CanManage(ent.Owner, actor)) return;
        if (args.Operation == NCMetaNetOperation.Connect && args.AccessPoint is { } target) { TryConnect(ent.Owner, actor, GetEntity(target)); return; }
        if (args.Operation == NCMetaNetOperation.Disconnect && _connecting.ContainsKey(ent.Owner)) { TryDisconnect(ent.Owner, actor); return; }
        TryExecuteActionCore(ent.Owner, actor, args.Operation, args.SessionId, args.Revision, args.NodeId);
    }
    private void Publish(EntityUid deck, EntityUid actor, string disconnected = "nc-meta-net-disconnected")
    {
        if (!_ui.IsUiOpen(deck, NCMetaNetUiKey.Console, actor)) return;
        var state = BuildState(deck, actor, disconnected);
        _ui.ServerSendUiMessage(deck, NCMetaNetUiKey.Console, new NCMetaNetUpdate(state), actor);
    }
}
