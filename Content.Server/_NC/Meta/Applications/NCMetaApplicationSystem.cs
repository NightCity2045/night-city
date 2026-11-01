// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Content.Meta.Hosting;
using Content.Meta.Mui;
using Content.Meta.Runtime;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Server._NC.Meta.Authority;
using Content.Shared._NC.Meta.UI;
using Content.Shared.CCVar;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Enums;
using Robust.Shared.Configuration;
using Robust.Shared.Player;

namespace Content.Server._NC.Meta.Applications;

/// <summary>Private, bounded application sessions over one existing installation and its normal scheduler.</summary>
public sealed partial class NCMetaApplicationSystem : EntitySystem
{
    [Dependency] private NCMetaAuthoritySystem _authority = default!;
    private sealed class ActionRequest(Guid id, string action, string body, string hash, long deadline)
    {
        public readonly Guid Id = id;
        public readonly string Action = action;
        public string Body = body;
        public readonly string Hash = hash;
        public readonly long Deadline = deadline;
        public bool Executed;
        public bool Success;
    }
    private sealed record Receipt(string Hash, bool Completed, bool Success, long Revision);
    private sealed record ActionIngress(Guid Id, string Action, string Body, long Revision);
    private sealed record ReplayEntry(NCMetaAppState State, int Bytes, long RetainedBytes);
    private sealed class Session
    {
        public required Guid Id;
        public required NCMetaAppRegistration Registration;
        public required EntityUid UiOwner;
        public required EntityUid User;
        public required ICommonSession Connection;
        public required DateTime ConnectedTime;
        public required Enum UiKey;
        public required string Party;
        public required string View;
        public required NCMetaAppPresentation Presentation;
        public required Func<bool> RouteCurrent;
        public required NCSessionEpoch WorldEpoch;
        public required long Expires;
        public long Revision = -1;
        public long MemoryLease;
        public long ViewVersion;
        public ActionIngress? Ingress;
        public long PendingAck = -1;
        public long? PendingResync;
        public bool InitialReady;
        public NCMetaAppState[]? ResyncPlan;
        public int ResyncIndex;
        public PendingProjection? Output;
        public IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>();
        public LinkedListNode<Session>? IngressNode;
        public LinkedListNode<Session>? ActionNode;
        public LinkedListNode<Session>? ControlNode;
        public LinkedListNode<Session>? MemberNode;
        public (long At, long Order, Guid Id)? ViewKey;
        public (long At, long Order, Guid Id)? OutputKey;
        public required ulong UserToken;
        public required ulong SessionToken;
        public Guid QuotaId;
        public NCMetaManagementProof? OwnerManagement;
        public MetaValue? UserRef;
        public MetaValue? SessionRef;
        public string Json = "";
        public long Acknowledged = -1;
        public long NextPublish;
        public long LastActive;
        public bool NeedsView = true;
        public bool Busy;
        public bool ForceSnapshot;
        public ActionRequest? Action;
        public readonly Dictionary<Guid, Receipt> Receipts = [];
        public readonly Queue<Guid> ReceiptOrder = [];
        public readonly Queue<ReplayEntry> Replay = [];
        public int ReplayBytes;
    }

    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private INetConfigurationManager _netConfiguration = default!;
    private readonly Dictionary<(EntityUid Host, Guid Generation), NCMetaAppRegistration> _applications = [];
    private readonly Dictionary<Guid, Session> _sessions = [];
    private readonly Dictionary<ICommonSession, MetaRateLimit> _actionRates = [];
    private readonly Dictionary<ICommonSession, MetaRateLimit> _resyncRates = [];
    private sealed record HostingQuota(Guid Id, EntityUid Host, Guid Generation, int Sessions, Func<bool> Current);
    private readonly Dictionary<Guid, HostingQuota> _hostingQuotas = [];
    private readonly Dictionary<EntityUid, long> _invalidatedAt = [];
    private readonly Dictionary<(EntityUid Host, Guid Generation), ApplicationWork> _applicationWork = [];
    private readonly ApplicationWork?[] _wakeScratch = new ApplicationWork?[64];
    private NCSessionEpoch _epoch;
    private long _now;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCMetaApplicationUiComponent, NCMetaAppActionMessage>(OnAction);
        SubscribeLocalEvent<NCMetaApplicationUiComponent, NCMetaAppAckMessage>(OnAck);
        SubscribeLocalEvent<NCMetaApplicationUiComponent, NCMetaAppResyncMessage>(OnResync);
        SubscribeLocalEvent<NCMetaApplicationUiComponent, NCMetaAppCloseMessage>(OnClose);
        SubscribeLocalEvent<NCMetaApplicationUiComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<NCMetaApplicationUiComponent, ComponentShutdown>(OnUiShutdown);
        _runtime.ViewStateChanged += OnViewStateChanged;
    }

    public override void Shutdown()
    {
        _runtime.ViewStateChanged -= OnViewStateChanged;
        foreach (var id in _sessions.Keys.ToArray())
            Close(id, "nc-meta-app-closed");
        _applications.Clear();
        _actionRates.Clear();
        _resyncRates.Clear();
        _hostingQuotas.Clear();
        _invalidatedAt.Clear();
        foreach (var work in _applicationWork.Values.ToArray()) work.Cancel(MetaStopReason.Stop);
        _applicationWork.Clear();
        base.Shutdown();
    }

    public bool TryRegister(EntityUid host, EntityUid user, MuiVerifiedApplication application, NCMetaAppPolicy policy, Guid runtimeGeneration = default)
    {
        return _devices.CanManage(host, user) && _devices.TryGetEntry(host, out var device) &&
            TryRegisterRecovered(host, device.Owner, application, policy, runtimeGeneration);
    }

    /// <summary>Trusted recovery path over the freshly admitted live installation, never a client-provided owner or policy.</summary>
    public bool TryRegisterRecovered(EntityUid host, string owner, MuiVerifiedApplication application, NCMetaAppPolicy policy, Guid runtimeGeneration = default)
    {
        foreach (var stale in _applications.Where(pair => !_runtime.TryGetGeneration(pair.Key.Host, pair.Value.Owner, pair.Key.Generation, out _)).Select(pair => pair.Key).ToArray())
        {
            _applications.Remove(stale);
            foreach (var id in _sessions.Values.Where(session => session.Registration.Host == stale.Host && session.Registration.RuntimeGeneration == stale.Generation).Select(session => session.Id).ToArray())
                Close(id, "nc-meta-app-updated");
            if (!_applications.Keys.Any(key => key.Host == stale.Host)) _invalidatedAt.Remove(stale.Host);
        }
        NCMetaRuntimeInstance runtime;
        if (!_devices.TryGetEntry(host, out var device) || device.Owner != owner || !_devices.IsPowered(host) ||
            !_persistence.TryCaptureTime(out _, out _) || !(runtimeGeneration == Guid.Empty
                ? _runtime.TryGetInstallation(host, owner, out runtime) : _runtime.TryGetGeneration(host, owner, runtimeGeneration, out runtime)) ||
            runtime.Owner != owner || runtime.Artifact.Digest != application.Artifact.Digest ||
            policy.PublicViews.Any(x => !application.Views.ContainsKey(x)) || policy.PublicActions.Any(x => !application.Actions.ContainsKey(x)) ||
            policy.AudienceParties.Count > 64 || policy.GrantedScopes.Count > 64 ||
            !_applications.ContainsKey((host, runtime.Generation)) && _applications.Count >= 64)
            return false;
        foreach (var id in _sessions.Values.Where(x => x.Registration.Host == host && x.Registration.RuntimeGeneration == runtime.Generation).Select(x => x.Id).ToArray())
            Close(id, "nc-meta-app-updated");
        var registration = new NCMetaAppRegistration(host, runtime.Owner, runtime.Generation, application, policy);
        if (_applicationWork.Remove((host, runtime.Generation), out var previous)) previous.Cancel(MetaStopReason.Stop);
        _applications.Remove((host, runtime.Generation));
        var packageBytes = 8192L + application.ContractJson.Length * 2L + application.StyleJson.Length * 2L +
            application.Views.Values.Sum(view => view.Source.Length * 2L + view.Document.NodeCount * 640L) +
            application.Resources.Sum(resource => 128L + resource.Key.Length * 2L + resource.Value.Length * 2L);
        if (!_runtime.TryReserveApplicationMemory(host, runtime.Generation, packageBytes, out var packageLease)) return false;
        var work = new ApplicationWork(this, registration, runtime, packageLease);
        if (!_runtime.TryBindApplicationWork(host, runtime.Generation, work))
        { _runtime.TryReleaseApplicationMemory(runtime.Generation, packageLease); return false; }
        _applications[(host, runtime.Generation)] = registration;
        _applicationWork[(host, runtime.Generation)] = work;
        return true;
    }

    public bool TryOpenDefault(EntityUid terminal, EntityUid user, EntityUid host, out string notice, string? viewId = null, Guid runtimeGeneration = default)
    {
        notice = Loc.GetString("nc-meta-app-access-lost");
        if (!TrySelectRegistration(host, runtimeGeneration, out var registration))
            return false;
        var view = viewId ?? registration.Application.Views.Keys.Order(StringComparer.Ordinal).FirstOrDefault();
        if (view is null || !TryOpen(terminal, user, host, view, NCMetaAppPresentation.Desktop, NCMetaUiKey.Application,
            () => _devices.CanManage(terminal, user) && _runtime.CanReach(terminal, host), out _, runtimeGeneration: registration.RuntimeGeneration))
            return false;
        notice = "";
        return true;
    }

    /// <summary>Legacy host selection fails closed when several live applications share a physical host.</summary>
    public bool TryGetRegistration(EntityUid host, out NCMetaAppRegistration registration) => TryGetUniqueRegistration(host, null, out registration);

    public bool TryGetRegistration(EntityUid host, Guid generation, out NCMetaAppRegistration registration) =>
        _applications.TryGetValue((host, generation), out registration!) &&
        _runtime.TryGetGeneration(host, registration.Owner, generation, out _);

    public bool TryGetRegistration(EntityUid host, string packageDigest, out NCMetaAppRegistration registration) =>
        TryGetUniqueRegistration(host, packageDigest, out registration);

    private bool TrySelectRegistration(EntityUid host, Guid generation, out NCMetaAppRegistration registration) => generation == Guid.Empty
        ? TryGetRegistration(host, out registration) : TryGetRegistration(host, generation, out registration);

    private bool TryGetUniqueRegistration(EntityUid host, string? packageDigest, out NCMetaAppRegistration registration)
    {
        registration = default!;
        var found = false;
        foreach (var candidate in _applications.Values)
        {
            if (candidate.Host != host || packageDigest is not null && candidate.Application.PackageDigest != packageDigest ||
                !_runtime.TryGetGeneration(host, candidate.Owner, candidate.RuntimeGeneration, out _)) continue;
            if (found) { registration = default!; return false; }
            registration = candidate;
            found = true;
        }
        return found;
    }

    public bool TryPublishView(EntityUid host, EntityUid user, string view, Guid runtimeGeneration = default)
    {
        if (!_devices.CanManage(host, user) || !TrySelectRegistration(host, runtimeGeneration, out var registration))
            return false;
        return TryRestorePublishedView(host, registration.Owner, registration.Application.PackageDigest, view);
    }

    /// <summary>Restores only a view whose publication was read from trusted durable metadata by a server caller.</summary>
    public bool TryRestorePublishedView(EntityUid host, string owner, string packageDigest, string view)
    {
        if (!_devices.TryGetEntry(host, out var device) || device.Owner != owner || !TryGetRegistration(host, packageDigest, out var registration) ||
            registration.Owner != owner || registration.Application.PackageDigest != packageDigest ||
            !registration.Application.Contract.Views.TryGetValue(view, out var contract))
            return false;
        if (registration.Policy.PublicViews.Contains(view))
            return true;
        foreach (var id in _sessions.Values.Where(x => x.Registration.Host == host && x.Registration.RuntimeGeneration == registration.RuntimeGeneration).Select(x => x.Id).ToArray())
            Close(id, "nc-meta-app-updated");
        _applications[(host, registration.RuntimeGeneration)] = registration with { Policy = registration.Policy with
        {
            PublicViews = registration.Policy.PublicViews.Add(view),
            PublicActions = registration.Policy.PublicActions.Union(contract.Actions),
        } };
        return true;
    }

    public bool TryOpen(EntityUid uiOwner, EntityUid user, EntityUid host, string view, NCMetaAppPresentation presentation,
        Enum uiKey, Func<bool> routeCurrent, out Guid sessionId, Guid quotaId = default, int maximumQuotaSessions = MuiLimits.InstallationSessions,
        Guid runtimeGeneration = default)
    {
        sessionId = Guid.Empty;
        NCMetaManagementProof? ownerManagement = null;
        if (TrySelectRegistration(host, runtimeGeneration, out var ownerRegistration) &&
            _authority.TryGetManagementProof(user, ownerRegistration.Owner, out var management) &&
            _authority.CanAttendService(management)) ownerManagement = management;
        if (!_persistence.TryCaptureTime(out var epoch, out var time) || !TrySelectRegistration(host, runtimeGeneration, out var registration) ||
            !registration.Application.Views.ContainsKey(view) || !TryComp<ActorComponent>(user, out var actor) ||
            actor.PlayerSession.Status != SessionStatus.InGame || actor.PlayerSession.AttachedEntity != user ||
            !_parties.TryResolve(user, out var party) || !routeCurrent() ||
            !CanView(registration, party.ToString(), view) && (ownerManagement == null ||
                ownerManagement.Owner.ToString() != registration.Owner || !_authority.CanAttendService(ownerManagement)) ||
            !_runtime.TryGetGeneration(host, registration.Owner, registration.RuntimeGeneration, out _) ||
            maximumQuotaSessions is < 1 or > MuiLimits.InstallationSessions)
            return false;
        _epoch = epoch;
        _now = time.Ticks / TimeSpan.TicksPerMillisecond;
        var quotas = _hostingQuotas.Values.Where(x => x.Host == host && x.Generation == registration.RuntimeGeneration && x.Current()).ToArray();
        var quota = quotas.FirstOrDefault(x => x.Id == quotaId);
        if (quotaId != Guid.Empty && (quota is null || maximumQuotaSessions > quota.Sessions))
            return false;
        var replaced = presentation == NCMetaAppPresentation.Desktop
            ? _sessions.Values.Where(x => x.UiOwner == uiOwner && x.User == user && x.UiKey.Equals(uiKey) && x.Presentation == presentation).ToArray()
            : [];
        var existing = replaced.FirstOrDefault(x => x.Registration == registration && x.View == view && x.QuotaId == quotaId && CanUse(x));
        if (existing is not null)
        {
            existing.LastActive = _now;
            if (!_resyncRates.TryGetValue(existing.Connection, out var rate))
                _resyncRates.Add(existing.Connection, rate = new(2, 1));
            if (existing.Revision >= 0 && rate.TryConsume(_now))
                Send(existing, BuildState(existing, true, existing.Json, existing.Revision, existing.Revision));
            sessionId = existing.Id;
            return true;
        }
        var remaining = _sessions.Values.Where(x => !replaced.Contains(x)).ToArray();
        if (remaining.Length >= 1024 || remaining.Count(x => x.Registration.Host == host && x.Registration.RuntimeGeneration == registration.RuntimeGeneration) >= MuiLimits.InstallationSessions ||
            remaining.Count(x => x.Connection == actor.PlayerSession) >= MuiLimits.ClientSessions ||
            quotaId != Guid.Empty && remaining.Count(x => x.QuotaId == quotaId) >= maximumQuotaSessions ||
            quotaId == Guid.Empty && remaining.Count(x => x.Registration.Host == host && x.Registration.RuntimeGeneration == registration.RuntimeGeneration && x.QuotaId == Guid.Empty) >=
                MuiLimits.InstallationSessions - quotas.Sum(x => x.Sessions))
            return false;
        if (!_runtime.TryReserveApplicationMemory(host, registration.RuntimeGeneration, 2048, out var sessionLease)) return false;
        if (!_ui.TryOpenUi(uiOwner, uiKey, user))
        { _runtime.TryReleaseApplicationMemory(registration.RuntimeGeneration, sessionLease); return false; }
        foreach (var prior in replaced)
            Close(prior.Id, "nc-meta-app-updated");
        EnsureComp<NCMetaApplicationUiComponent>(uiOwner);
        var id = Guid.NewGuid();
        var session = new Session
        {
            Id = id, Registration = registration, UiOwner = uiOwner, User = user,
            Connection = actor.PlayerSession, ConnectedTime = actor.PlayerSession.ConnectedTime,
            UiKey = uiKey, Party = party.ToString(), View = view, Presentation = presentation,
            RouteCurrent = routeCurrent, WorldEpoch = epoch, Expires = _now + 15 * 60 * 1000,
            LastActive = _now, MemoryLease = sessionLease,
            UserToken = Token(), SessionToken = Token(),
            QuotaId = quotaId,
            OwnerManagement = ownerManagement,
        };
        _sessions.Add(session.Id, session);
        _applicationWork[(host, registration.RuntimeGeneration)].Add(session);
        if (!_actionRates.ContainsKey(session.Connection))
            _actionRates.Add(session.Connection, new(20, 10));
        Enqueue(session);
        sessionId = session.Id;
        return true;
    }

    /// <summary>Trusted durable-lease adapter. Reserved slots reduce the free visitor allowance.</summary>
    public bool TryReserveHostingSessions(EntityUid host, Guid leaseId, int sessions, Func<bool> current, Guid runtimeGeneration = default)
    {
        if (leaseId == Guid.Empty || sessions is < 1 or > MuiLimits.InstallationSessions || !current() ||
            !TrySelectRegistration(host, runtimeGeneration, out var registration)) return false;
        var generation = registration.RuntimeGeneration;
        foreach (var stale in _hostingQuotas.Values.Where(x => !x.Current()).Select(x => x.Id).ToArray()) _hostingQuotas.Remove(stale);
        if (_hostingQuotas.Count >= 1024 && !_hostingQuotas.ContainsKey(leaseId) ||
            _hostingQuotas.Values.Where(x => x.Host == host && x.Generation == generation && x.Id != leaseId).Sum(x => x.Sessions) + sessions > MuiLimits.InstallationSessions)
            return false;
        _hostingQuotas[leaseId] = new(leaseId, host, generation, sessions, current);
        // Release free visitors occupying newly purchased capacity; physical controls and the workspace remain available.
        var allowed = MuiLimits.InstallationSessions - _hostingQuotas.Values.Where(x => x.Host == host && x.Generation == generation).Sum(x => x.Sessions);
        foreach (var id in _sessions.Values.Where(x => x.Registration.Host == host && x.Registration.RuntimeGeneration == generation && x.QuotaId == Guid.Empty).Skip(allowed).Select(x => x.Id).ToArray())
            Close(id, "nc-meta-app-access-lost");
        return true;
    }
    public void ReleaseHostingSessions(Guid leaseId) => _hostingQuotas.Remove(leaseId);

    /// <summary>Trusted native watch completion hook. Reuses private view sessions and the installation's existing VM scheduler.</summary>
    public bool TryInvalidateViews(EntityUid host)
    {
        if (!_applications.Keys.Any(key => key.Host == host)) return false;
        if (_invalidatedAt.TryGetValue(host, out var last) && last == _now) return true;
        _invalidatedAt[host] = _now;
        foreach (var session in _sessions.Values.Where(x => x.Registration.Host == host))
        { session.NeedsView = true; Enqueue(session); }
        return true;
    }
    private void OnViewStateChanged(EntityUid host) => TryInvalidateViews(host);

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_persistence.TryCaptureTime(out var epoch, out var time))
        {
            if (_sessions.Count > 0)
                foreach (var id in _sessions.Keys.ToArray())
                    Close(id, "nc-meta-app-closed");
            return;
        }
        _epoch = epoch;
        _now = time.Ticks / TimeSpan.TicksPerMillisecond;
        var count = 0;
        foreach (var work in _applicationWork.Values) _wakeScratch[count++] = work;
        for (var index = 0; index < count; index++)
        {
            var work = _wakeScratch[index]!; _wakeScratch[index] = null;
            if (work.HasScheduledWork) _runtime.TryWakeApplicationWork(work.Registration.Host, work.Registration.RuntimeGeneration);
        }
    }

    private bool CanUse(Session session) => session.WorldEpoch == _epoch && _now < session.Expires &&
        _now - session.LastActive < 5 * 60 * 1000 && Exists(session.UiOwner) && Exists(session.User) &&
        session.Connection.Status == SessionStatus.InGame && session.Connection.AttachedEntity == session.User &&
        session.Connection.ConnectedTime == session.ConnectedTime && TryComp<ActorComponent>(session.User, out var actor) &&
        actor.PlayerSession == session.Connection && _ui.IsUiOpen(session.UiOwner, session.UiKey, session.User) &&
        session.RouteCurrent() && _parties.TryResolve(session.User, out var party) && party.ToString() == session.Party &&
        TryGetRegistration(session.Registration.Host, session.Registration.RuntimeGeneration, out var registration) && registration == session.Registration &&
        (CanView(registration, session.Party, session.View) || OwnsRegistration(session));

    private bool OwnsRegistration(Session session) => session.OwnerManagement is { } proof &&
        proof.Owner.ToString() == session.Registration.Owner && proof.Actor.ToString() == session.Party &&
        proof.ActorEntity == session.User && _authority.CanAttendService(proof);

    private static bool CanView(NCMetaAppRegistration registration, string party, string view) =>
        party == registration.Owner || registration.Policy.AudienceParties.Contains(party) || registration.Policy.PublicViews.Contains(view);

    private bool CanAct(Session session, string action) => CanUse(session) &&
        (session.Party == session.Registration.Owner || OwnsRegistration(session) || session.Registration.Policy.AudienceParties.Contains(session.Party) || session.Registration.Policy.PublicActions.Contains(action)) &&
        session.Registration.Application.Contract.Views[session.View].Actions.Contains(action) &&
        session.Registration.Application.Contract.Actions.TryGetValue(action, out var contract) &&
        contract.Scopes.All(session.Registration.Policy.GrantedScopes.Contains);


    private MetaValue Context(Session session, MetaProcess process)
    {
        var application = session.Registration.Application;
        session.UserRef ??= process.CreateOpaque(application.UserRefTypeId, session.UserToken);
        session.SessionRef ??= process.CreateOpaque(application.SessionRefTypeId, session.SessionToken);
        var presentation = session.Presentation switch
        {
            NCMetaAppPresentation.Agent => "agent", NCMetaAppPresentation.Website => "website",
            NCMetaAppPresentation.Dashboard => "dashboard", _ => "desktop",
        };
        var locale = _netConfiguration.GetClientCVar(session.Connection.Channel, CCVars.NCClientCulture);
        if (locale.Length is < 1 or > 35 || !locale.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            locale = "en-US";
        return process.CreateRecord(application.ContextTypeId, [session.UserRef.Value, session.SessionRef.Value,
            process.CreateString(session.View), process.CreateString(presentation), process.CreateString(locale)]);
    }


    private NCMetaAppState BuildState(Session session, bool full, string json, long previous, long revision)
    {
        var app = session.Registration.Application;
        NCMetaMuiPresentation? presentation = null;
        if (full && !session.Registration.Presentations.TryGetValue(session.View, out presentation))
        {
            presentation = NCMetaMuiPresentationBuilder.Build(app, session.View, session.Registration.Presentations.Values.FirstOrDefault());
            session.Registration.Presentations.Add(session.View, presentation);
        }
        return new()
        {
            SessionId = session.Id, ViewId = session.View, Title = app.Artifact.Manifest.PackageName,
            Website = session.Presentation == NCMetaAppPresentation.Website,
            ContractJson = full ? app.ContractJson : "", Document = full ? app.Views[session.View].Source : "",
            Styles = full ? app.StyleJson : "", StateJson = json, BaseRevision = previous, Revision = revision, FullSnapshot = full,
            Presentation = presentation, Values = NCMetaMuiPresentationBuilder.ReadValues(json),
            StateBytes = Encoding.UTF8.GetByteCount(json),
            Resources = full ? app.Resources.Select(x => new NCMetaMuiResource(x.Key, x.Value)).ToList() : [],
        };
    }

    public bool TryHandleAction(EntityUid uiOwner, EntityUid user, NCMetaAppActionMessage message)
    {
        if (!_sessions.TryGetValue(message.SessionId, out var session) || session.UiOwner != uiOwner || session.User != user ||
            message.RequestId == Guid.Empty || message.ActionId.Length > 128 || message.FormJson.Length > MuiLimits.ActionBytes ||
            session.Ingress != null || !_actionRates[session.Connection].TryConsume(_now) ||
            !TryResizeSession(session, 512L + message.ActionId.Length * 2L + message.FormJson.Length * 2L)) return false;
        session.Ingress = new(message.RequestId, message.ActionId, message.FormJson, message.BaseRevision);
        _applicationWork[(session.Registration.Host, session.Registration.RuntimeGeneration)].EnqueueIngress(session);
        return true;
    }

    private void OnAction(Entity<NCMetaApplicationUiComponent> ent, ref NCMetaAppActionMessage args) => TryHandleAction(ent.Owner, args.Actor, args);
    private void OnAck(Entity<NCMetaApplicationUiComponent> ent, ref NCMetaAppAckMessage args)
    {
        if (!_sessions.TryGetValue(args.SessionId, out var session) || session.UiOwner != ent.Owner || session.User != args.Actor || args.Revision < 0) return;
        session.PendingAck = Math.Max(session.PendingAck, args.Revision);
        _applicationWork[(session.Registration.Host, session.Registration.RuntimeGeneration)].EnqueueControl(session);
    }
    private void OnResync(Entity<NCMetaApplicationUiComponent> ent, ref NCMetaAppResyncMessage args)
    {
        Session? session;
        if (args.SessionId == Guid.Empty && args.Revision == -1)
        {
            session = null;
            foreach (var candidate in _sessions.Values)
            {
                if (candidate.UiOwner != ent.Owner || candidate.User != args.Actor || !candidate.UiKey.Equals(args.UiKey)) continue;
                if (session != null) return;
                session = candidate;
            }
            if (session == null || session.InitialReady || !CanUse(session)) return;
            // One ready acknowledgement per admitted session. Four simultaneous window opens must
            // not compete for the ordinary resync limiter; subsequent requests retain that limiter.
            session.InitialReady = true;
        }
        else
        {
            if (!_sessions.TryGetValue(args.SessionId, out session) || session.UiOwner != ent.Owner || session.User != args.Actor) return;
            if (!_resyncRates.TryGetValue(session.Connection, out var rate)) _resyncRates.Add(session.Connection, rate = new(2, 1));
            if (!rate.TryConsume(_now)) return;
        }
        session.PendingResync = args.Revision; session.ResyncPlan = null; session.ResyncIndex = 0;
        _applicationWork[(session.Registration.Host, session.Registration.RuntimeGeneration)].EnqueueControl(session);
    }

    private void OnClose(Entity<NCMetaApplicationUiComponent> ent, ref NCMetaAppCloseMessage args)
    {
        if (TrySession(ent.Owner, args.Actor, args.SessionId, out _))
            Close(args.SessionId, "nc-meta-app-closed");
    }
    private void OnUiClosed(Entity<NCMetaApplicationUiComponent> ent, ref BoundUIClosedEvent args)
    {
        var user = args.Actor;
        var key = args.UiKey;
        foreach (var id in _sessions.Values.Where(x => x.UiOwner == ent.Owner && x.User == user && x.UiKey.Equals(key)).Select(x => x.Id).ToArray())
            Close(id, "nc-meta-app-closed");
    }
    private void OnUiShutdown(Entity<NCMetaApplicationUiComponent> ent, ref ComponentShutdown args)
    {
        foreach (var id in _sessions.Values.Where(x => x.UiOwner == ent.Owner).Select(x => x.Id).ToArray())
            Close(id, "nc-meta-app-closed");
    }

    private bool TrySession(EntityUid uiOwner, EntityUid user, Guid id, out Session session) =>
        _sessions.TryGetValue(id, out session!) && session.UiOwner == uiOwner && session.User == user && CanUse(session);
    private void Enqueue(Session session)
    {
        if (_applicationWork.TryGetValue((session.Registration.Host, session.Registration.RuntimeGeneration), out var work)) work.Dirty(session);
    }

    private void Send(Session session, NCMetaAppState state) =>
        _ui.ServerSendUiMessage(session.UiOwner, session.UiKey, new NCMetaAppUpdateMessage(state), session.User);
    private void SendActionResult(Session session, Guid id, bool pending, bool success, long revision) =>
        _ui.ServerSendUiMessage(session.UiOwner, session.UiKey, new NCMetaAppActionResultMessage(session.Id, id, revision, pending, success,
            pending ? Loc.GetString("nc-meta-app-action-pending") : Loc.GetString(success ? "nc-meta-app-action-complete" : "nc-meta-app-action-failed")), session.User);

    public void Close(Guid id, string reason)
    {
        if (!_sessions.Remove(id, out var session))
            return;
        if (_applicationWork.TryGetValue((session.Registration.Host, session.Registration.RuntimeGeneration), out var work)) work.Remove(session);
        if (_runtime.TryGetGeneration(session.Registration.Host, session.Registration.Owner, session.Registration.RuntimeGeneration, out var runtime))
        {
            if (session.UserRef is { } user)
                runtime.Process.TryReleaseOpaque(user);
            if (session.SessionRef is { } view)
                runtime.Process.TryReleaseOpaque(view);
        }
        session.Output?.Dispose(); session.Output = null;
        _runtime.TryReleaseApplicationMemory(session.Registration.RuntimeGeneration, session.MemoryLease);
        session.Json = "";
        session.Receipts.Clear();
        session.Replay.Clear();
        if (Exists(session.UiOwner) && Exists(session.User) && _ui.IsUiOpen(session.UiOwner, session.UiKey, session.User))
            Send(session, new NCMetaAppState { SessionId = id, Closed = true, Notice = Loc.GetString(reason) });
        if (!_sessions.Values.Any(x => x.Connection == session.Connection))
        { _actionRates.Remove(session.Connection); _resyncRates.Remove(session.Connection); }
    }

    private static ulong Token()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes);
    }
    [Dependency] private Robust.Shared.Serialization.IRobustSerializer _wireSerializer = default!;
    private int WireBytes(NCMetaAppState state)
    {
        using var counter = new NCMetaWireSizeStream();
        _wireSerializer.SerializeDirect(counter, state);
        return checked((int) counter.Length);
    }
    private long SessionBytes(Session session) => 2048L + session.Json.Length * 2L +
        session.Fields.Sum(field => 128L + field.Key.Length * 2L + field.Value.Length * 2L) + session.Receipts.Count * 512L +
        (session.Action is { } action ? 512L + action.Action.Length * 2L + action.Body.Length * 2L : 0) +
        (session.Ingress is { } ingress ? 512L + ingress.Action.Length * 2L + ingress.Body.Length * 2L : 0) +
        session.Replay.Sum(entry => entry.RetainedBytes);
    private bool TryResizeSession(Session session, long extra = 0) =>
        _runtime.TryResizeApplicationMemory(session.Registration.Host, session.Registration.RuntimeGeneration,
            session.MemoryLease, checked(SessionBytes(session) + extra));
}
