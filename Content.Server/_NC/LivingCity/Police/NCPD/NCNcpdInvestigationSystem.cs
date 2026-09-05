// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.CityServices;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Police;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Police;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>
/// Projects a persistent incident onto one real Inspector, supplies a bounded cognition fact, and commits only
/// evidence-backed outcomes. The component is diagnostic state; incident and wanted aggregates remain canonical.
/// </summary>
public sealed partial class NCNcpdInvestigationSystem : EntitySystem
{
    private const string ReviewAction = "ncpd-review-case-file";
    private const string ConcludeAction = "ncpd-conclude-investigation";
    private static readonly ProtoId<NCNcpdInvestigationPolicyPrototype> DefaultPolicy =
        "NCDefaultNcpdInvestigationPolicy";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private NCCityIncidentRegistrySystem _incidents = default!;
    [Dependency] private NCPoliceWantedRegistrySystem _wanted = default!;
    [Dependency] private NCNcpdServiceDispatchSystem _dispatch = default!;
    [Dependency] private NCNcpdUnitSystem _units = default!;
    [Dependency] private NCNcpdWorkforceProjectionSystem _workforce = default!;
    [Dependency] private NCNcpdAssignmentExecutionSystem _assignments = default!;
    [Dependency] private NCCognitionSystem _cognition = default!;

    private readonly Dictionary<NCCityIncidentId, NCCityIncidentRecord> _activeCases = [];
    private readonly Dictionary<NCCityIncidentId, EntityUid> _assigned = [];
    private readonly Dictionary<Guid, PendingAction> _pendingActions = [];
    private readonly HashSet<NCCityIncidentId> _pendingStageChanges = [];
    private readonly Dictionary<NCCityIncidentId, NCPoliceWantedRecord> _wantedTriggers = [];
    private readonly List<NCNcpdServiceDispatchAdapterSnapshot> _dispatchBuffer = [];

