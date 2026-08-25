// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.City.Connectivity;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.CityServices;

public enum NCCityServiceMutationStatus : byte { Applied, Duplicate, NotFound, VersionConflict, NotEligible,
    NoRoute, CapacityExceeded, Rejected, StorageFailure }
public sealed record NCCityServiceRecord(NCCityServiceSnapshot Snapshot, long AggregateVersion,
    NCSessionEpoch SessionEpoch, NCActiveWorldTime PersistedAt);
public sealed record NCServiceContractRecord(NCServiceContractSnapshot Snapshot, long AggregateVersion,
    NCSessionEpoch SessionEpoch, NCActiveWorldTime PersistedAt);
public sealed record NCServiceDispatchRecord(NCServiceDispatchSnapshot Snapshot, long AggregateVersion,
    NCSessionEpoch SessionEpoch, NCActiveWorldTime PersistedAt);
public sealed record NCCityServiceMutationResult(NCCityServiceMutationStatus Status, NCCityServiceRecord? Service,
    NCServiceContractRecord? Contract, NCServiceDispatchRecord? Dispatch, string? Error);
public sealed record NCCreateServiceContractRequest(Guid CommandId, NCServiceContractId ContractId,
    NCCityServiceId ServiceId, NCPartyRef Subscriber, int CoverageTier, long MaximumCoveredCost,
    int MaximumThreatBasisPoints, NCActiveWorldTime? EndsAt, NCPartyRef Issuer, Guid CorrelationId,
    Guid? CausationId = null, string AuditSource = "service-contract-create");
public sealed record NCRequestServiceDispatch(Guid CommandId, NCServiceDispatchId DispatchId,
    NCCityServiceId ServiceId, NCCityIncidentId IncidentId, NCServiceContractId? ContractId,
    NCPartyRef Requester, Guid CorrelationId, Guid? CausationId = null,
    string AuditSource = "service-dispatch-request");

/// <summary>
/// Capacity-, route- and contract-aware service dispatch. It stores no runtime team entity; physical response is a
/// group adapter consuming the persistent dispatch.
/// </summary>
public sealed partial class NCCityServiceSystem : EntitySystem
{
    public const string ServiceAggregateType = "living-city.city-service";
    public const string ContractAggregateType = "living-city.service-contract";
    public const string DispatchAggregateType = "living-city.service-dispatch";
    public const int SnapshotFormatVersion = 1;
    private const int RecoveryPageSize = 100;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyDirectorySystem _directory = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    [Dependency] private NCCityConnectivitySystem _city = default!;
    [Dependency] private NCCityIncidentRegistrySystem _incidents = default!;

    private readonly ConcurrentDictionary<NCCityServiceId, NCCityServiceRecord> _services = [];
    private readonly ConcurrentDictionary<NCServiceContractId, NCServiceContractRecord> _contracts = [];
    private readonly ConcurrentDictionary<NCServiceDispatchId, NCServiceDispatchRecord> _dispatches = [];
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly PriorityQueue<NCServiceDispatchId, long> _due = new();
    private readonly HashSet<NCServiceDispatchId> _queued = [];
    private readonly HashSet<NCServiceDispatchId> _processing = [];
    private readonly List<NCCityRouteStep> _route = [];
    private bool _active;

