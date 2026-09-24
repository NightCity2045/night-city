// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server._NC.City.Zones;
using Content.Shared._NC.Agent.Shards.TraumaResponse;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.Identity;
using Content.Shared._NC.TraumaTeam;
using Content.Shared.CartridgeLoader;
using Content.Shared.GameTicking;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.TraumaTeam;

public enum NCTraumaResponseRequestStatus : byte
{
    Success,
    NoCoverage,
    AlreadyActive,
    Cooldown,
    LocationUnavailable,
    Invalid,
}

/// <summary>
/// Owns round-scoped Trauma Team calls. Subscriptions remain persistent, while emergencies deliberately end with
/// the round. Exact coordinates are disclosed only to responders participating in the call.
/// </summary>
public sealed partial class NCTraumaResponseSystem : EntitySystem
{
    private static readonly ProtoId<NCTraumaResponseConfigurationPrototype> DefaultConfiguration = "Default";
    [Dependency] private CartridgeLoaderSystem _cartridges = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private NCTraumaTeamSubscriptionSystem _subscriptions = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCZoneSystem _zones = default!;

    private readonly Dictionary<Guid, ResponseCall> _calls = [];
    private readonly Dictionary<NCCharacterId, Guid> _activeByClient = [];
    private readonly Dictionary<NCCharacterId, Guid> _activeByResponder = [];
    private readonly Dictionary<NCCharacterId, TimeSpan> _lastRequestAt = [];
    private readonly Dictionary<EntityUid, OpenShardView> _openViews = [];

    private NCTraumaResponseConfigurationPrototype Configuration => _prototypes.Index(DefaultConfiguration);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCAgentTraumaResponseComponent, CartridgeMessageEvent>(OnUiMessage);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    public async Task<(NCTraumaResponseRequestStatus Status, ResponseCall? Call)> TryRequestAsync(
        EntityUid user,
        NCCharacterId client,
        NCTraumaResponseUrgency urgency,
        string rawDescription)
    {
        var description = rawDescription.Trim();
        if (!Enum.IsDefined(urgency) || description.Length > Configuration.MaximumDescriptionLength)
            return (NCTraumaResponseRequestStatus.Invalid, null);
        ExpireOldCalls();
        if (_activeByClient.ContainsKey(client))
            return (NCTraumaResponseRequestStatus.AlreadyActive, null);
        if (_lastRequestAt.TryGetValue(client, out var last) && _timing.CurTime - last < Configuration.RequestCooldown)
            return (NCTraumaResponseRequestStatus.Cooldown, null);
        var coverage = await _subscriptions.GetActiveCoverageAsync(client);
        if (coverage == null)
            return (NCTraumaResponseRequestStatus.NoCoverage, null);
        // Re-check after persistence I/O so two devices cannot create parallel calls for one character.
        if (_activeByClient.ContainsKey(client))
            return (NCTraumaResponseRequestStatus.AlreadyActive, null);
        if (!_coordinates.TryGetCoordinates(user, out var coordinates))
            return (NCTraumaResponseRequestStatus.LocationUnavailable, null);

        var call = new ResponseCall(
            Guid.NewGuid(),
            client,
            user,
            Name(user),
            coverage.PlanId,
            coverage.DispatchPriority,
            coverage.TreatmentCoverageBasisPoints,
            coverage.MaximumThreatBasisPoints,
            urgency,
            description,
            coordinates,
            ResolveZone(coordinates),
            _timing.CurTime);
        _calls.Add(call.Id, call);
        _activeByClient.Add(client, call.Id);
        _lastRequestAt[client] = _timing.CurTime;
        await RefreshOpenViewsAsync(true);
        return (NCTraumaResponseRequestStatus.Success, call);
    }

    public bool TryCancelClientCall(NCCharacterId client, Guid callId, long expectedVersion)
    {
        if (!_calls.TryGetValue(callId, out var call) || call.Client != client || call.Version != expectedVersion ||
            call.Status is NCTraumaResponseCallStatus.Completed or NCTraumaResponseCallStatus.Cancelled or
                NCTraumaResponseCallStatus.Rejected or
                NCTraumaResponseCallStatus.Expired)
            return false;
        Finish(call, NCTraumaResponseCallStatus.Cancelled);
        _ = RefreshOpenViewsAsync();
        return true;
    }

