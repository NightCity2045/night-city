// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.Server._NC.Bank;
using Content.Server._NC.City.Atlas;
using Content.Server._NC.Police;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.City.Map;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.Dispatch;
using Content.Shared.CartridgeLoader;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Dispatch;

/// <summary>
/// City emergency dispatch for NCPD and Trauma Team. Residents call from the PDA emergency program, Trauma
/// subscribers' biomonitors call by themselves and gunfire in districts under police coverage raises an alert.
/// Service members get a PDA notification, see the incidents in their dispatcher program and on the city map, take
/// them and close them; the crew is on scene as soon as one of them reaches the place.
/// </summary>
public sealed partial class NCDispatchSystem : EntitySystem
{
    public static readonly ProtoId<NCEmergencyServicePrototype> PoliceService = "NCPD";
    public static readonly ProtoId<NCEmergencyServicePrototype> TraumaService = "TraumaTeam";

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CartridgeRefreshInterval = TimeSpan.FromSeconds(5);

    /// <summary>Nobody took the incident in this time: it is closed.</summary>
    public static readonly TimeSpan OpenExpiry = TimeSpan.FromMinutes(15);

    /// <summary>A taken incident nobody closed is closed after this time.</summary>
    public static readonly TimeSpan AcceptedExpiry = TimeSpan.FromMinutes(30);

    /// <summary>The crew reached the place and nobody closed the incident.</summary>
    public static readonly TimeSpan OnSceneExpiry = TimeSpan.FromMinutes(10);

    /// <summary>A service member this close to the place is on scene.</summary>
    public const float ArrivalRange = 3f;

    public const int MaximumMessageLength = 120;

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private SharedMindSystem _minds = default!;
    [Dependency] private SharedJobSystem _jobs = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private CartridgeLoaderSystem _cartridgeLoader = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private NCMapCoordinatesSystem _mapCoordinates = default!;
    [Dependency] private NCCityAtlasSystem _atlas = default!;
    [Dependency] private NCPoliceCoverageSystem _coverage = default!;
    [Dependency] private NCBankSystem _bank = default!;