    public int CachedServiceCount => _services.Count;
    public int CachedContractCount => _contracts.Count;
    public int CachedDispatchCount => _dispatches.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(_ => _active = false);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => ClearRuntime());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => ClearRuntime());
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || !_clock.TryGetCurrentTime(out _, out var now))
            return;
        var count = 0;
        while (count < 8 && _due.TryPeek(out var id, out var ticks) && ticks <= now.Ticks)
        {
            _due.Dequeue();
            _queued.Remove(id);
            if (_processing.Add(id))
                _ = ArriveAsync(id);
            count++;
        }
    }

    public Task<NCCityServiceRecord?> GetServiceAsync(NCCityServiceId id, bool forceReload = false) =>
        GetAsync(id, ServiceAggregateType, _services, DeserializeService, forceReload);
    public Task<NCServiceContractRecord?> GetContractAsync(NCServiceContractId id, bool forceReload = false) =>
        GetAsync(id, ContractAggregateType, _contracts, DeserializeContract, forceReload);
    public Task<NCServiceDispatchRecord?> GetDispatchAsync(NCServiceDispatchId id, bool forceReload = false) =>
        GetAsync(id, DispatchAggregateType, _dispatches, DeserializeDispatch, forceReload);

    /// <summary>
    /// Rehydrates one dispatch into the active-time scheduler. Queued work is promoted only through the canonical
    /// capacity transition, while an en-route dispatch is merely re-enqueued at its persisted due time.
    /// </summary>
    public async Task<NCServiceDispatchRecord?> RecoverDispatchAsync(NCServiceDispatchId id)
    {
        if (!_active || !id.IsValid)
            return null;
        var dispatch = await GetDispatchAsync(id, true);
        if (dispatch == null)
            return null;
        if (dispatch.Snapshot.Status == NCServiceDispatchStatus.EnRoute &&
            dispatch.Snapshot.ExpectedArrivalAt is { } due)
        {
            Enqueue(id, due);
        }
        else if (dispatch.Snapshot.Status == NCServiceDispatchStatus.Queued)
        {
            var service = await GetServiceAsync(dispatch.Snapshot.ServiceId, true);
            if (service != null)
                await PromoteQueueAsync(service);
            dispatch = await GetDispatchAsync(id, true) ?? dispatch;
        }
        RaiseLocalEvent(new NCServiceDispatchPersistedEvent(dispatch));
        return dispatch;
    }

    public async Task<NCCityServiceMutationResult> CreateContractAsync(NCCreateServiceContractRequest request)
    {
        if (request.CommandId == Guid.Empty || !request.ContractId.IsValid || !request.ServiceId.IsValid ||
            !request.Subscriber.IsValid || request.CoverageTier is < 1 or > 10 || request.MaximumCoveredCost < 0 ||
            request.MaximumThreatBasisPoints is < 0 or > 10_000 || !request.Issuer.IsValid ||
            request.CorrelationId == Guid.Empty || !_clock.TryGetCurrentTime(out var epoch, out var now) ||
            request.EndsAt is { } end && end <= now)
            return Reject("The service contract request is invalid.");
        var service = await GetServiceAsync(request.ServiceId, true);
        if (service == null || request.Issuer != service.Snapshot.ServiceParty ||
            !(await _parties.ResolveAsync(request.Subscriber, true)).IsResolved)
            return new(NCCityServiceMutationStatus.NotEligible, service, null, null,
                "The service, subscriber, or issuer is unavailable.");
        var gate = Gate(request.ContractId.Value);
        await gate.WaitAsync();
        try
        {
            var existing = await GetContractAsync(request.ContractId, true);
            if (existing != null)
                return existing.Snapshot.ServiceId == request.ServiceId &&
                    existing.Snapshot.Subscriber == request.Subscriber
                    ? new(NCCityServiceMutationStatus.Duplicate, service, existing, null, null)
                    : Reject("The contract identity belongs to different terms.");
            var eventId = Guid.NewGuid();
            var snapshot = new NCServiceContractSnapshot(request.ContractId, request.ServiceId, request.Subscriber,
                request.CoverageTier, request.MaximumCoveredCost, request.MaximumThreatBasisPoints, now,
                request.EndsAt, true, 1, eventId);
            var result = await Persist(request.CommandId, "living-city.service-contract.create", "service-contract",
                request.ContractId.ToString(), [Write(ContractAggregateType, request.ContractId.ToString(), 0,
                    snapshot, eventId, "living-city.service-contract.created", request.AuditSource)],
                request.CorrelationId, request.CausationId, epoch, now);
            if (!Committed(result, out var status))
                return Reject(result.Error ?? "The service contract was not committed.");
            var loaded = await GetContractAsync(request.ContractId, true);
            return loaded == null ? Reject("The service contract could not reload.") :
                new(status, service, loaded, null, null);
        }
        finally { gate.Release(); }
    }

    public async Task<NCCityServiceMutationResult> RequestDispatchAsync(NCRequestServiceDispatch request)
    {
        if (request.CommandId == Guid.Empty || !request.DispatchId.IsValid || !request.ServiceId.IsValid ||
            !request.IncidentId.IsValid || !request.Requester.IsValid || request.CorrelationId == Guid.Empty ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject("The dispatch request is invalid.");
        var service = await GetServiceAsync(request.ServiceId, true);
        var incident = await _incidents.GetAsync(request.IncidentId, true);
        if (service == null || incident == null || !TryPolicy(service.Snapshot.Kind, out var policy))
            return new(NCCityServiceMutationStatus.NotFound, service, null, null,
                "The service, incident, or service policy is unavailable.");
        if (incident.Snapshot.Severity < policy.SeverityThreshold)
            return new(NCCityServiceMutationStatus.NotEligible, service, null, null,
                "The incident is below the service response threshold.");
        var threat = Math.Clamp(incident.Snapshot.Severity * 10, 0, 10_000);
        var contract = request.ContractId is { } contractId
            ? await GetContractAsync(contractId, true)
            : null;
        if (policy.RequiresContract && (contract == null || !contract.Snapshot.Active ||
                contract.Snapshot.ServiceId != request.ServiceId || contract.Snapshot.Subscriber != request.Requester ||
                contract.Snapshot.StartsAt > now || contract.Snapshot.EndsAt is { } end && end <= now ||
                threat > contract.Snapshot.MaximumThreatBasisPoints))
            return new(NCCityServiceMutationStatus.NotEligible, service, contract, null,
                "The requester has no active contract covering this threat.");
        if (threat > service.Snapshot.Capacity.MaximumThreatBasisPoints ||
            !_city.TryFindRoute(service.Snapshot.BaseZone, incident.Snapshot.LocationZone,
                policy.TravelMode, _route))
            return new(NCCityServiceMutationStatus.NoRoute, service, contract, null,
                "No permitted response route exists or the threat is above the service limit.");
        var gates = await AcquireGates(request.ServiceId.Value, request.DispatchId.Value);
        try
        {
            service = await GetServiceAsync(request.ServiceId, true);
            var existing = await GetDispatchAsync(request.DispatchId, true);
            if (existing != null)
                return existing.Snapshot.IncidentId == request.IncidentId
                    ? new(NCCityServiceMutationStatus.Duplicate, service, contract, existing, null)
                    : Reject("The dispatch identity belongs to another incident.");
            if (service == null)
                return Reject("The service disappeared.");
            var hasTeam = service.Snapshot.Capacity.AvailableTeams > 0;
            if (!hasTeam && service.Snapshot.Queue.Count >= service.Snapshot.Capacity.MaximumQueue)
                return new(NCCityServiceMutationStatus.CapacityExceeded, service, contract, null,
                    "The service queue is full.");
            var eta = CalculateEta(_route, policy);
            var dispatchEvent = Guid.NewGuid();
            var dispatch = new NCServiceDispatchSnapshot(request.DispatchId, request.ServiceId, request.IncidentId,
                request.ContractId, request.Requester, hasTeam ? NCServiceDispatchStatus.EnRoute :
                    NCServiceDispatchStatus.Queued, service.Snapshot.BaseZone, incident.Snapshot.LocationZone,
                policy.TravelMode, threat, RouteZones(service.Snapshot.BaseZone, _route), now,
                hasTeam ? now.Advance(eta) : null, null, hasTeam ? "team-assigned" : "queued-for-capacity",
                1, dispatchEvent);
            var serviceEvent = Guid.NewGuid();
            var nextService = service.Snapshot with
            {
                Capacity = service.Snapshot.Capacity with
                {
                    AvailableTeams = hasTeam ? service.Snapshot.Capacity.AvailableTeams - 1 :
                        service.Snapshot.Capacity.AvailableTeams,
                },
                Queue = hasTeam ? service.Snapshot.Queue : service.Snapshot.Queue.Append(request.DispatchId).ToArray(),
                ActiveDispatches = hasTeam ? service.Snapshot.ActiveDispatches.Append(request.DispatchId).ToArray() :
                    service.Snapshot.ActiveDispatches,
                DataVersion = service.Snapshot.DataVersion + 1,
                LastAppliedEventId = serviceEvent,
            };
            var result = await Persist(request.CommandId, "living-city.service-dispatch.create", "dispatch",
                request.DispatchId.ToString(),
                [
                    Write(ServiceAggregateType, request.ServiceId.ToString(), service.AggregateVersion, nextService,
                        serviceEvent, "living-city.city-service.dispatch-accepted", request.AuditSource),
                    Write(DispatchAggregateType, request.DispatchId.ToString(), 0, dispatch, dispatchEvent,
                        "living-city.service-dispatch.created", request.AuditSource),
                ], request.CorrelationId, request.CausationId, epoch, now);
            if (!Committed(result, out var status))
                return Reject(result.Error ?? "The service dispatch was not committed.");
            var loadedService = await GetServiceAsync(request.ServiceId, true);
            var loadedDispatch = await GetDispatchAsync(request.DispatchId, true);
            if (loadedService == null || loadedDispatch == null)
                return Reject("The committed dispatch could not reload.");
            await _incidents.AttachDispatchAsync(request.IncidentId, incident.AggregateVersion,
                request.DispatchId, loadedService.Snapshot.ServiceParty);
            var attachedIncident = await _incidents.GetAsync(request.IncidentId, true);
            if (hasTeam && attachedIncident?.Snapshot.Stage == NCCityIncidentStage.Dispatching)
            {
                var stable = $"{request.IncidentId}.{request.DispatchId}";
                await _incidents.ChangeStageAsync(new NCChangeIncidentStageRequest(
                    NCPersistentIdentity.Command("incident", "response-en-route", stable), request.IncidentId,
                    attachedIncident.AggregateVersion, NCCityIncidentStage.ResponseEnRoute,
                    loadedService.Snapshot.ServiceParty, loadedDispatch.Snapshot.ExpectedArrivalAt,
                    NCPersistentIdentity.Correlation("incident", "response-en-route", stable),
                    loadedDispatch.Snapshot.LastAppliedEventId, "city-service-dispatch"));
            }
            if (loadedDispatch.Snapshot.ExpectedArrivalAt is { } due)
                Enqueue(request.DispatchId, due);
            RaiseLocalEvent(new NCServiceDispatchPersistedEvent(loadedDispatch));
            return new(status, loadedService, contract, loadedDispatch, null);
        }
        finally { ReleaseGates(gates); }
    }

    public async Task<NCCityServiceMutationResult> CompleteDispatchAsync(NCServiceDispatchId id,
        long expectedVersion, NCPartyRef actor)
    {
        var dispatch = await GetDispatchAsync(id, true);
        if (dispatch == null)
            return new(NCCityServiceMutationStatus.NotFound, null, null, null, "The dispatch does not exist.");
        var service = await GetServiceAsync(dispatch.Snapshot.ServiceId, true);
        if (service == null || dispatch.AggregateVersion != expectedVersion || actor != service.Snapshot.ServiceParty ||
            dispatch.Snapshot.Status is not (NCServiceDispatchStatus.OnScene or NCServiceDispatchStatus.Rejected or
                NCServiceDispatchStatus.Cancelled) || !_clock.TryGetCurrentTime(out var epoch, out var now))
            return new(NCCityServiceMutationStatus.Rejected, service, null, dispatch,
                "The dispatch cannot be completed by this actor.");
        var gates = await AcquireGates(service.Snapshot.ServiceId.Value, id.Value);
        try
        {
            var eventId = Guid.NewGuid();
            var nextDispatch = dispatch.Snapshot with { Status = NCServiceDispatchStatus.Completed,
                DataVersion = dispatch.Snapshot.DataVersion + 1, LastAppliedEventId = eventId };
            var serviceEvent = Guid.NewGuid();
            var nextService = service.Snapshot with
            {
                Capacity = service.Snapshot.Capacity with { AvailableTeams = Math.Min(
                    service.Snapshot.Capacity.TotalTeams, service.Snapshot.Capacity.AvailableTeams + 1) },
                ActiveDispatches = service.Snapshot.ActiveDispatches.Where(value => value != id).ToArray(),
                DataVersion = service.Snapshot.DataVersion + 1,
                LastAppliedEventId = serviceEvent,
            };
            var stable = $"{id}.{expectedVersion}";
            var result = await Persist(NCPersistentIdentity.Command("service", "complete-dispatch", stable),
                "living-city.service-dispatch.complete", "dispatch", id.ToString(),
                [
                    Write(ServiceAggregateType, service.Snapshot.ServiceId.ToString(), service.AggregateVersion,
                        nextService, serviceEvent, "living-city.city-service.team-released",
                        "service-dispatch-complete"),
                    Write(DispatchAggregateType, id.ToString(), dispatch.AggregateVersion, nextDispatch, eventId,
                        "living-city.service-dispatch.completed", "service-dispatch-complete"),
                ], NCPersistentIdentity.Correlation("service", "complete-dispatch", stable),
                dispatch.Snapshot.LastAppliedEventId, epoch, now);
            if (!Committed(result, out var status))
                return Reject(result.Error ?? "The dispatch completion failed.");
            var loadedService = await GetServiceAsync(service.Snapshot.ServiceId, true);
            var loadedDispatch = await GetDispatchAsync(id, true);
            if (loadedService != null)
                _ = PromoteQueueAsync(loadedService);
            return new(status, loadedService, null, loadedDispatch, null);
        }
        finally { ReleaseGates(gates); }
    }

    internal async Task<NCCityServiceMutationResult> BindResponseGroupAsync(NCServiceDispatchId id,
        long expectedVersion, NCGroupId groupId, NCPartyRef actor)
    {
        var dispatch = await GetDispatchAsync(id, true);
        if (dispatch == null)
            return new(NCCityServiceMutationStatus.NotFound, null, null, null, "The dispatch does not exist.");
        var service = await GetServiceAsync(dispatch.Snapshot.ServiceId, true);
        if (service == null || actor != service.Snapshot.ServiceParty || !groupId.IsValid ||
            dispatch.AggregateVersion != expectedVersion || dispatch.Snapshot.Status is
                (NCServiceDispatchStatus.Completed or NCServiceDispatchStatus.Cancelled or
                    NCServiceDispatchStatus.Rejected) || !_clock.TryGetCurrentTime(out var epoch, out var now))
            return new(NCCityServiceMutationStatus.Rejected, service, null, dispatch,
                "The response group cannot be bound to this dispatch.");
        if (dispatch.Snapshot.ResponseGroupId is { } existing)
            return existing == groupId.Value
                ? new(NCCityServiceMutationStatus.Duplicate, service, null, dispatch, null)
                : new(NCCityServiceMutationStatus.Rejected, service, null, dispatch,
                    "The dispatch already has another response group.");

        var gate = Gate(id.Value);
        await gate.WaitAsync();
        try
        {
            dispatch = await GetDispatchAsync(id, true);
            if (dispatch == null || dispatch.AggregateVersion != expectedVersion)
                return new(NCCityServiceMutationStatus.VersionConflict, service, null, dispatch,
                    "The dispatch version changed.");
            var eventId = Guid.NewGuid();
            var next = dispatch.Snapshot with { ResponseGroupId = groupId.Value,
                DataVersion = dispatch.Snapshot.DataVersion + 1, LastAppliedEventId = eventId };
            var stable = $"{id}.{groupId}";
            var result = await Persist(NCPersistentIdentity.Command("service", "bind-group", stable),
                "living-city.service-dispatch.bind-group", "dispatch", id.ToString(),
                [Write(DispatchAggregateType, id.ToString(), dispatch.AggregateVersion, next, eventId,
                    "living-city.service-dispatch.group-bound", "service-group-adapter")],
                NCPersistentIdentity.Correlation("service", "bind-group", stable),
                dispatch.Snapshot.LastAppliedEventId, epoch, now);
            if (!Committed(result, out var status))
                return Reject(result.Error ?? "The response group binding failed.");
            var loaded = await GetDispatchAsync(id, true);
            if (loaded != null)
                RaiseLocalEvent(new NCServiceDispatchPersistedEvent(loaded));
            return new(status, service, null, loaded, null);
        }
        finally { gate.Release(); }
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        _active = true;
        _due.Clear();
        _queued.Clear();
        _processing.Clear();
        _ = BootstrapAndRecoverAsync(args.Time);
    }

    private async Task BootstrapAndRecoverAsync(NCActiveWorldTime now)
    {
        foreach (var prototype in _prototypes.EnumeratePrototypes<NCCityServicePrototype>())
            await EnsureServiceAsync(prototype);
        string? after = null;
        while (_active)
        {
            var page = await _database.GetNCAggregateSnapshotPageAsync(DispatchAggregateType, after, RecoveryPageSize);
            foreach (var stored in page)
            {
                if (!Guid.TryParse(stored.AggregateId, out var raw))
                    continue;
                var dispatch = await GetDispatchAsync(new NCServiceDispatchId(raw), true);
                if (dispatch?.Snapshot.Status == NCServiceDispatchStatus.EnRoute &&
                    dispatch.Snapshot.ExpectedArrivalAt is { } due)
                {
                    Enqueue(dispatch.Snapshot.DispatchId, due);
                    // Re-publish active work so service-specific adapters rebuild their round-local execution state.
                    RaiseLocalEvent(new NCServiceDispatchPersistedEvent(dispatch));
                }
                else if (dispatch?.Snapshot.Status == NCServiceDispatchStatus.OnScene)
                {
                    RaiseLocalEvent(new NCServiceDispatchPersistedEvent(dispatch));
                }
            }
            if (page.Count < RecoveryPageSize)
                break;
            after = page[^1].AggregateId;
        }
        foreach (var service in _services.Values)
        {
            if (service.Snapshot.Capacity.AvailableTeams > 0 && service.Snapshot.Queue.Count > 0)
                _ = PromoteQueueAsync(service);
        }
    }

    private async Task EnsureServiceAsync(NCCityServicePrototype prototype)
    {
        if (!_prototypes.TryIndex(prototype.Policy, out NCCityServicePolicyPrototype? policy))
            return;
        var party = NCPersistentIdentity.CityService(prototype.StableKey);
        await _directory.RegisterAsync(new NCPartyDirectoryRegisterRequest(
            NCPersistentIdentity.Command("city-service", "register-party", prototype.StableKey), party,
            prototype.DisplayName,
            NCPersistentIdentity.Correlation("city-service", "register-party", prototype.StableKey),
            AuditSource: "city-service-bootstrap"));
        var serviceId = new NCCityServiceId(party.PersistentId);
        if (await GetServiceAsync(serviceId, true) != null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return;
        var eventId = Guid.NewGuid();
        var snapshot = new NCCityServiceSnapshot(serviceId, prototype.StableKey, prototype.DisplayName, policy.Kind,
            party, prototype.FactionId, prototype.BaseZone, new NCServiceCapacity(policy.DefaultTeams,
                policy.DefaultTeams, policy.MaximumQueue, policy.MaximumThreatBasisPoints), [], [], 1, eventId);
        var result = await Persist(NCPersistentIdentity.Command("city-service", "register", prototype.StableKey),
            "living-city.city-service.create", "city-service", serviceId.ToString(),
            [Write(ServiceAggregateType, serviceId.ToString(), 0, snapshot, eventId,
                "living-city.city-service.created", "city-service-bootstrap")],
            NCPersistentIdentity.Correlation("city-service", "register", prototype.StableKey), null, epoch, now);
        if (Committed(result, out _))
            await GetServiceAsync(serviceId, true);
    }

    private async Task ArriveAsync(NCServiceDispatchId id)
    {
        try
        {
            var dispatch = await GetDispatchAsync(id, true);
            if (dispatch == null || dispatch.Snapshot.Status != NCServiceDispatchStatus.EnRoute ||
                !_clock.TryGetCurrentTime(out var epoch, out var now))
                return;
            if (dispatch.Snapshot.ExpectedArrivalAt is { } future && future > now)
            {
                Enqueue(id, future);
                return;
            }
            // NC: a physical service adapter may require a factual arrival instead of accepting ETA as proof.
            var arrivalAttempt = new NCServiceDispatchArrivalAttemptEvent(dispatch, now);
            RaiseLocalEvent(arrivalAttempt);
            if (arrivalAttempt.Deferred)
            {
                Enqueue(id, now.Advance(arrivalAttempt.RetryAfter > TimeSpan.Zero
                    ? arrivalAttempt.RetryAfter
                    : TimeSpan.FromSeconds(1)));
                return;
            }
            var eventId = Guid.NewGuid();
            var next = dispatch.Snapshot with { Status = NCServiceDispatchStatus.OnScene,
                DataVersion = dispatch.Snapshot.DataVersion + 1, LastAppliedEventId = eventId };
            var stable = $"{id}.{dispatch.AggregateVersion}";
            var result = await Persist(NCPersistentIdentity.Command("service", "arrive", stable),
                "living-city.service-dispatch.arrive", "dispatch", id.ToString(),
                [Write(DispatchAggregateType, id.ToString(), dispatch.AggregateVersion, next, eventId,
                    "living-city.service-dispatch.on-scene", "service-active-time-arrival")],
                NCPersistentIdentity.Correlation("service", "arrive", stable),
                dispatch.Snapshot.LastAppliedEventId, epoch, now);
            if (Committed(result, out _))
            {
                var loaded = await GetDispatchAsync(id, true);
                if (loaded != null)
                    RaiseLocalEvent(new NCServiceDispatchPersistedEvent(loaded));
                var incident = await _incidents.GetAsync(dispatch.Snapshot.IncidentId, true);
                if (incident?.Snapshot.Stage == NCCityIncidentStage.ResponseEnRoute)
                    await _incidents.ChangeStageAsync(new NCChangeIncidentStageRequest(
                        NCPersistentIdentity.Command("incident", "response-arrived", stable),
                        incident.Snapshot.IncidentId, incident.AggregateVersion, NCCityIncidentStage.Active,
                        (await GetServiceAsync(dispatch.Snapshot.ServiceId, true))!.Snapshot.ServiceParty, null,
                        NCPersistentIdentity.Correlation("incident", "response-arrived", stable),
                        dispatch.Snapshot.LastAppliedEventId, "service-response-arrival"));
            }
        }
        finally { _processing.Remove(id); }
    }

    private async Task PromoteQueueAsync(NCCityServiceRecord service)
    {
        if (!_active || service.Snapshot.Capacity.AvailableTeams <= 0 || service.Snapshot.Queue.Count == 0 ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || !TryPolicy(service.Snapshot.Kind, out var policy))
            return;
        var dispatchId = service.Snapshot.Queue[0];
        var dispatch = await GetDispatchAsync(dispatchId, true);
        if (dispatch == null || dispatch.Snapshot.Status != NCServiceDispatchStatus.Queued ||
            !_city.TryFindRoute(service.Snapshot.BaseZone, dispatch.Snapshot.DestinationZone,
                policy.TravelMode, _route))
            return;
        var gates = await AcquireGates(service.Snapshot.ServiceId.Value, dispatchId.Value);
        try
        {
            service = await GetServiceAsync(service.Snapshot.ServiceId, true) ?? service;
            dispatch = await GetDispatchAsync(dispatchId, true) ?? dispatch;
            if (service.Snapshot.Capacity.AvailableTeams <= 0 || service.Snapshot.Queue.FirstOrDefault() != dispatchId ||
                dispatch.Snapshot.Status != NCServiceDispatchStatus.Queued)
                return;
            var eta = CalculateEta(_route, policy);
            var dispatchEvent = Guid.NewGuid();
            var nextDispatch = dispatch.Snapshot with
            {
                Status = NCServiceDispatchStatus.EnRoute,
                ExpectedArrivalAt = now.Advance(eta),
                DecisionReason = "capacity-released",
                DataVersion = dispatch.Snapshot.DataVersion + 1,
                LastAppliedEventId = dispatchEvent,
            };
            var serviceEvent = Guid.NewGuid();
            var nextService = service.Snapshot with
            {
                Capacity = service.Snapshot.Capacity with
                    { AvailableTeams = service.Snapshot.Capacity.AvailableTeams - 1 },
                Queue = service.Snapshot.Queue.Skip(1).ToArray(),
                ActiveDispatches = service.Snapshot.ActiveDispatches.Append(dispatchId).ToArray(),
                DataVersion = service.Snapshot.DataVersion + 1,
                LastAppliedEventId = serviceEvent,
            };
            var stable = $"{dispatchId}.{dispatch.AggregateVersion}";
            var result = await Persist(NCPersistentIdentity.Command("service", "promote-dispatch", stable),
                "living-city.service-dispatch.promote", "dispatch", dispatchId.ToString(),
                [
                    Write(ServiceAggregateType, service.Snapshot.ServiceId.ToString(), service.AggregateVersion,
                        nextService, serviceEvent, "living-city.city-service.queue-promoted", "service-queue"),
                    Write(DispatchAggregateType, dispatchId.ToString(), dispatch.AggregateVersion, nextDispatch,
                        dispatchEvent, "living-city.service-dispatch.en-route", "service-queue"),
                ], NCPersistentIdentity.Correlation("service", "promote-dispatch", stable),
                dispatch.Snapshot.LastAppliedEventId, epoch, now);
            if (!Committed(result, out _))
                return;
            await GetServiceAsync(service.Snapshot.ServiceId, true);
            var loaded = await GetDispatchAsync(dispatchId, true);
            if (loaded != null)
            {
                Enqueue(dispatchId, loaded.Snapshot.ExpectedArrivalAt!.Value);
                RaiseLocalEvent(new NCServiceDispatchPersistedEvent(loaded));
                var incident = await _incidents.GetAsync(loaded.Snapshot.IncidentId, true);
                if (incident?.Snapshot.Stage == NCCityIncidentStage.Dispatching)
                    await _incidents.ChangeStageAsync(new NCChangeIncidentStageRequest(
                        NCPersistentIdentity.Command("incident", "queued-response-en-route", stable),
                        incident.Snapshot.IncidentId, incident.AggregateVersion,
                        NCCityIncidentStage.ResponseEnRoute, service.Snapshot.ServiceParty,
                        loaded.Snapshot.ExpectedArrivalAt,
                        NCPersistentIdentity.Correlation("incident", "queued-response-en-route", stable),
                        loaded.Snapshot.LastAppliedEventId, "service-queue-promotion"));
            }
        }
        finally { ReleaseGates(gates); }
    }

    private static TimeSpan CalculateEta(IReadOnlyList<NCCityRouteStep> route, NCCityServicePolicyPrototype policy)
    {
        var seconds = route.Sum(value => Math.Max(1f, value.Cost));
        return TimeSpan.FromSeconds(Math.Clamp(seconds, policy.MinimumEta.TotalSeconds,
            policy.MaximumEta.TotalSeconds));
    }
    private static NCZoneId[] RouteZones(NCZoneId origin, IReadOnlyList<NCCityRouteStep> route) =>
        route.Aggregate(new List<NCZoneId> { origin }, (list, step) => { list.Add(step.ToZone); return list; })
            .ToArray();
    private bool TryPolicy(NCCityServiceKind kind, out NCCityServicePolicyPrototype policy)
    {
        policy = _prototypes.EnumeratePrototypes<NCCityServicePolicyPrototype>()
            .FirstOrDefault(value => value.Kind == kind)!;
        return policy != null && policy.DefaultTeams > 0 && policy.MaximumQueue > 0 &&
            policy.MaximumThreatBasisPoints is >= 0 and <= 10_000 && policy.MinimumEta > TimeSpan.Zero &&
            policy.MaximumEta >= policy.MinimumEta;
    }
    private void ClearRuntime()
    {
        _active = false;
        _due.Clear();
        _queued.Clear();
        _processing.Clear();
    }
    private void Enqueue(NCServiceDispatchId id, NCActiveWorldTime due)
    {
        if (_active && _queued.Add(id))
            _due.Enqueue(id, due.Ticks);
    }

    private Task<TRecord?> GetAsync<TId, TRecord>(TId id, string aggregateType,
        ConcurrentDictionary<TId, TRecord> cache, Func<NCAggregateSnapshotData?, TId, TRecord?> deserialize,
        bool forceReload) where TId : notnull where TRecord : class =>
        !forceReload && cache.TryGetValue(id, out var cached) ? Task.FromResult<TRecord?>(cached) :
            LoadAsync(id, aggregateType, cache, deserialize);
    private async Task<TRecord?> LoadAsync<TId, TRecord>(TId id, string aggregateType,
        ConcurrentDictionary<TId, TRecord> cache, Func<NCAggregateSnapshotData?, TId, TRecord?> deserialize)
        where TId : notnull where TRecord : class
    {
        var stored = await _database.GetNCAggregateSnapshotAsync(aggregateType, id.ToString()!);
        var loaded = deserialize(stored, id);
        if (loaded != null)
            cache[id] = loaded;
        return loaded;
    }
    private Task<NCPersistentBatchMutationResult> Persist(Guid commandId, string commandType, string scopeType,
        string scopeId, IReadOnlyList<NCPersistentAggregateWrite> writes, Guid correlationId, Guid? causationId,
        NCSessionEpoch epoch, NCActiveWorldTime now) => _database.ApplyNCPersistentBatchMutationAsync(
        new NCPersistentBatchMutationRequest(commandId, commandType, scopeType, scopeId, writes,
            JsonSerializer.Serialize(new ServiceReceipt(scopeId), JsonOptions), correlationId, causationId,
            epoch, now, []));
    private static NCPersistentAggregateWrite Write<T>(string aggregateType, string aggregateId,
        long expectedVersion, T snapshot, Guid eventId, string eventType, string auditSource) => new(eventId,
        eventType, aggregateType, aggregateId, expectedVersion, SnapshotFormatVersion,
        JsonSerializer.Serialize(snapshot, JsonOptions), JsonSerializer.Serialize(new ServiceEvent(aggregateId,
            auditSource), JsonOptions));
    private async Task<SemaphoreSlim[]> AcquireGates(params Guid[] ids)
    {
        var gates = ids.Select(Gate).Distinct().OrderBy(gate => Array.IndexOf(_gates, gate)).ToArray();
        foreach (var gate in gates) await gate.WaitAsync();
        return gates;
    }
    private static void ReleaseGates(IReadOnlyList<SemaphoreSlim> gates)
    {
        for (var index = gates.Count - 1; index >= 0; index--) gates[index].Release();
    }
    private SemaphoreSlim Gate(Guid id) => _gates[(id.GetHashCode() & int.MaxValue) % _gates.Length];
    private static bool Committed(NCPersistentBatchMutationResult result, out NCCityServiceMutationStatus status)
    {
        status = result.Status == NCPersistentMutationStatus.Applied ? NCCityServiceMutationStatus.Applied :
            NCCityServiceMutationStatus.Duplicate;
        return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted;
    }
    private static NCCityServiceRecord? DeserializeService(NCAggregateSnapshotData? stored, NCCityServiceId id) =>
        Deserialize<NCCityServiceSnapshot, NCCityServiceRecord>(stored, value => value.ServiceId == id,
            value => new NCCityServiceRecord(value,
            stored!.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime));
    private static NCServiceContractRecord? DeserializeContract(NCAggregateSnapshotData? stored,
        NCServiceContractId id) => Deserialize<NCServiceContractSnapshot, NCServiceContractRecord>(stored,
        value => value.ContractId == id,
        value => new NCServiceContractRecord(value, stored!.AggregateVersion, stored.SessionEpoch,
            stored.ActiveWorldTime));
    private static NCServiceDispatchRecord? DeserializeDispatch(NCAggregateSnapshotData? stored,
        NCServiceDispatchId id) => Deserialize<NCServiceDispatchSnapshot, NCServiceDispatchRecord>(stored,
        value => value.DispatchId == id,
        value => new NCServiceDispatchRecord(value, stored!.AggregateVersion, stored.SessionEpoch,
            stored.ActiveWorldTime));
    private static TRecord? Deserialize<TSnapshot, TRecord>(NCAggregateSnapshotData? stored,
        Func<TSnapshot, bool> valid, Func<TSnapshot, TRecord> create) where TRecord : class
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion) return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<TSnapshot>(stored.Payload, JsonOptions);
            return snapshot != null && valid(snapshot) ? create(snapshot) : null;
        }
        catch (JsonException) { return null; }
    }
    private static NCCityServiceMutationResult Reject(string error) =>
        new(NCCityServiceMutationStatus.Rejected, null, null, null, error);
    private sealed record ServiceReceipt(string SubjectId);
    private sealed record ServiceEvent(string SubjectId, string AuditSource);
}

public sealed class NCServiceDispatchPersistedEvent(NCServiceDispatchRecord dispatch) : EntityEventArgs
{
    public NCServiceDispatchRecord Dispatch { get; } = dispatch;
}

/// <summary>
/// Allows a physical responder adapter to defer the ETA transition until its real unit has arrived. Deferral is
/// round-local and bounded by the adapter retry policy; no persistence write occurs for a failed arrival probe.
/// </summary>
public sealed class NCServiceDispatchArrivalAttemptEvent(
    NCServiceDispatchRecord dispatch,
    NCActiveWorldTime attemptedAt) : EntityEventArgs
{
    public NCServiceDispatchRecord Dispatch { get; } = dispatch;
    public NCActiveWorldTime AttemptedAt { get; } = attemptedAt;
    public bool Deferred { get; set; }
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(1);
    public string Reason { get; set; } = string.Empty;
}
