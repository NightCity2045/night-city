// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.CityServices;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Server.Chat.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Chat;
using Content.Shared.Interaction;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>
/// Performs a bounded physical interview and persists only statements backed by the witness's own direct-perception
/// memory of the exact incident. GPT may later render or discuss that fact, but cannot create the statement.
/// </summary>
public sealed partial class NCNcpdWitnessInterviewSystem : EntitySystem
{
    private static readonly ProtoId<NCNcpdInvestigationPolicyPrototype> DefaultPolicy =
        "NCDefaultNcpdInvestigationPolicy";
    private const string InterviewAction = "ncpd-interview-witnesses";
    private const string InvestigationInterviewAction = "ncpd-interview-assigned-witnesses";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private NCCityIncidentRegistrySystem _incidents = default!;
    [Dependency] private NCMemoryRegistrySystem _memories = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private NCNcpdAssignmentExecutionSystem _assignments = default!;

    private readonly Dictionary<Guid, PendingInterview> _pending = [];

    public int PendingMutations => _pending.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCHumanActionExecutionEvent>(OnExecute);
        SubscribeLocalEvent<NCHumanActionInterruptedEvent>(OnInterrupted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => _pending.Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => _pending.Clear());
    }

    private void OnExecute(NCHumanActionExecutionEvent args)
    {
        if (args.Step.ActionId is not (InterviewAction or InvestigationInterviewAction) ||
            args.EntityUid is not { } officer || !Exists(officer) ||
            !HasComp<NCNcpdOfficerComponent>(officer))
        {
            return;
        }

        args.Handled = true;
        _assignments.YieldToProfessionalAction(officer);
        if (!_clock.TryGetCurrentTime(out _, out var now) || !TryGetPolicy(out var policy))
        {
            args.Failure = "ncpd-witness-interview-configuration-unavailable";
            return;
        }

        if (_pending.TryGetValue(args.InstanceId, out var pending))
        {
            CompletePending(args, officer, pending, policy, now);
            return;
        }

        NCCityIncidentId incidentId;
        Guid sourceWorldEventId;
        if (args.Step.ActionId == InvestigationInterviewAction &&
            TryComp<NCNcpdInvestigationComponent>(officer, out var investigation) &&
            investigation.IncidentId.IsValid && investigation.SourceWorldEventId != Guid.Empty)
        {
            incidentId = investigation.IncidentId;
            sourceWorldEventId = investigation.SourceWorldEventId;
            investigation.Status = NCNcpdInvestigationStatus.Interviewing;
            investigation.UpdatedAt = now;
            investigation.Failure = string.Empty;
        }
        else if (TryComp<NCNcpdIncidentReportComponent>(officer, out var report) &&
                 report.Status == NCNcpdIncidentReportStatus.Persisted && report.IncidentId.IsValid &&
                 report.SourceWorldEventId != Guid.Empty)
        {
            incidentId = report.IncidentId;
            sourceWorldEventId = report.SourceWorldEventId;
        }
        else
        {
            args.Failure = "ncpd-witness-incident-unavailable";
            return;
        }

        var candidates = FindWitnesses(officer, sourceWorldEventId, policy, now);
        var projection = EnsureComp<NCNcpdWitnessInterviewComponent>(officer);
        projection.IncidentId = incidentId;
        projection.SourceWorldEventId = sourceWorldEventId;
        projection.Candidates = candidates.Length;
        projection.RecordedStatements = 0;
        projection.CurrentWitness = candidates.Length == 0 ? EntityUid.Invalid : candidates[0].Entity;
        projection.UpdatedAt = now;
        projection.Failure = string.Empty;

        if (candidates.Length == 0)
        {
            projection.Status = NCNcpdWitnessInterviewStatus.Complete;
            if (TryComp<NCNcpdInvestigationComponent>(officer, out var emptyInvestigation) &&
                emptyInvestigation.IncidentId == incidentId)
            {
                emptyInvestigation.Status = NCNcpdInvestigationStatus.Assigned;
                emptyInvestigation.UpdatedAt = now;
            }
            args.Completed = true;
            return;
        }

        // Both sides use ordinary in-world speech. The witness sentence is a rendering of an already observed event,
        // never free-form evidence authored by a neural model.
        foreach (var candidate in candidates)
        {
            _chat.TrySendInGameICMessage(officer, Loc.GetString(policy.WitnessQuestionLocKey),
                InGameICChatType.Speak, hideChat: false);
            var answerKey = candidate.Kind == NCCityLocalPerceivedEventKind.Killing
                ? policy.WitnessKillingLocKey
                : policy.WitnessAssaultLocKey;
            _chat.TrySendInGameICMessage(candidate.Entity,
                Loc.GetString(answerKey, ("actor", candidate.ActorHandle), ("target", candidate.TargetHandle)),
                InGameICChatType.Speak, hideChat: false);
        }

        projection.Status = NCNcpdWitnessInterviewStatus.Recording;
        var operation = PersistAsync(incidentId, sourceWorldEventId, candidates, policy, now);
        _pending.Add(args.InstanceId, new PendingInterview(
            officer,
            incidentId,
            sourceWorldEventId,
            candidates,
            now.Advance(policy.MemoryAvailabilityWait),
            operation));
    }

    private void CompletePending(
        NCHumanActionExecutionEvent args,
        EntityUid officer,
        PendingInterview pending,
        NCNcpdInvestigationPolicyPrototype policy,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now)
    {
        if (!pending.Operation.IsCompleted)
            return;

        var result = pending.Operation.GetAwaiter().GetResult();
        var projection = EnsureComp<NCNcpdWitnessInterviewComponent>(officer);
        projection.RecordedStatements = result.Recorded;
        projection.UpdatedAt = now;
        if (!result.Success)
        {
            _pending.Remove(args.InstanceId);
            projection.Status = NCNcpdWitnessInterviewStatus.Failed;
            projection.Failure = result.Error ?? "ncpd-witness-statement-rejected";
            args.Failure = projection.Failure;
            return;
        }

        if (result.MemoryPending && now < pending.MemoryDeadline)
        {
            projection.Status = NCNcpdWitnessInterviewStatus.AwaitingMemory;
            projection.Failure = string.Empty;
            _pending[args.InstanceId] = pending with
            {
                Operation = PersistAsync(pending.IncidentId, pending.SourceWorldEventId,
                    pending.Candidates, policy, now),
            };
            return;
        }

        _pending.Remove(args.InstanceId);
        projection.Status = NCNcpdWitnessInterviewStatus.Complete;
        projection.CurrentWitness = EntityUid.Invalid;
        projection.Failure = result.MemoryPending ? "ncpd-witness-memory-not-ready" : string.Empty;
        if (TryComp<NCNcpdInvestigationComponent>(officer, out var investigation) &&
            investigation.IncidentId == pending.IncidentId)
        {
            investigation.Status = NCNcpdInvestigationStatus.Assigned;
            investigation.WitnessCount = Math.Max(investigation.WitnessCount, result.Recorded);
            investigation.UpdatedAt = now;
            investigation.Failure = projection.Failure;
        }
        args.Completed = true;
    }

    private WitnessCandidate[] FindWitnesses(
        EntityUid officer,
        Guid sourceWorldEventId,
        NCNcpdInvestigationPolicyPrototype policy,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now)
    {
        var result = new List<WitnessCandidate>(policy.MaximumWitnessesPerInterview);
        var origin = _transform.GetMapCoordinates(officer);
        var inspected = 0;
        foreach (var entity in _lookup.GetEntitiesInRange(origin, policy.WitnessInterviewRadius))
        {
            if (++inspected > policy.MaximumWitnessCandidates)
                break;
            if (entity == officer || !Exists(entity) || !_runtime.TryGetActor(entity, out var witnessActor) ||
                !witnessActor.IsValid || !TryComp<NCCityLocalPerceptionComponent>(entity, out var perception) ||
                !_interaction.InRangeUnobstructed(officer, entity, policy.WitnessInterviewRadius))
            {
                continue;
            }

            var witnessed = perception.RecentEvents.FirstOrDefault(value =>
                value.EventId == sourceWorldEventId && value.ExpiresAt > now &&
                value.Confidence >= policy.MinimumWitnessConfidence && value.ActorEntity != entity &&
                Exists(value.ActorEntity) && Exists(value.TargetEntity));
            if (witnessed.EventId == Guid.Empty)
                continue;

            var witness = NCPartyRef.ForActor(witnessActor);
            var identified = new List<NCPartyRef>(2);
            if (_parties.TryResolve(witnessed.ActorEntity, out var actor) && actor != witness)
                identified.Add(actor);
            if (_parties.TryResolve(witnessed.TargetEntity, out var target) && target != witness &&
                !identified.Contains(target))
            {
                identified.Add(target);
            }

            result.Add(new WitnessCandidate(
                entity,
                witness,
                witnessed.Kind,
                witnessed.ActorHandle,
                witnessed.TargetHandle,
                witnessed.Confidence,
                identified.ToArray()));
            if (result.Count >= policy.MaximumWitnessesPerInterview)
                break;
        }
        return result.ToArray();
    }

    private async Task<InterviewResult> PersistAsync(
        NCCityIncidentId incidentId,
        Guid worldEventId,
        IReadOnlyList<WitnessCandidate> candidates,
        NCNcpdInvestigationPolicyPrototype policy,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now)
    {
        try
        {
            var incident = await _incidents.GetAsync(incidentId, true);
            if (incident == null)
                return new InterviewResult(false, false, 0, "ncpd-witness-incident-not-found");

            var recorded = 0;
            var memoryPending = false;
            foreach (var candidate in candidates)
            {
                var memoryId = NCPersistentIdentity.Memory(candidate.Witness, worldEventId);
                var statementId = NCPersistentIdentity.Command(
                    "ncpd-witness",
                    "statement",
                    $"{incidentId}.{candidate.Witness}.{worldEventId:D}");
                if (incident.Snapshot.WitnessStatements.Any(value => value.StatementId == statementId))
                {
                    recorded++;
                    continue;
                }

                var memory = await _memories.GetAsync(candidate.Witness, true);
                if (memory == null || memory.Snapshot.Episodes.All(value => value.MemoryId != memoryId))
                {
                    memoryPending = true;
                    continue;
                }

                NCIncidentMutationResult? mutation = null;
                for (var attempt = 0; attempt < policy.MaximumPersistenceRetries; attempt++)
                {
                    var statement = new NCIncidentWitnessStatement(
                        statementId,
                        candidate.Witness,
                        memoryId,
                        candidate.Kind == NCCityLocalPerceivedEventKind.Killing
                            ? "direct-witness.killing"
                            : "direct-witness.assault",
                        Math.Clamp((int) Math.Round(candidate.Confidence * 1000f), 0, 1000),
                        candidate.IdentifiedParties,
                        now);
                    var stable = $"{incidentId}.{candidate.Witness}.{worldEventId:D}";
                    mutation = await _incidents.AddWitnessStatementAsync(new NCAddWitnessStatementRequest(
                        NCPersistentIdentity.Command("ncpd-witness", "record", stable),
                        incidentId,
                        incident.AggregateVersion,
                        statement,
                        NCPersistentIdentity.Correlation("ncpd-witness", "record", stable),
                        worldEventId,
                        "ncpd-physical-witness-interview"));
                    if (mutation.Status is NCIncidentMutationStatus.Applied or NCIncidentMutationStatus.Duplicate)
                    {
                        recorded++;
                        incident = mutation.Incident ?? await _incidents.GetAsync(incidentId, true) ?? incident;
                        break;
                    }
                    if (mutation.Status != NCIncidentMutationStatus.VersionConflict ||
                        (incident = await _incidents.GetAsync(incidentId, true)) == null)
                    {
                        return new InterviewResult(false, memoryPending, recorded,
                            mutation.Error ?? "ncpd-witness-statement-rejected");
                    }
                }

                if (mutation?.Status is not (NCIncidentMutationStatus.Applied or NCIncidentMutationStatus.Duplicate))
                    return new InterviewResult(false, memoryPending, recorded, "ncpd-witness-version-conflict");
            }

            return new InterviewResult(true, memoryPending, recorded, null);
        }
        catch (Exception exception)
        {
            return new InterviewResult(false, false, 0,
                $"ncpd-witness-exception:{exception.GetType().Name}");
        }
    }

    private bool TryGetPolicy(out NCNcpdInvestigationPolicyPrototype policy)
    {
        if (!_prototypes.TryIndex(DefaultPolicy, out policy!) ||
            policy.WitnessInterviewRadius <= 0f || policy.MaximumWitnessCandidates is < 1 or > 256 ||
            policy.MaximumWitnessesPerInterview is < 1 or > 16 ||
            policy.MaximumWitnessesPerInterview > policy.MaximumWitnessCandidates ||
            !float.IsFinite(policy.MinimumWitnessConfidence) ||
            policy.MinimumWitnessConfidence is < 0f or > 1f || policy.MemoryAvailabilityWait <= TimeSpan.Zero ||
            policy.MaximumPersistenceRetries is < 1 or > 8 ||
            string.IsNullOrWhiteSpace(policy.WitnessQuestionLocKey) ||
            string.IsNullOrWhiteSpace(policy.WitnessAssaultLocKey) ||
            string.IsNullOrWhiteSpace(policy.WitnessKillingLocKey))
        {
            policy = default!;
            return false;
        }
        return true;
    }

    private void OnInterrupted(NCHumanActionInterruptedEvent args)
    {
        if (args.ActionId is InterviewAction or InvestigationInterviewAction)
            _pending.Remove(args.InstanceId);
    }

    private readonly record struct WitnessCandidate(
        EntityUid Entity,
        NCPartyRef Witness,
        NCCityLocalPerceivedEventKind Kind,
        string ActorHandle,
        string TargetHandle,
        float Confidence,
        NCPartyRef[] IdentifiedParties);

    private readonly record struct PendingInterview(
        EntityUid Officer,
        NCCityIncidentId IncidentId,
        Guid SourceWorldEventId,
        WitnessCandidate[] Candidates,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime MemoryDeadline,
        Task<InterviewResult> Operation);

    private readonly record struct InterviewResult(bool Success, bool MemoryPending, int Recorded, string? Error);
}
