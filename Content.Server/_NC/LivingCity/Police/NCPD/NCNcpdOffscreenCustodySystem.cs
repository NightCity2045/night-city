// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.CityServices;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Police;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Police;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>
/// Applies a completed abstract NCPD response to persistent NPC suspects. Player characters and materialized actors
/// are deliberately excluded: their custody must remain physical gameplay.
/// </summary>
public sealed partial class NCNcpdOffscreenCustodySystem : EntitySystem
{
    private static readonly ProtoId<NCNcpdDetentionPolicyPrototype> DefaultPolicy =
        "NCDefaultNcpdDetentionPolicy";
    private static readonly NCCityServiceId NcpdService =
        new(NCPersistentIdentity.CityService("ncpd").PersistentId);
    private static readonly NCPartyRef NcpdParty = NCPartyRef.ForCityService(NcpdService);

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityIncidentRegistrySystem _incidents = default!;
    [Dependency] private NCCityServiceSystem _services = default!;
    [Dependency] private NCPoliceWantedRegistrySystem _wanted = default!;

    private readonly HashSet<NCCityIncidentId> _ncpdOnScene = [];
    private readonly HashSet<NCCityIncidentId> _pending = [];
    private int _generation;
    private bool _active;

    public int PendingCount => _pending.Count;
    public int ImprisonedCount { get; private set; }
    public int WantedCount { get; private set; }
    public int FailedCount { get; private set; }
    public string? LastError { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(_ => Start());
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Clear());
        SubscribeLocalEvent<NCServiceDispatchPersistedEvent>(OnDispatchPersisted);
        SubscribeLocalEvent<NCCityIncidentPersistedEvent>(OnIncidentPersisted);
    }

    private void OnDispatchPersisted(NCServiceDispatchPersistedEvent args)
    {
        var dispatch = args.Dispatch.Snapshot;
        if (!_active || dispatch.ServiceId != NcpdService)
            return;
        if (dispatch.Status == NCServiceDispatchStatus.OnScene)
        {
            _ncpdOnScene.Add(dispatch.IncidentId);
            ObserveRecoveredIncident(dispatch.IncidentId, _generation);
        }
        else if (dispatch.Status is NCServiceDispatchStatus.Completed or NCServiceDispatchStatus.Cancelled or
                 NCServiceDispatchStatus.Rejected)
        {
            _ncpdOnScene.Remove(dispatch.IncidentId);
        }
    }

    private void OnIncidentPersisted(NCCityIncidentPersistedEvent args) => TryQueue(args.Incident);

    private async void ObserveRecoveredIncident(NCCityIncidentId incidentId, int generation)
    {
        NCCityIncidentRecord? incident = null;
        string? error = null;
        try
        {
            incident = await _incidents.GetAsync(incidentId, true);
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }

        _taskManager.RunOnMainThread(() =>
        {
            if (generation != _generation || !_active)
                return;
            if (error != null)
            {
                LastError = error;
                FailedCount++;
                return;
            }
            if (incident != null)
                TryQueue(incident);
        });
    }

    private void TryQueue(NCCityIncidentRecord incident)
    {
        var snapshot = incident.Snapshot;
        if (!_active || !_ncpdOnScene.Contains(snapshot.IncidentId) || _pending.Contains(snapshot.IncidentId) ||
            snapshot.Stage is not (NCCityIncidentStage.Resolved or NCCityIncidentStage.Closed) ||
            snapshot.MaterializationPolicy != NCIncidentMaterializationPolicy.AbstractUnlessObserved ||
            snapshot.Outcome?.OutcomeKey != "abstract-contained" ||
            !_prototypes.TryIndex(DefaultPolicy, out NCNcpdDetentionPolicyPrototype? policy) || policy == null ||
            policy.AbstractCustodyReviewInterval <= TimeSpan.Zero)
        {
            return;
        }

        _pending.Add(snapshot.IncidentId);
        var generation = _generation;
        _ = ApplyAsync(generation, incident, policy.AbstractCustodyReviewInterval);
    }

    private async Task ApplyAsync(int generation, NCCityIncidentRecord incident, TimeSpan reviewInterval)
    {
        var imprisoned = 0;
        var wanted = 0;
        string? error = null;
        try
        {
            if (!_clock.TryGetCurrentTime(out _, out var now))
                throw new InvalidOperationException("The active-world clock is unavailable.");
            var service = await _services.GetServiceAsync(NcpdService, true) ??
                          throw new InvalidOperationException("The NCPD city service is unavailable.");
            var snapshot = incident.Snapshot;
            var sourceWorldEventId = snapshot.Causes.FirstOrDefault()?.SourceWorldEventId ?? Guid.Empty;
            foreach (var party in snapshot.Outcome!.ResponsibleParties.Distinct())
            {
                if (generation != _generation || !_active || !party.TryGetActor(out var actorId) ||
                    _runtime.TryGetEntity(actorId, out var physical) && Exists(physical))
                {
                    continue;
                }

                var actor = await _actors.GetAsync(actorId, true);
                if (actor == null || actor.Snapshot.Lifecycle.Status is
                        (NCCityActorStatus.Dead or NCCityActorStatus.Archived))
                {
                    continue;
                }
                if (actor.Snapshot.Lifecycle.Status == NCCityActorStatus.Imprisoned)
                {
                    await MarkWantedDetainedAsync(snapshot.IncidentId, party);
                    continue;
                }

                var alreadyBooked = actor.Snapshot.Lifecycle.Status == NCCityActorStatus.Arrested &&
                                    actor.Snapshot.Location.CurrentZone == service.Snapshot.BaseZone;
                if (!alreadyBooked && actor.Snapshot.Location.CurrentZone != snapshot.LocationZone)
                {
                    if (sourceWorldEventId != Guid.Empty)
                    {
                        var result = await _wanted.CreateAsync(party, snapshot.IncidentId, sourceWorldEventId,
                            snapshot.Severity, "ncpd-offscreen-suspect-left-scene", NcpdParty,
                            "ncpd-offscreen-resolution");
                        if (result.Status is NCPoliceWantedMutationStatus.Applied or
                            NCPoliceWantedMutationStatus.Duplicate)
                            wanted++;
                    }
                    continue;
                }

                if (!alreadyBooked)
                    actor = await SetStatusAsync(actor, NCCityActorStatus.Arrested, snapshot) ??
                            throw new InvalidOperationException($"Actor {actorId} could not enter abstract arrest.");
                actor = await MoveToHoldingAsync(actor, service.Snapshot.BaseZone, snapshot, now, reviewInterval) ??
                        throw new InvalidOperationException($"Actor {actorId} could not enter abstract holding.");
                actor = await SetStatusAsync(actor, NCCityActorStatus.Imprisoned, snapshot) ??
                        throw new InvalidOperationException($"Actor {actorId} could not complete abstract custody.");
                await MarkWantedDetainedAsync(snapshot.IncidentId, party);
                imprisoned++;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }

        _taskManager.RunOnMainThread(() => Complete(generation, incident.Snapshot.IncidentId, imprisoned, wanted,
            error));
    }

    private async Task<NCCityActorRecord?> SetStatusAsync(
        NCCityActorRecord actor,
        NCCityActorStatus status,
        NCCityIncidentSnapshot incident)
    {
        if (actor.Snapshot.Lifecycle.Status == status)
            return actor;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stable = $"{incident.IncidentId}.{actor.Snapshot.Identity.ActorId}.{status}.{actor.AggregateVersion}";
            var result = await _actors.SetStatusAsync(new NCCityActorStatusMutationRequest(
                NCPersistentIdentity.Command("ncpd-offscreen", "set-status", stable),
                actor.Snapshot.Identity.ActorId,
                actor.AggregateVersion,
                status,
                NCPersistentIdentity.Correlation("ncpd-offscreen", "custody",
                    $"{incident.IncidentId}.{actor.Snapshot.Identity.ActorId}"),
                incident.LastAppliedEventId,
                "ncpd-offscreen-custody"));
            if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
                return result.Record ?? await _actors.GetAsync(actor.Snapshot.Identity.ActorId, true);
            if (result.Status != NCCityActorMutationStatus.VersionConflict ||
                await _actors.GetAsync(actor.Snapshot.Identity.ActorId, true) is not { } reloaded)
                return null;
            actor = reloaded;
        }
        return null;
    }

    private async Task<NCCityActorRecord?> MoveToHoldingAsync(
        NCCityActorRecord actor,
        Content.Shared._NC.City.Zones.NCZoneId holdingZone,
        NCCityIncidentSnapshot incident,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now,
        TimeSpan reviewInterval)
    {
        if (actor.Snapshot.Location.CurrentZone == holdingZone &&
            actor.Snapshot.Schedule.PresenceMode == NCCityActorPresenceMode.AbstractCustody)
            return actor;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var actorId = actor.Snapshot.Identity.ActorId;
            var stable = $"{incident.IncidentId}.{actorId}.{actor.AggregateVersion}";
            var result = await _actors.SetLocationAsync(new NCCityActorLocationMutationRequest(
                NCPersistentIdentity.Command("ncpd-offscreen", "move-holding", stable),
                actorId,
                actor.AggregateVersion,
                holdingZone,
                default,
                NCCityActorTravelPhase.Stationary,
                NCCityTravelMode.None,
                null,
                "ncpd-holding",
                incident.IncidentId.ToString(),
                NCCityActorAvailability.Busy,
                now.Advance(reviewInterval),
                NCPersistentIdentity.Correlation("ncpd-offscreen", "custody",
                    $"{incident.IncidentId}.{actorId}"),
                incident.LastAppliedEventId,
                "ncpd-offscreen-custody",
                NCCityActorPresenceMode.AbstractCustody));
            if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
                return result.Record ?? await _actors.GetAsync(actorId, true);
            if (result.Status != NCCityActorMutationStatus.VersionConflict ||
                await _actors.GetAsync(actorId, true) is not { } reloaded)
                return null;
            actor = reloaded;
        }
        return null;
    }

    private async Task MarkWantedDetainedAsync(NCCityIncidentId incidentId, NCPartyRef subject)
    {
        var wantedId = NCPoliceWantedRegistrySystem.Identity(incidentId, subject);
        var current = await _wanted.GetAsync(wantedId, true);
        if (current?.Snapshot.Status != NCPoliceWantedStatus.Active)
            return;
        await _wanted.SetStatusAsync(wantedId, NCPoliceWantedStatus.Detained, NcpdParty,
            "ncpd-offscreen-custody");
    }

    private void Complete(int generation, NCCityIncidentId incidentId, int imprisoned, int wanted, string? error)
    {
        if (generation != _generation)
            return;
        _pending.Remove(incidentId);
        ImprisonedCount += imprisoned;
        WantedCount += wanted;
        if (error == null)
            return;
        FailedCount++;
        LastError = error;
    }

    private void Start()
    {
        Clear();
        _active = true;
    }

    private void Clear()
    {
        _active = false;
        _generation++;
        _ncpdOnScene.Clear();
        _pending.Clear();
        ImprisonedCount = 0;
        WantedCount = 0;
        FailedCount = 0;
        LastError = null;
    }
}
