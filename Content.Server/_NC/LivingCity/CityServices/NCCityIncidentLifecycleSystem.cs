// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Database;
using Content.Server._NC.City.ZoneTemplates;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.CityServices;

/// <summary>
/// Advances incident deadlines only against the active-world clock. Observed physical scenes are never resolved by
/// the abstract simulator; they remain authoritative gameplay until a responder or another domain reports outcome.
/// </summary>
public sealed partial class NCCityIncidentLifecycleSystem : EntitySystem
{
    private const int RecoveryPageSize = 100;
    private static readonly ProtoId<NCIncidentPolicyPrototype> DefaultPolicy = "Default";

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityIncidentRegistrySystem _incidents = default!;
    [Dependency] private NCCityServiceSystem _services = default!;
    [Dependency] private NCZoneOccupancySystem _occupancy = default!;

    private readonly PriorityQueue<NCCityIncidentId, long> _due = new();
    private readonly HashSet<NCCityIncidentId> _queued = [];
    private readonly HashSet<NCCityIncidentId> _processing = [];
    private bool _active;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(_ => _active = false);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Clear());
        SubscribeLocalEvent<NCCityIncidentPersistedEvent>(OnIncidentPersisted);
        SubscribeLocalEvent<NCServiceDispatchPersistedEvent>(OnDispatchPersisted);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || !_clock.TryGetCurrentTime(out _, out var now) ||
            !_prototypes.TryIndex(DefaultPolicy, out NCIncidentPolicyPrototype? policy))
            return;

        var advanced = 0;
        while (advanced < policy.MaximumAdvancesPerTick &&
            _due.TryPeek(out var incidentId, out var ticks) && ticks <= now.Ticks)
        {
            _due.Dequeue();
            _queued.Remove(incidentId);
            if (_processing.Add(incidentId))
                _ = AdvanceAsync(incidentId);
            advanced++;
        }
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Clear();
        _active = true;
        _ = RecoverAsync();
    }

    private void OnIncidentPersisted(NCCityIncidentPersistedEvent args)
    {
        var snapshot = args.Incident.Snapshot;
        if (snapshot.EscalatesAt is { } due && snapshot.Stage is not
            (NCCityIncidentStage.Closed or NCCityIncidentStage.Cancelled))
            Enqueue(snapshot.IncidentId, due);
    }

    private void OnDispatchPersisted(NCServiceDispatchPersistedEvent args)
    {
        if (args.Dispatch.Snapshot.Status == NCServiceDispatchStatus.OnScene)
            _ = ArmOnSceneResolutionAsync(args.Dispatch);
    }

    private async Task ArmOnSceneResolutionAsync(NCServiceDispatchRecord dispatch)
    {
        if (!_active || !_clock.TryGetCurrentTime(out _, out var now) ||
            !_prototypes.TryIndex(DefaultPolicy, out NCIncidentPolicyPrototype? policy))
            return;
        var incident = await _incidents.GetAsync(dispatch.Snapshot.IncidentId, true);
        if (incident?.Snapshot.Stage != NCCityIncidentStage.Active || incident.Snapshot.EscalatesAt != null)
            return;
        await _incidents.ScheduleEscalationAsync(incident.Snapshot.IncidentId, incident.AggregateVersion,
            now.Advance(policy.AbstractResolutionDelay), LifecycleActor(incident.Snapshot),
            $"response-scene.{dispatch.Snapshot.DispatchId}.{incident.AggregateVersion}");
    }

    private async Task RecoverAsync()
    {
        string? after = null;
        while (_active)
        {
            var page = await _database.GetNCAggregateSnapshotPageAsync(
                NCCityIncidentRegistrySystem.AggregateType, after, RecoveryPageSize);
            foreach (var stored in page)
            {
                if (!Guid.TryParse(stored.AggregateId, out var raw))
                    continue;
                var incident = await _incidents.GetAsync(new NCCityIncidentId(raw), true);
                if (incident?.Snapshot.EscalatesAt is { } due && incident.Snapshot.Stage is not
                    (NCCityIncidentStage.Closed or NCCityIncidentStage.Cancelled))
                    Enqueue(incident.Snapshot.IncidentId, due);
            }
            if (page.Count < RecoveryPageSize)
                break;
            after = page[^1].AggregateId;
        }
    }

    private async Task AdvanceAsync(NCCityIncidentId incidentId)
    {
        try
        {
            var incident = await _incidents.GetAsync(incidentId, true);
            if (!_active || incident?.Snapshot.EscalatesAt is not { } due ||
                !_clock.TryGetCurrentTime(out _, out var now) ||
                !_prototypes.TryIndex(DefaultPolicy, out NCIncidentPolicyPrototype? policy))
                return;
            if (due > now)
            {
                Enqueue(incidentId, due);
                return;
            }

            var actor = LifecycleActor(incident.Snapshot);
            switch (incident.Snapshot.Stage)
            {
                case NCCityIncidentStage.Reported:
                    await ChangeStage(incident, NCCityIncidentStage.Verified,
                        now.Advance(policy.VerificationDelay), actor, "verify");
                    break;
                case NCCityIncidentStage.Verified:
                    await DispatchAsync(incident, policy, now, actor);
                    break;
                case NCCityIncidentStage.Dispatching:
                    await _incidents.ScheduleEscalationAsync(incidentId, incident.AggregateVersion,
                        now.Advance(policy.DispatchRetryDelay), actor, $"dispatch-wait.{incident.AggregateVersion}");
                    break;
                case NCCityIncidentStage.Active:
                    await ResolveAbstractAsync(incident, policy, now, actor);
                    break;
                case NCCityIncidentStage.Resolved:
                    await ChangeStage(incident, NCCityIncidentStage.Closed, null, actor, "close");
                    break;
            }
        }
        finally
        {
            _processing.Remove(incidentId);
        }
    }

    private async Task DispatchAsync(NCCityIncidentRecord incident, NCIncidentPolicyPrototype policy,
        NCActiveWorldTime now, NCPartyRef requester)
    {
        if (!policy.ResponseServices.TryGetValue(incident.Snapshot.Kind, out var responseServices) ||
            responseServices.Count == 0)
        {
            await ChangeStage(incident, NCCityIncidentStage.Active,
                now.Advance(policy.AbstractResolutionDelay), requester, "activate-without-service");
            return;
        }

        var accepted = false;
        foreach (var servicePrototypeId in responseServices.Distinct())
        {
            if (!_prototypes.TryIndex(servicePrototypeId, out NCCityServicePrototype? prototype))
                continue;
            if (prototype.Policy == new ProtoId<NCCityServicePolicyPrototype>("MaxTac") &&
                incident.Snapshot.Severity < (incident.Snapshot.Kind == NCCityIncidentKind.Cyberpsychosis
                    ? policy.MaxTacCyberpsychosisThreshold
                    : policy.MaxTacSeverityThreshold))
                continue;

            var serviceId = new NCCityServiceId(NCPersistentIdentity.CityService(prototype.StableKey).PersistentId);
            var dispatchId = NCPersistentIdentity.ServiceDispatch(incident.Snapshot.IncidentId, serviceId);
            var result = await _services.RequestDispatchAsync(new NCRequestServiceDispatch(
                NCPersistentIdentity.Command("incident", "request-service", dispatchId.ToString()), dispatchId,
                serviceId, incident.Snapshot.IncidentId, null, requester,
                NCPersistentIdentity.Correlation("incident", "request-service", dispatchId.ToString()),
                incident.Snapshot.LastAppliedEventId, "incident-lifecycle"));
            accepted |= result.Status is NCCityServiceMutationStatus.Applied or NCCityServiceMutationStatus.Duplicate;
        }

        var current = await _incidents.GetAsync(incident.Snapshot.IncidentId, true);
        if (current == null || current.Snapshot.Stage != NCCityIncidentStage.Verified)
            return;
        if (accepted)
            await _incidents.ScheduleEscalationAsync(current.Snapshot.IncidentId, current.AggregateVersion,
                now.Advance(policy.DispatchRetryDelay), requester, $"dispatch-watch.{current.AggregateVersion}");
        else
            await _incidents.ScheduleEscalationAsync(current.Snapshot.IncidentId, current.AggregateVersion,
                now.Advance(policy.DispatchRetryDelay), requester, $"dispatch-retry.{current.AggregateVersion}");
    }

    private async Task ResolveAbstractAsync(NCCityIncidentRecord incident, NCIncidentPolicyPrototype policy,
        NCActiveWorldTime now, NCPartyRef actor)
    {
        if (incident.Snapshot.MaterializationPolicy != NCIncidentMaterializationPolicy.AbstractUnlessObserved ||
            _occupancy.IsOccupied(incident.Snapshot.LocationZone, out _))
        {
            await _incidents.ScheduleEscalationAsync(incident.Snapshot.IncidentId, incident.AggregateVersion,
                now.Advance(policy.DispatchRetryDelay), actor, $"observed.{incident.AggregateVersion}");
            return;
        }

        var responsible = incident.Snapshot.Participants
            .Where(value => value.Role is NCIncidentParticipantRole.Suspect or
                NCIncidentParticipantRole.ResponsibleFaction)
            .Select(value => value.Party).Distinct().ToArray();
        var outcome = new NCIncidentOutcome("abstract-contained", responsible, [],
            incident.Snapshot.Severity >= 900 ? 1 : 0, checked((long) incident.Snapshot.Severity * 100));
        var result = await _incidents.ResolveAsync(new NCResolveIncidentRequest(
            NCPersistentIdentity.Command("incident", "abstract-resolve", incident.Snapshot.IncidentId.ToString()),
            incident.Snapshot.IncidentId, incident.AggregateVersion, outcome, actor,
            NCPersistentIdentity.Correlation("incident", "abstract-resolve", incident.Snapshot.IncidentId.ToString()),
            incident.Snapshot.LastAppliedEventId, "incident-lifecycle"));
        if (result.Incident is { } resolved)
            await _incidents.ScheduleEscalationAsync(resolved.Snapshot.IncidentId, resolved.AggregateVersion,
                now.Advance(policy.ClosureDelay), actor, $"close.{resolved.AggregateVersion}");
    }

    private Task<NCIncidentMutationResult> ChangeStage(NCCityIncidentRecord incident, NCCityIncidentStage stage,
        NCActiveWorldTime? next, NCPartyRef actor, string operation) => _incidents.ChangeStageAsync(
        new NCChangeIncidentStageRequest(NCPersistentIdentity.Command("incident", operation,
            $"{incident.Snapshot.IncidentId}.{incident.AggregateVersion}"), incident.Snapshot.IncidentId,
            incident.AggregateVersion, stage, actor, next, NCPersistentIdentity.Correlation("incident", operation,
                $"{incident.Snapshot.IncidentId}.{incident.AggregateVersion}"),
            incident.Snapshot.LastAppliedEventId, "incident-lifecycle"));

    private static NCPartyRef LifecycleActor(NCCityIncidentSnapshot incident) =>
        incident.Participants.FirstOrDefault(value => value.Party.IsValid)?.Party ??
        NCPersistentIdentity.CityService("incident-lifecycle");

    private void Enqueue(NCCityIncidentId incidentId, NCActiveWorldTime due)
    {
        if (_active && _queued.Add(incidentId))
            _due.Enqueue(incidentId, due.Ticks);
    }

    private void Clear()
    {
        _active = false;
        _due.Clear();
        _queued.Clear();
        _processing.Clear();
    }
}