    public NCTraumaResponseClientCallSummary? GetClientCall(NCCharacterId client)
    {
        ExpireOldCalls();
        if (!_activeByClient.TryGetValue(client, out var id) || !_calls.TryGetValue(id, out var call))
            return null;
        return new(call.Id, call.Version, call.Status, call.PlanId, call.Zone, call.LeadName,
            call.Participants.Values.ToList());
    }

    /// <summary>
    /// Returns only the caller assigned to this responder. A Trauma implant upgrades the request position to a live feed.
    /// </summary>
    public bool TryGetMapMarker(NCCharacterId responder, out NCAgentMapMarker marker)
    {
        marker = default!;
        if (!_activeByResponder.TryGetValue(responder, out var callId) ||
            !_calls.TryGetValue(callId, out var call) ||
            call.Status != NCTraumaResponseCallStatus.Accepted)
            return false;

        var live = false;
        if (!Deleted(call.ClientEntity) && HasComp<NCTraumaTrackingComponent>(call.ClientEntity) &&
            _coordinates.TryGetCoordinates(call.ClientEntity, out var current))
        {
            call.Coordinates = current;
            live = true;
        }

        marker = new NCAgentMapMarker(call.ClientName, call.Coordinates.NetworkId.ToString(),
            call.Coordinates.Z, call.Coordinates.Position, live);
        return true;
    }

    private void OnUiMessage(Entity<NCAgentTraumaResponseComponent> ent, ref CartridgeMessageEvent args)
    {
        if (args is not NCAgentTraumaResponseActionMessage message)
            return;
        _ = HandleUiMessageAsync(ent.Owner, GetEntity(args.LoaderUid), args.Actor, message);
    }

    private async Task HandleUiMessageAsync(EntityUid shard, EntityUid loader, EntityUid user,
        NCAgentTraumaResponseActionMessage message)
    {
        var responder = await GetResponderAsync(user);
        if (Deleted(loader))
            return;
        if (responder == null)
        {
            _cartridges.UpdateCartridgeUiState(loader, UnauthorizedState());
            return;
        }

        _openViews[loader] = new(shard, loader, user, responder.Value.CharacterId);
        var notice = message.Action switch
        {
            NCTraumaResponseAction.Refresh => string.Empty,
            _ => TryAct(responder.Value, message),
        };
        await UpdateUiAsync(loader, user, notice);
        if (message.Action is not NCTraumaResponseAction.Refresh)
            await RefreshOpenViewsAsync();
    }

    private string TryAct(ResponderContext responder, NCAgentTraumaResponseActionMessage message)
    {
        ExpireOldCalls();
        if (!_calls.TryGetValue(message.CallId, out var call) || call.Version != message.ExpectedVersion)
            return "nc-trauma-response-stale";

        switch (message.Action)
        {
            case NCTraumaResponseAction.Accept:
                if (call.Status != NCTraumaResponseCallStatus.Requested ||
                    _activeByResponder.ContainsKey(responder.CharacterId))
                    return "nc-trauma-response-action-denied";
                call.Lead = responder.CharacterId;
                call.Participants.Add(responder.CharacterId, responder.Name);
                call.Status = NCTraumaResponseCallStatus.Accepted;
                _activeByResponder.Add(responder.CharacterId, call.Id);
                break;
            case NCTraumaResponseAction.Reject:
                if (call.Status != NCTraumaResponseCallStatus.Requested)
                    return "nc-trauma-response-action-denied";
                Finish(call, NCTraumaResponseCallStatus.Rejected);
                return "nc-trauma-response-rejected";
            case NCTraumaResponseAction.Complete:
                if (!CanLeadTransition(call, responder.CharacterId, NCTraumaResponseCallStatus.Accepted))
                    return "nc-trauma-response-action-denied";
                Finish(call, NCTraumaResponseCallStatus.Completed);
                return "nc-trauma-response-completed";
            default:
                return "nc-trauma-response-action-denied";
        }

        call.Version++;
        return "nc-trauma-response-updated";
    }