    private readonly List<NCDispatchIncident> _incidents = new();
    private readonly Dictionary<ProtoId<JobPrototype>, ProtoId<NCEmergencyServicePrototype>> _jobServices = new();
    private readonly List<(EntityUid Uid, ProtoId<NCEmergencyServicePrototype> Service, NCMapCoordinates At)> _members = new();
    private int _nextId = 1;
    private TimeSpan _nextUpdate;
    private TimeSpan _nextCartridgeRefresh;
    private bool _dirty;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        SubscribeLocalEvent<ActorComponent, NCCityMapCollectMarkersEvent>(OnCollectMarkers);
        InitializeCartridges();
        InitializeTrauma();
        InitializeShooting();
        RebuildServices();
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _incidents.Clear();
        _cooldowns.Clear();
        _plans.Clear();
        _nextId = 1;
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<NCEmergencyServicePrototype>() || args.WasModified<DepartmentPrototype>())
            RebuildServices();
    }

    private void RebuildServices()
    {
        _jobServices.Clear();
        foreach (var service in _prototypes.EnumeratePrototypes<NCEmergencyServicePrototype>())
        {
            if (!_prototypes.TryIndex(service.Department, out var department))
                continue;

            foreach (var job in department.Roles)
                _jobServices[job] = service.ID;
        }
    }

    #region Queries

    /// <summary>The emergency service the person works for, if any.</summary>
    public bool TryGetService(EntityUid user, out NCEmergencyServicePrototype service)
    {
        service = default!;
        return _minds.TryGetMind(user, out var mindId, out _) &&
               _jobs.MindTryGetJobId(mindId, out var job) &&
               job is { } jobId &&
               _jobServices.TryGetValue(jobId, out var serviceId) &&
               _prototypes.TryIndex(serviceId, out service!);
    }

    public bool IsMember(EntityUid user, ProtoId<NCEmergencyServicePrototype> service)
    {
        return TryGetService(user, out var found) && found.ID == service;
    }

    /// <summary>Incidents not closed yet, oldest first.</summary>
    public IReadOnlyList<NCDispatchIncident> GetIncidents()
    {
        return _incidents;
    }

    public NCDispatchIncident? GetIncident(int id)
    {
        return _incidents.FirstOrDefault(incident => incident.Id == id);
    }

    /// <summary>The active call a person made to a service, if any.</summary>
    public NCDispatchIncident? GetCall(EntityUid caller, ProtoId<NCEmergencyServicePrototype> service)
    {
        return _incidents.FirstOrDefault(incident => incident.Service == service && incident.Caller == caller);
    }

    #endregion

    #region Incidents

    private NCDispatchIncident CreateIncident(
        ProtoId<NCEmergencyServicePrototype> service,
        NCDispatchIncidentKind kind,
        EntityUid place,
        NCMapCoordinates location,
        string callerName,
        string message)
    {
        var now = _timing.CurTime;
        var incident = new NCDispatchIncident
        {
            Id = _nextId++,
            Service = service,
            Kind = kind,
            Status = NCDispatchStatus.Open,
            CallerName = callerName,
            Message = message,
            Location = location,
            LocationText = DescribeLocation(place),
            Created = now,
            Changed = now,
            LastShot = now,
        };

        _incidents.Add(incident);
        return incident;
    }

    /// <summary>Tells every member of the incident's service about it.</summary>
    private void Announce(NCDispatchIncident incident)
    {
        if (!_prototypes.TryIndex(incident.Service, out var service))
            return;

        var header = Loc.GetString("nc-dispatch-notification-header", ("service", Loc.GetString(service.Name)));
        var text = Loc.GetString("nc-dispatch-notification-new",
            ("id", incident.Id),
            ("kind", KindName(incident.Kind)),
            ("location", incident.LocationText),
            ("z", incident.Location.Z),
            ("message", incident.Message));

        CollectMembers();
        foreach (var (uid, memberService, _) in _members)
        {
            if (memberService == incident.Service)
                Notify(uid, header, text);
        }

        _dirty = true;
    }

    private void TellCaller(NCDispatchIncident incident, string text)
    {
        if (incident.Caller is not { } caller || TerminatingOrDeleted(caller) ||
            !_prototypes.TryIndex(incident.Service, out var service))
        {
            return;
        }

        Notify(caller, Loc.GetString("nc-dispatch-notification-header", ("service", Loc.GetString(service.Name))), text);
    }

    /// <summary>A PDA notification when the person carries a PDA, a chat line otherwise.</summary>
    private void Notify(EntityUid person, string header, string text)
    {
        if (FindPda(person) is { } pda)
        {
            _cartridgeLoader.SendNotification(pda, header, text);
            return;
        }

        if (TryComp<ActorComponent>(person, out var actor))
            _chat.DispatchServerMessage(actor.PlayerSession, $"{header}: {text}");
    }

    private EntityUid? FindPda(EntityUid person)
    {
        if (_inventory.TryGetSlotEntity(person, "id", out var worn) && HasComp<CartridgeLoaderComponent>(worn))
            return worn;

        foreach (var held in _hands.EnumerateHeld(person))
        {
            if (HasComp<CartridgeLoaderComponent>(held))
                return held;
        }

        return null;
    }

    private void Close(NCDispatchIncident incident, string reason)
    {
        if (incident.Status == NCDispatchStatus.Closed)
            return;

        incident.Status = NCDispatchStatus.Closed;
        _incidents.Remove(incident);
        _dirty = true;

        // A paid call that ends without a crew on scene is paid back.
        if (!incident.Arrived && incident.Paid > 0)
            Refund(incident, incident.Paid);

        if (incident.Kind != NCDispatchIncidentKind.Shooting)
            TellCaller(incident, Loc.GetString(reason, ("id", incident.Id)));
    }

    private void Refund(NCDispatchIncident incident, long amount)
    {
        if (incident.Refunded || incident.Client is not { } client ||
            !_prototypes.TryIndex(incident.Service, out var service) || service.Account is not { } account)
        {
            return;
        }

        incident.Refunded = true;
        var refunded = _bank.RefundService(account, client, amount, Loc.GetString(service.Name));
        if (refunded > 0)
            TellCaller(incident, Loc.GetString("nc-dispatch-refund", ("id", incident.Id), ("amount", refunded)));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        if (now >= _nextUpdate)
        {
            _nextUpdate = now + UpdateInterval;
            UpdateIncidents(now);
        }

        if (_dirty || now >= _nextCartridgeRefresh)
        {
            _dirty = false;
            _nextCartridgeRefresh = now + CartridgeRefreshInterval;
            RefreshCartridges();
        }
    }

    private void UpdateIncidents(TimeSpan now)
    {
        if (_incidents.Count == 0)
            return;

        CollectMembers();
        foreach (var incident in _incidents.ToArray())
        {
            Follow(incident);

            if (incident.Status is NCDispatchStatus.Open or NCDispatchStatus.Accepted && CrewOnScene(incident))
            {
                incident.Status = NCDispatchStatus.OnScene;
                incident.Arrived = true;
                incident.Changed = now;
                _dirty = true;
                if (incident.Kind != NCDispatchIncidentKind.Shooting)
                    TellCaller(incident, Loc.GetString("nc-dispatch-arrived", ("id", incident.Id)));
            }

            if (!incident.Arrived && incident.Deadline is { } deadline && now > deadline && !incident.Refunded)
                Refund(incident, LateRefund(incident));

            var expired = incident.Status switch
            {
                NCDispatchStatus.Open when incident.Kind == NCDispatchIncidentKind.Shooting =>
                    now - incident.LastShot > OpenExpiry,
                NCDispatchStatus.Open => now - incident.Created > OpenExpiry,
                NCDispatchStatus.Accepted => now - incident.Changed > AcceptedExpiry,
                NCDispatchStatus.OnScene => now - incident.Changed > OnSceneExpiry,
                _ => false,
            };

            if (expired)
                Close(incident, "nc-dispatch-closed-expired");
        }
    }

    /// <summary>The place of a call follows the caller or patient as long as they exist.</summary>
    private void Follow(NCDispatchIncident incident)
    {
        if (incident.Kind == NCDispatchIncidentKind.Shooting || incident.Subject is not { } subject ||
            TerminatingOrDeleted(subject) || !_mapCoordinates.TryGetCoordinates(subject, out var coordinates))
        {
            return;
        }

        if (coordinates == incident.Location)
            return;

        incident.Location = coordinates;
        incident.LocationText = DescribeLocation(subject);
    }

    private bool CrewOnScene(NCDispatchIncident incident)
    {
        foreach (var (uid, service, at) in _members)
        {
            if (service != incident.Service || uid == incident.Subject || at.NetworkId != incident.Location.NetworkId ||
                at.Z != incident.Location.Z)
            {
                continue;
            }

            if ((at.Position - incident.Location.Position).Length() <= ArrivalRange)
                return true;
        }

        return false;
    }

    /// <summary>Service members able to answer: alive, conscious and placed in the city.</summary>
    private void CollectMembers()
    {
        _members.Clear();
        var query = EntityQueryEnumerator<MindContainerComponent>();
        while (query.MoveNext(out var uid, out var mind))
        {
            if (!mind.HasMind || _mobState.IsIncapacitated(uid) || !TryGetService(uid, out var service) ||
                !_mapCoordinates.TryGetCoordinates(uid, out var at))
            {
                continue;
            }

            _members.Add((uid, service.ID, at));
        }
    }

    #endregion

    #region Texts

    public string KindName(NCDispatchIncidentKind kind)
    {
        return Loc.GetString(kind switch
        {
            NCDispatchIncidentKind.Biomonitor => "nc-dispatch-kind-biomonitor",
            NCDispatchIncidentKind.Shooting => "nc-dispatch-kind-shooting",
            _ => "nc-dispatch-kind-call",
        });
    }

    private string DescribeLocation(EntityUid place)
    {
        _atlas.GetLocation(place, out var district, out var building);
        return (district, building) switch
        {
            ({ } d, { } b) => Loc.GetString("nc-dispatch-location-building", ("building", b), ("district", d)),
            ({ } d, null) => d,
            (null, { } b) => b,
            _ => Loc.GetString("nc-dispatch-location-unknown"),
        };
    }

    #endregion

    #region City map

    /// <summary>Service members see their service's incidents on the city map.</summary>
    private void OnCollectMarkers(Entity<ActorComponent> ent, ref NCCityMapCollectMarkersEvent args)
    {
        if (_incidents.Count == 0 || !TryGetService(ent, out var service))
            return;

        foreach (var incident in _incidents)
        {
            if (incident.Service != service.ID)
                continue;

            args.Markers.Add(new NCCityMapMarker(
                Loc.GetString("nc-dispatch-map-marker", ("id", incident.Id), ("kind", KindName(incident.Kind))),
                incident.Location.NetworkId.ToString(),
                incident.Location.Z,
                incident.Location.Position,
                incident.Status == NCDispatchStatus.Open));
        }
    }

    /// <summary>Map markers of a service's incidents, for the NCPD monitoring console.</summary>
    public void AddMarkers(ProtoId<NCEmergencyServicePrototype> service, List<NCCityMapMarker> markers)
    {
        foreach (var incident in _incidents)
        {
            if (incident.Service != service)
                continue;

            markers.Add(new NCCityMapMarker(
                Loc.GetString("nc-dispatch-map-marker", ("id", incident.Id), ("kind", KindName(incident.Kind))),
                incident.Location.NetworkId.ToString(),
                incident.Location.Z,
                incident.Location.Position,
                incident.Status == NCDispatchStatus.Open));
        }
    }

    #endregion
}