    public int ActiveCount => _activeCases.Count;
    public int AssignedCount => _assigned.Count;
    public int PendingMutationCount => _pendingActions.Count + _pendingStageChanges.Count;
    public int CompletedCount { get; private set; }
    public int BlockedCount { get; private set; }
    public string? LastError { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityIncidentPersistedEvent>(OnIncidentPersisted);
        SubscribeLocalEvent<NCPoliceWantedPersistedEvent>(OnWantedPersisted);
        SubscribeLocalEvent<NCNcpdInvestigationUnitAssignedEvent>(OnUnitAssigned);
        SubscribeLocalEvent<NCCityActorMaterializationChangedEvent>(OnMaterializationChanged);
        SubscribeLocalEvent<NCCognitionCollectFactsEvent>(OnCollectFacts);
        SubscribeLocalEvent<NCHumanActionExecutionEvent>(OnExecute);
        SubscribeLocalEvent<NCHumanActionInterruptedEvent>(OnInterrupted);
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(_ => Clear());
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Clear());
    }

    private void OnWantedPersisted(NCPoliceWantedPersistedEvent args)
    {
        var wanted = args.Record;
        if (wanted.Snapshot.Status != NCPoliceWantedStatus.Active || !wanted.Snapshot.IncidentId.IsValid)
            return;
        _wantedTriggers[wanted.Snapshot.IncidentId] = wanted;
        BeginInvestigation(wanted.Snapshot.IncidentId);
    }

    private void OnIncidentPersisted(NCCityIncidentPersistedEvent args)
    {
        var incident = args.Incident;
        if (_wantedTriggers.ContainsKey(incident.Snapshot.IncidentId) && incident.Snapshot.Stage is
            NCCityIncidentStage.Active or NCCityIncidentStage.Contained)
        {
            BeginInvestigation(incident.Snapshot.IncidentId);
        }

        if (incident.Snapshot.Stage == NCCityIncidentStage.Investigation)
        {
            if (TryGetPolicy(out var policy) && (_activeCases.ContainsKey(incident.Snapshot.IncidentId) ||
                    _activeCases.Count < policy.MaximumActiveInvestigations))
            {
                _activeCases[incident.Snapshot.IncidentId] = incident;
                RefreshProjection(incident);
                TryAssign(incident.Snapshot.IncidentId);
            }
            return;
        }

        if (incident.Snapshot.Stage is NCCityIncidentStage.Resolved or NCCityIncidentStage.Closed or
            NCCityIncidentStage.Cancelled)
        {
            RemoveCase(incident.Snapshot.IncidentId);
        }
    }

    private void BeginInvestigation(NCCityIncidentId incidentId)
    {
        if (_pendingStageChanges.Add(incidentId))
            _ = BeginInvestigationAsync(incidentId);
    }

    private async Task BeginInvestigationAsync(NCCityIncidentId incidentId)
    {
        try
        {
            if (!TryGetPolicy(out var policy) || !_wantedTriggers.TryGetValue(incidentId, out var trigger))
                return;
            for (var attempt = 0; attempt < policy.MaximumPersistenceRetries; attempt++)
            {
                var incident = await _incidents.GetAsync(incidentId, true);
                if (incident == null || incident.Snapshot.Stage is NCCityIncidentStage.Resolved or
                    NCCityIncidentStage.Closed or NCCityIncidentStage.Cancelled)
                    return;
                if (incident.Snapshot.Stage == NCCityIncidentStage.Investigation)
                    return;
                if (incident.Snapshot.Stage is not (NCCityIncidentStage.Active or NCCityIncidentStage.Contained))
                    return;

                var stable = $"{incidentId}.{incident.AggregateVersion}";
                var result = await _incidents.ChangeStageAsync(new NCChangeIncidentStageRequest(
                    NCPersistentIdentity.Command("ncpd-investigation", "open", stable),
                    incidentId,
                    incident.AggregateVersion,
                    NCCityIncidentStage.Investigation,
                    trigger.Snapshot.IssuedBy,
                    null,
                    NCPersistentIdentity.Correlation("ncpd-investigation", "open", stable),
                    trigger.Snapshot.LastAppliedEventId,
                    "ncpd-investigation"));
                if (result.Status is NCIncidentMutationStatus.Applied or NCIncidentMutationStatus.Duplicate)
                    return;
                if (result.Status != NCIncidentMutationStatus.VersionConflict)
                {
                    LastError = result.Error ?? "Investigation stage transition was rejected.";
                    return;
                }
            }
            LastError = "Investigation stage transition exhausted its version retries.";
        }
        finally
        {
            _pendingStageChanges.Remove(incidentId);
        }
    }

    private void OnUnitAssigned(NCNcpdInvestigationUnitAssignedEvent args) => TryAssign(args.IncidentId, args.UnitId);

    private void OnMaterializationChanged(NCCityActorMaterializationChangedEvent args)
    {
        if (!args.Materialized)
        {
            foreach (var incidentId in _assigned
                         .Where(value => value.Value == args.EntityUid)
                         .Select(value => value.Key)
                         .ToArray())
                _assigned.Remove(incidentId);
            return;
        }
        if (!_workforce.TryGetRoleProfile(args.ActorId, out _, out var role) ||
            !role.CanInvestigate)
            return;
        foreach (var incidentId in _activeCases.Keys.ToArray())
            TryAssign(incidentId);
    }

    private void TryAssign(NCCityIncidentId incidentId, NCNcpdUnitId requiredUnit = default)
    {
        if (!_activeCases.TryGetValue(incidentId, out var incident) || !_clock.TryGetCurrentTime(out _, out var now))
            return;
        _dispatch.GetSnapshots(_dispatchBuffer);
        foreach (var response in _dispatchBuffer)
        {
            if (response.IncidentId != incidentId || !response.UnitId.IsValid ||
                requiredUnit.IsValid && response.UnitId != requiredUnit ||
                !_units.TryGetSnapshot(response.UnitId, out var unit) ||
                !_workforce.TryGetRoleProfile(unit.LeaderActorId, out _, out var role) || !role.CanInvestigate ||
                !role.AllowedAssignments.Contains(NCNcpdAssignmentKind.Investigation) ||
                !_runtime.TryGetEntity(unit.LeaderActorId, out var entity) || !Exists(entity))
            {
                continue;
            }

            if (_assigned.TryGetValue(incidentId, out var previous) && previous != entity && Exists(previous))
                RemComp<NCNcpdInvestigationComponent>(previous);
            var projection = EnsureComp<NCNcpdInvestigationComponent>(entity);
            projection.IncidentId = incidentId;
            projection.SourceWorldEventId = SourceWorldEvent(incident.Snapshot);
            projection.IncidentVersion = incident.AggregateVersion;
            projection.Status = projection.Status is NCNcpdInvestigationStatus.ReviewingCase or
                NCNcpdInvestigationStatus.Interviewing or NCNcpdInvestigationStatus.Evaluating
                ? projection.Status
                : NCNcpdInvestigationStatus.Assigned;
            projection.EvidenceCount = incident.Snapshot.Evidence.Count;
            projection.WitnessCount = incident.Snapshot.WitnessStatements.Count;
            projection.UpdatedAt = now;
            projection.Failure = string.Empty;
            _assigned[incidentId] = entity;
            _cognition.ForceSchedule(unit.LeaderActorId, now);
            return;
        }
    }

    private void OnCollectFacts(NCCognitionCollectFactsEvent args)
    {
        if (!_runtime.TryGetEntity(args.ActorId, out var entity) ||
            !TryComp<NCNcpdInvestigationComponent>(entity, out var investigation) ||
            !_activeCases.TryGetValue(investigation.IncidentId, out var incident))
            return;

        args.Sink.Add(new NCCognitionFact(
            "ncpd.investigation-assigned",
            new NCCognitionSubjectRef(NCCognitionReferenceKind.Incident, investigation.IncidentId.ToString()),
            NCCognitionDomain.Crime,
            "ncpd-investigation",
            incident.AggregateVersion,
            NCCognitionValue.FromBoolean(true),
            incident.Snapshot.UpdatedAt,
            args.Now.Advance(TimeSpan.FromMinutes(5))));
    }

    private void OnExecute(NCHumanActionExecutionEvent args)
    {
        if (args.Step.ActionId is not (ReviewAction or ConcludeAction) || args.EntityUid is not { } inspector ||
            !Exists(inspector) || !TryComp<NCNcpdInvestigationComponent>(inspector, out var investigation))
            return;
        args.Handled = true;
        _assignments.YieldToProfessionalAction(inspector);
        if (!_clock.TryGetCurrentTime(out _, out var now) || !TryGetPolicy(out var policy) ||
            args.Intention.Target.Kind != NCCognitionReferenceKind.Incident ||
            args.Intention.Target.StableId != investigation.IncidentId.ToString() ||
            investigation.SourceWorldEventId == Guid.Empty ||
            !_parties.TryResolve(inspector, out var inspectorParty))
        {
            args.Failure = "ncpd-investigation-context-unavailable";
            return;
        }

        if (!_pendingActions.TryGetValue(args.InstanceId, out var pending))
        {
            investigation.Status = args.Step.ActionId == ReviewAction
                ? NCNcpdInvestigationStatus.ReviewingCase
                : NCNcpdInvestigationStatus.Evaluating;
            investigation.UpdatedAt = now;
            investigation.Failure = string.Empty;
            var operation = args.Step.ActionId == ReviewAction
                ? ReviewAsync(investigation.IncidentId, investigation.SourceWorldEventId, inspectorParty, policy, now)
                : ConcludeAsync(investigation.IncidentId, investigation.SourceWorldEventId, inspectorParty, policy);
            pending = new PendingAction(inspector, investigation.IncidentId, args.Step.ActionId, operation);
            _pendingActions.Add(args.InstanceId, pending);
        }

        if (!pending.Operation.IsCompleted)
            return;
        _pendingActions.Remove(args.InstanceId);
        var result = pending.Operation.GetAwaiter().GetResult();
        if (!TryComp<NCNcpdInvestigationComponent>(inspector, out investigation))
        {
            args.Failure = "ncpd-investigation-projection-lost";
            return;
        }

        investigation.UpdatedAt = now;
        investigation.EvidenceCount = result.Incident?.Snapshot.Evidence.Count ?? investigation.EvidenceCount;
        investigation.WitnessCount = result.Incident?.Snapshot.WitnessStatements.Count ?? investigation.WitnessCount;
        investigation.IncidentVersion = result.Incident?.AggregateVersion ?? investigation.IncidentVersion;
        investigation.Failure = result.Error ?? string.Empty;
        if (!result.Success)
        {
            investigation.Status = result.Blocked
                ? NCNcpdInvestigationStatus.Blocked
                : NCNcpdInvestigationStatus.Failed;
            if (result.Blocked)
                BlockedCount++;
            LastError = result.Error;
            args.Failure = result.Error ?? "ncpd-investigation-action-rejected";
            return;
        }

        investigation.Status = args.Step.ActionId == ConcludeAction
            ? NCNcpdInvestigationStatus.Complete
            : NCNcpdInvestigationStatus.Assigned;
        if (args.Step.ActionId == ConcludeAction)
            CompletedCount++;
        args.Completed = true;
    }

    private async Task<InvestigationResult> ReviewAsync(
        NCCityIncidentId incidentId,
        Guid sourceWorldEventId,
        NCPartyRef inspector,
        NCNcpdInvestigationPolicyPrototype policy,
        NCActiveWorldTime now)
    {
        try
        {
            for (var attempt = 0; attempt < policy.MaximumPersistenceRetries; attempt++)
            {
                var incident = await _incidents.GetAsync(incidentId, true);
                if (incident == null || incident.Snapshot.Stage != NCCityIncidentStage.Investigation)
                    return new(false, false, incident, "ncpd-investigation-case-unavailable");
                var evidenceId = NCPersistentIdentity.Command(
                    "ncpd-investigation", "source-event-evidence", $"{incidentId}.{sourceWorldEventId:D}");
                if (incident.Snapshot.Evidence.Any(value => value.EvidenceId == evidenceId))
                    return new(true, false, incident, null);
                var evidence = new NCIncidentEvidence(
                    evidenceId,
                    NCIncidentEvidenceKind.WorldEvent,
                    inspector,
                    $"world-event:{sourceWorldEventId:D}",
                    1000,
                    true,
                    now);
                var stable = $"{incidentId}.{sourceWorldEventId:D}.{incident.AggregateVersion}";
                var mutation = await _incidents.AddEvidenceAsync(new NCAddIncidentEvidenceRequest(
                    NCPersistentIdentity.Command("ncpd-investigation", "review", stable),
                    incidentId,
                    incident.AggregateVersion,
                    evidence,
                    inspector,
                    NCPersistentIdentity.Correlation("ncpd-investigation", "review", stable),
                    sourceWorldEventId,
                    "ncpd-investigation-review"));
                if (mutation.Status is NCIncidentMutationStatus.Applied or NCIncidentMutationStatus.Duplicate)
                    return new(true, false, mutation.Incident ?? incident, null);
                if (mutation.Status != NCIncidentMutationStatus.VersionConflict)
                    return new(false, false, mutation.Incident, mutation.Error);
            }
            return new(false, false, null, "ncpd-investigation-review-version-conflict");
        }
        catch (Exception exception)
        {
            return new(false, false, null, $"ncpd-investigation-review-exception:{exception.GetType().Name}");
        }
    }

    private async Task<InvestigationResult> ConcludeAsync(
        NCCityIncidentId incidentId,
        Guid sourceWorldEventId,
        NCPartyRef inspector,
        NCNcpdInvestigationPolicyPrototype policy)
    {
        try
        {
            var incident = await _incidents.GetAsync(incidentId, true);
            if (incident == null || incident.Snapshot.Stage != NCCityIncidentStage.Investigation)
                return new(false, false, incident, "ncpd-investigation-case-unavailable");
            var evidence = incident.Snapshot.Evidence.Count(value =>
                value.Admissible && value.Reliability >= policy.MinimumEvidenceReliability);
            var statements = incident.Snapshot.WitnessStatements.Count(value =>
                value.Confidence >= (int) Math.Round(policy.MinimumWitnessConfidence * 1000f));
            if (evidence + statements < policy.MinimumCorroboratingRecords)
                return new(false, true, incident, "ncpd-investigation-insufficient-corroboration");

            var suspects = incident.Snapshot.Participants
                .Where(value => value.Role == NCIncidentParticipantRole.Suspect && value.Confirmed)
                .Select(value => value.Party)
                .Where(value => value.Kind is NCPartyKind.Actor or NCPartyKind.Character)
                .Distinct()
                .ToArray();
            foreach (var suspect in suspects)
            {
                var wanted = await _wanted.CreateAsync(
                    suspect,
                    incidentId,
                    sourceWorldEventId,
                    incident.Snapshot.Severity,
                    "investigation.confirmed-suspect",
                    inspector,
                    "ncpd-investigation-conclusion");
                if (wanted.Status is not (NCPoliceWantedMutationStatus.Applied or
                    NCPoliceWantedMutationStatus.Duplicate))
                    return new(false, false, incident, wanted.Error ?? "ncpd-investigation-wanted-rejected");
            }

            for (var attempt = 0; attempt < policy.MaximumPersistenceRetries; attempt++)
            {
                incident = await _incidents.GetAsync(incidentId, true);
                if (incident == null)
                    return new(false, false, null, "ncpd-investigation-case-unavailable");
                if (incident.Snapshot.Stage == NCCityIncidentStage.Resolved)
                    return new(true, false, incident, null);
                var outcome = new NCIncidentOutcome(
                    suspects.Length > 0 ? "investigation-suspect-identified" : "investigation-no-charge",
                    suspects,
                    [],
                    0,
                    0);
                var stable = $"{incidentId}.{incident.AggregateVersion}";
                var mutation = await _incidents.ResolveAsync(new NCResolveIncidentRequest(
                    NCPersistentIdentity.Command("ncpd-investigation", "conclude", stable),
                    incidentId,
                    incident.AggregateVersion,
                    outcome,
                    inspector,
                    NCPersistentIdentity.Correlation("ncpd-investigation", "conclude", stable),
                    sourceWorldEventId,
                    "ncpd-investigation-conclusion"));
                if (mutation.Status is NCIncidentMutationStatus.Applied or NCIncidentMutationStatus.Duplicate)
                    return new(true, false, mutation.Incident ?? incident, null);
                if (mutation.Status != NCIncidentMutationStatus.VersionConflict)
                    return new(false, false, mutation.Incident, mutation.Error);
            }
            return new(false, false, incident, "ncpd-investigation-conclusion-version-conflict");
        }
        catch (Exception exception)
        {
            return new(false, false, null, $"ncpd-investigation-conclusion-exception:{exception.GetType().Name}");
        }
    }

    private void RefreshProjection(NCCityIncidentRecord incident)
    {
        if (!_assigned.TryGetValue(incident.Snapshot.IncidentId, out var entity) || !Exists(entity) ||
            !TryComp<NCNcpdInvestigationComponent>(entity, out var projection))
            return;
        projection.IncidentVersion = incident.AggregateVersion;
        projection.EvidenceCount = incident.Snapshot.Evidence.Count;
        projection.WitnessCount = incident.Snapshot.WitnessStatements.Count;
        projection.UpdatedAt = incident.Snapshot.UpdatedAt;
    }

    private void RemoveCase(NCCityIncidentId incidentId)
    {
        _activeCases.Remove(incidentId);
        _wantedTriggers.Remove(incidentId);
        if (_assigned.Remove(incidentId, out var entity) && Exists(entity))
            RemComp<NCNcpdInvestigationComponent>(entity);
    }

    private bool TryGetPolicy(out NCNcpdInvestigationPolicyPrototype policy)
    {
        if (_prototypes.TryIndex(DefaultPolicy, out policy!) && policy.MaximumPersistenceRetries is > 0 and <= 8 &&
            policy.MaximumActiveInvestigations is > 0 and <= 128 && policy.MinimumCorroboratingRecords is > 0 and <= 16 &&
            policy.MinimumEvidenceReliability is >= 0 and <= 1000 &&
            float.IsFinite(policy.MinimumWitnessConfidence) && policy.MinimumWitnessConfidence is >= 0f and <= 1f)
            return true;
        policy = default!;
        LastError = $"NCPD investigation policy {DefaultPolicy} is missing or invalid.";
        return false;
    }

    private static Guid SourceWorldEvent(NCCityIncidentSnapshot incident) =>
        incident.Causes.FirstOrDefault()?.SourceWorldEventId ?? Guid.Empty;

    private void OnInterrupted(NCHumanActionInterruptedEvent args)
    {
        if (args.ActionId is ReviewAction or ConcludeAction)
            _pendingActions.Remove(args.InstanceId);
    }

    private void Clear()
    {
        foreach (var entity in _assigned.Values.Where(Exists).ToArray())
            RemComp<NCNcpdInvestigationComponent>(entity);
        _activeCases.Clear();
        _assigned.Clear();
        _pendingActions.Clear();
        _pendingStageChanges.Clear();
        _wantedTriggers.Clear();
        CompletedCount = 0;
        BlockedCount = 0;
        LastError = null;
    }

    private readonly record struct PendingAction(
        EntityUid Inspector,
        NCCityIncidentId IncidentId,
        string ActionId,
        Task<InvestigationResult> Operation);

    private readonly record struct InvestigationResult(
        bool Success,
        bool Blocked,
        NCCityIncidentRecord? Incident,
        string? Error);
}