    private async Task UpdateUiAsync(EntityUid loader, EntityUid user, string notice = "")
    {
        var responder = await GetResponderAsync(user);
        if (responder == null || Deleted(loader))
        {
            if (!Deleted(loader))
                _cartridges.UpdateCartridgeUiState(loader, UnauthorizedState());
            return;
        }
        ExpireOldCalls();
        var viewer = responder.Value.CharacterId;
        var active = _activeByResponder.TryGetValue(viewer, out var activeId) && _calls.TryGetValue(activeId, out var activeCall)
            ? Summary(activeCall, viewer)
            : null;
        var queue = _calls.Values
            .Where(value => value.Status == NCTraumaResponseCallStatus.Requested)
            .OrderByDescending(value => value.Priority)
            .ThenBy(value => value.CreatedAt)
            .Select(value => Summary(value, viewer))
            .ToList();
        var recent = _calls.Values
            .Where(value => value.Status is NCTraumaResponseCallStatus.Completed or
                NCTraumaResponseCallStatus.Rejected or NCTraumaResponseCallStatus.Cancelled or
                NCTraumaResponseCallStatus.Expired)
            .OrderByDescending(value => value.ChangedAt)
            .Take(Configuration.RecentCallLimit)
            .Select(value => Summary(value, viewer))
            .ToList();
        var roster = await BuildRosterAsync();
        if (!Deleted(loader))
        {
            _cartridges.UpdateCartridgeUiState(loader, new NCAgentTraumaResponseUiState(true,
                responder.Value.JobId, queue, active, recent, roster, notice));
        }
    }

    private async Task<List<NCTraumaResponseResponderSummary>> BuildRosterAsync()
    {
        var roster = new List<NCTraumaResponseResponderSummary>();
        // Session membership may change while database calls yield; enumerate a stable snapshot.
        foreach (var session in _players.Sessions.ToArray())
        {
            if (session.AttachedEntity is not { Valid: true } entity ||
                !_preferences.TryGetSelectedNCCharacterId(session.UserId, out var character))
                continue;
            var job = await _database.GetNCActiveJobAsync(character);
            if (job == null || !IsResponseJob(job))
                continue;
            roster.Add(new(character.Value, Name(entity), job, _activeByResponder.ContainsKey(character)));
        }
        return roster.OrderBy(value => value.Busy).ThenBy(value => value.Name).ToList();
    }

    private async Task<ResponderContext?> GetResponderAsync(EntityUid user)
    {
        if (!_players.TryGetSessionByEntity(user, out var session) ||
            !_preferences.TryGetSelectedNCCharacterId(session.UserId, out var character))
            return null;
        var job = await _database.GetNCActiveJobAsync(character);
        return job != null && IsResponseJob(job) ? new(character, Name(user), job) : null;
    }

    private NCTraumaResponseCallSummary Summary(ResponseCall call, NCCharacterId viewer)
    {
        var participating = call.Participants.ContainsKey(viewer);
        var waiting = Math.Max(0, (long) (_timing.CurTime - call.CreatedAt).TotalSeconds);
        return new(call.Id, call.Version, participating ? call.Client.Value : 0,
            participating ? call.ClientName : Loc.GetString("nc-trauma-response-covered-client"),
            call.PlanId, call.Priority, call.TreatmentCoverageBasisPoints, call.MaximumThreatBasisPoints,
            call.Urgency, call.Status, call.Description, call.Zone, participating ? call.Coordinates : null,
            waiting, call.LeadName, call.Participants.Values.ToList(), participating);
    }

    private void Finish(ResponseCall call, NCTraumaResponseCallStatus status)
    {
        call.Status = status;
        call.Version++;
        call.ChangedAt = _timing.CurTime;
        _activeByClient.Remove(call.Client);
        foreach (var responder in call.Participants.Keys)
            _activeByResponder.Remove(responder);
    }

    private void ExpireOldCalls()
    {
        foreach (var call in _calls.Values)
        {
            if (call.Status == NCTraumaResponseCallStatus.Requested &&
                _timing.CurTime - call.CreatedAt >= Configuration.RequestLifetime)
                Finish(call, NCTraumaResponseCallStatus.Expired);
        }
    }

    private async Task RefreshOpenViewsAsync(bool notify = false)
    {
        foreach (var view in _openViews.Values.ToArray())
        {
            if (Deleted(view.Shard) || Deleted(view.Loader) || Deleted(view.User) ||
                !TryComp<CartridgeComponent>(view.Shard, out var cartridge) || cartridge.LoaderUid != view.Loader)
            {
                _openViews.Remove(view.Loader);
                continue;
            }
            if (notify)
                _cartridges.SendNotification(view.Loader, Loc.GetString("nc-trauma-response-notification-header"),
                    Loc.GetString("nc-trauma-response-new-call"));
            if (TryComp<CartridgeLoaderComponent>(view.Loader, out var loader) && loader.ActiveProgram == view.Shard)
                await UpdateUiAsync(view.Loader, view.User);
        }
    }

    private string ResolveZone(NCMapCoordinates coordinates)
    {
        var zones = new List<NCZoneInfo>();
        _zones.GetZones(coordinates, zones);
        var names = zones.Where(value => value.ContextSlot is NCZoneContextSlot.District or NCZoneContextSlot.Sector or
                NCZoneContextSlot.Street or NCZoneContextSlot.Building)
            .Select(value => value.Name).TakeLast(3).ToArray();
        return names.Length == 0 ? Loc.GetString("nc-trauma-response-unmapped") : string.Join(" / ", names);
    }

    private static bool CanLeadTransition(ResponseCall call, NCCharacterId responder,
        NCTraumaResponseCallStatus expected) => call.Lead == responder && call.Status == expected;

    private bool IsResponseJob(string jobId) =>
        Configuration.ResponseJobs.Any(value => value.Id == jobId);

    private static NCAgentTraumaResponseUiState UnauthorizedState() =>
        new(false, string.Empty, [], null, [], [], "nc-trauma-response-access-denied");

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _calls.Clear();
        _activeByClient.Clear();
        _activeByResponder.Clear();
        _lastRequestAt.Clear();
        _openViews.Clear();
    }

    public sealed class ResponseCall(
        Guid id,
        NCCharacterId client,
        EntityUid clientEntity,
        string clientName,
        string planId,
        int priority,
        int treatmentCoverageBasisPoints,
        int maximumThreatBasisPoints,
        NCTraumaResponseUrgency urgency,
        string description,
        NCMapCoordinates coordinates,
        string zone,
        TimeSpan createdAt)
    {
        public Guid Id = id;
        public NCCharacterId Client = client;
        public EntityUid ClientEntity = clientEntity;
        public string ClientName = clientName;
        public string PlanId = planId;
        public int Priority = priority;
        public int TreatmentCoverageBasisPoints = treatmentCoverageBasisPoints;
        public int MaximumThreatBasisPoints = maximumThreatBasisPoints;
        public NCTraumaResponseUrgency Urgency = urgency;
        public string Description = description;
        public NCMapCoordinates Coordinates = coordinates;
        public string Zone = zone;
        public TimeSpan CreatedAt = createdAt;
        public TimeSpan ChangedAt = createdAt;
        public long Version = 1;
        public NCTraumaResponseCallStatus Status = NCTraumaResponseCallStatus.Requested;
        public NCCharacterId? Lead;
        public Dictionary<NCCharacterId, string> Participants = [];
        public string LeadName => Lead is { } lead ? Participants.GetValueOrDefault(lead, string.Empty) : string.Empty;
    }

    private readonly record struct ResponderContext(NCCharacterId CharacterId, string Name, string JobId);
    private readonly record struct OpenShardView(EntityUid Shard, EntityUid Loader, EntityUid User,
        NCCharacterId CharacterId);
}
