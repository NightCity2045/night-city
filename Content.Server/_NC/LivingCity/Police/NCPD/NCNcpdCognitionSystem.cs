// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.CityServices;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Server.Chat.Systems;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Chat;
using Content.Shared.Cuffs;
using Content.Shared.Cuffs.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Storage;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>
/// Bridges NCPD cognition into ordinary gameplay systems. Neural output can select one of these procedures and a
/// perceived subject handle, but only observed incidents, real movement, speech, restraints and pulling can succeed.
/// </summary>
public sealed partial class NCNcpdCognitionSystem : EntitySystem
{
    private static readonly HashSet<string> SupportedActions =
    [
        "ncpd-assess-scene",
        "ncpd-report-incident",
        "ncpd-issue-lawful-order",
        "ncpd-await-compliance",
        "ncpd-pursue-suspect",
        "ncpd-restrain-suspect",
        "ncpd-begin-escort",
    ];

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityLocalPerceptionSystem _perception = default!;
    [Dependency] private NCCityIncidentRegistrySystem _incidents = default!;
    [Dependency] private NCEntityPartyResolverSystem _entityParties = default!;
    [Dependency] private NCCityLocationSystem _locations = default!;
    [Dependency] private NCCognitionSystem _cognition = default!;
    [Dependency] private NCCityNpcSteeringLeaseSystem _steeringLease = default!;
    [Dependency] private NCCityPhysicalExecutionSystem _physicalExecution = default!;
    [Dependency] private HTNSystem _htn = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedCuffableSystem _cuffable = default!;
    [Dependency] private PullingSystem _pulling = default!;
    [Dependency] private NCNcpdAssignmentExecutionSystem _assignmentExecution = default!;
    [Dependency] private NCNcpdUseOfForceSystem _useOfForce = default!;

    private readonly Dictionary<EntityUid, RestraintAttempt> _restraintAttempts = [];
    private readonly Dictionary<Guid, PendingIncidentReport> _pendingReports = [];
    private readonly HashSet<EntityUid> _policeSteering = [];
    private readonly HashSet<EntityUid> _suspendedHumanHtn = [];
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityLocalIncidentObservedEvent>(OnIncidentObserved);
        SubscribeLocalEvent<NCCognitionCollectFactsEvent>(OnCollectFacts);
        SubscribeLocalEvent<NCHumanActionExecutionEvent>(OnExecuteStep);
        SubscribeLocalEvent<NCHumanActionInterruptedEvent>(OnInterrupted);
        SubscribeLocalEvent<NCNcpdOfficerComponent, ComponentShutdown>(OnOfficerShutdown);
    }

    /// <summary>
    /// Adds the professional police interpretation to an incident already witnessed by universal human perception.
    /// The original visibility query and damage subscription remain centralized in NCCityLocalPerceptionSystem.
    /// </summary>
    private void OnIncidentObserved(
        NCCityLocalIncidentObservedEvent args)
    {
        if (args.Kind is not (NCCityLocalPerceivedEventKind.Assault or NCCityLocalPerceivedEventKind.Killing) ||
            !TryComp<NCNcpdOfficerComponent>(args.Observer, out var officer) ||
            !TryGetPolicy(args.Observer, out var policy))
        {
            return;
        }

        var immediateThreat = (args.Target == args.Observer || HasComp<NCNcpdOfficerComponent>(args.Target)) &&
                              !_cuffable.IsCuffed(args.Actor, requireFullyCuffed: false) &&
                              (!TryComp<MobStateComponent>(args.Actor, out var mobState) ||
                               mobState.CurrentState == MobState.Alive);
        if (_perception.ObserveSuspect(
                args.Observer,
                args.Actor,
                args.ObservedAt,
                policy.SuspectObservationLifetime,
                hostile: immediateThreat))
        {
            var observation = TryComp<NCCityLocalPerceptionComponent>(args.Observer, out var perception)
                ? perception.Observations.FirstOrDefault(value => value.EntityUid == args.Actor)
                : default;
            var claim = EnsureComp<NCNcpdSuspectClaimComponent>(args.Observer);
            if (claim.SourceWorldEventId != args.EventId || claim.Suspect != args.Actor)
            {
                if (claim.Suspect.IsValid() && Exists(claim.Suspect) && claim.Suspect != args.Actor)
                {
                    _useOfForce.End(args.Observer, claim.Suspect, NCNcpdSuspectStatus.Lost,
                        NCNcpdForceLevel.Presence, args.ObservedAt, "claim-replaced");
                }
                claim.SourceWorldEventId = args.EventId;
                claim.IncidentId = default;
                claim.Suspect = args.Actor;
                claim.SuspectParty = _entityParties.TryResolve(args.Actor, out var suspectParty)
                    ? suspectParty
                    : default;
                claim.SuspectHandle = observation.SubjectHandle;
                claim.ClaimedAt = args.ObservedAt;
                claim.OrderedAt = null;
                claim.ResponseDeadline = null;
                claim.ResultReason = string.Empty;
            }
            claim.Basis = args.Kind == NCCityLocalPerceivedEventKind.Killing
                ? "witnessed-killing"
                : "witnessed-assault";
            var severity = args.Kind == NCCityLocalPerceivedEventKind.Killing
                ? policy.ObservedKillingSeverity
                : policy.ObservedAssaultSeverity;
            var forceLevel = severity >= policy.LethalThreat
                ? NCNcpdForceLevel.Lethal
                : severity >= policy.NonLethalThreat
                    ? NCNcpdForceLevel.NonLethal
                    : severity >= policy.RestraintThreat
                        ? NCNcpdForceLevel.Restraint
                        : NCNcpdForceLevel.VerbalOrder;
            claim.Status = immediateThreat ? NCNcpdSuspectStatus.Resisting : NCNcpdSuspectStatus.Observed;
            claim.ForceLevel = immediateThreat ? forceLevel : NCNcpdForceLevel.Presence;
            claim.UpdatedAt = args.ObservedAt;
            // The universal vanilla combat compound reads faction hostility. NCPD only supplies the lawful intent.
            if (immediateThreat)
                _useOfForce.Begin(args.Observer, args.Actor, forceLevel);
            // A witnessed incident is immediate and must not wait for the routine cognition cadence.
            _cognition.ForceSchedule(officer.ActorId, args.ObservedAt);
        }
    }

    private void OnCollectFacts(NCCognitionCollectFactsEvent args)
    {
        if (!_runtime.TryGetEntity(args.ActorId, out var entityUid) ||
            !TryComp<NCNcpdOfficerComponent>(entityUid, out _) ||
            !TryComp<NCCityLocalPerceptionComponent>(entityUid, out var perception))
        {
            return;
        }

        args.Sink.Add(new NCCognitionFact(
            "ncpd.on-duty",
            default,
            NCCognitionDomain.Crime,
            "ncpd-officer-projection",
            Math.Max(1, perception.Revision),
            NCCognitionValue.FromBoolean(true),
            args.Now,
            args.Now.Advance(TimeSpan.FromMinutes(1))));
    }

    private void OnExecuteStep(NCHumanActionExecutionEvent args)
    {
        if (!SupportedActions.Contains(args.Step.ActionId) ||
            args.EntityUid is not { } officer || !Exists(officer) ||
            !TryComp<NCNcpdOfficerComponent>(officer, out _) || !TryGetPolicy(officer, out var policy))
        {
            return;
        }

        args.Handled = true;
        _assignmentExecution.YieldToProfessionalAction(officer);
        if (!_clock.TryGetCurrentTime(out _, out var now) ||
            args.Intention.Target.Kind != NCCognitionReferenceKind.StableKey)
        {
            StopPoliceSteering(officer);
            args.Failure = "ncpd-suspect-no-longer-observed";
            return;
        }
        if (!_perception.TryResolveSubjectHandle(args.ActorId, args.Intention.Target.StableId, now, out var suspect))
        {
            StopPoliceSteering(officer);
            if (args.Step.ActionId is "ncpd-pursue-suspect" or "ncpd-restrain-suspect" &&
                TryComp<NCNcpdSuspectClaimComponent>(officer, out var lostClaim) &&
                lostClaim.IncidentId.IsValid && lostClaim.SuspectParty.IsValid)
            {
                _useOfForce.End(officer, lostClaim.Suspect, NCNcpdSuspectStatus.Lost,
                    NCNcpdForceLevel.Presence, now, "suspect-no-longer-observed");
                args.Completed = true;
                return;
            }
            args.Failure = "ncpd-suspect-no-longer-observed";
            return;
        }

        switch (args.Step.ActionId)
        {
            case "ncpd-assess-scene":
                if (!TryComp<NCCityLocalPerceptionComponent>(officer, out var perception) ||
                    !perception.RecentEvents.Any(value => value.ActorHandle == args.Intention.Target.StableId &&
                                                         value.ExpiresAt > now))
                {
                    args.Failure = "ncpd-incident-evidence-expired";
                    return;
                }
                args.Completed = true;
                return;
            case "ncpd-report-incident":
                ExecuteIncidentReport(args, officer, suspect, policy, now);
                return;
            case "ncpd-pursue-suspect":
                if (TryComp<NCNcpdSuspectClaimComponent>(officer, out var pursuitClaim) &&
                    pursuitClaim.Suspect == suspect &&
                    pursuitClaim.Status == NCNcpdSuspectStatus.Incapacitated)
                {
                    args.Failure = "ncpd-suspect-incapacitated";
                    return;
                }
                args.Completed = Approach(officer, suspect, policy.VerbalOrderRange);
                return;
            case "ncpd-issue-lawful-order":
                if (!_interaction.InRangeUnobstructed(officer, suspect, policy.VerbalOrderRange))
                {
                    Approach(officer, suspect, policy.VerbalOrderRange);
                    return;
                }
                if (!TryComp<NCNcpdSuspectClaimComponent>(officer, out var orderClaim) ||
                    orderClaim.Suspect != suspect || orderClaim.SuspectHandle != args.Intention.Target.StableId)
                {
                    args.Failure = "ncpd-suspect-claim-unavailable";
                    return;
                }
                _chat.TrySendInGameICMessage(
                    officer,
                    Loc.GetString(policy.VerbalOrderLocKey),
                    InGameICChatType.Speak,
                    hideChat: false);
                var orderOrigin = _transform.GetMapCoordinates(suspect);
                orderClaim.Status = NCNcpdSuspectStatus.Ordered;
                orderClaim.ForceLevel = NCNcpdForceLevel.VerbalOrder;
                orderClaim.OrderedAt = now;
                orderClaim.ResponseDeadline = now.Advance(policy.ComplianceWindow);
                orderClaim.OrderMap = orderOrigin.MapId;
                orderClaim.OrderPosition = orderOrigin.Position;
                orderClaim.UpdatedAt = now;
                orderClaim.ResultReason = "lawful-order-issued";
                args.Completed = true;
                return;
            case "ncpd-await-compliance":
                EvaluateCompliance(args, officer, suspect, policy, now);
                return;
            case "ncpd-restrain-suspect":
                ExecuteRestraint(officer, suspect, policy, now, args);
                return;
            case "ncpd-begin-escort":
                if (!_cuffable.IsCuffed(suspect))
                {
                    args.Failure = "ncpd-suspect-not-restrained";
                    return;
                }
                args.Completed = _pulling.TryStartPull(officer, suspect);
                if (!args.Completed)
                    args.Failure = "ncpd-escort-rejected-by-pulling-system";
                else
                {
                    var incidentId = TryComp<NCNcpdIncidentReportComponent>(officer, out var report) &&
                                     report.Status == NCNcpdIncidentReportStatus.Persisted
                        ? report.IncidentId
                        : default;
                    RaiseLocalEvent(new NCNcpdEscortStartedEvent(officer, suspect, incidentId, now));
                }
                return;
        }

        args.Failure = $"ncpd-action-unsupported:{args.Step.ActionId}";
    }

    /// <summary>
    /// Classifies only observable post-order behavior. It does not force a player or NPC to surrender and does not
    /// manufacture a dice result: movement, hostility, cuffs and mob state remain authoritative.
    /// </summary>
    private void EvaluateCompliance(
        NCHumanActionExecutionEvent args,
        EntityUid officer,
        EntityUid suspect,
        NCNcpdUseOfForcePolicyPrototype policy,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now)
    {
        StopPoliceSteering(officer);
        SuspendHumanHtn(officer);
        _physicalExecution.SetInterrupt(officer, NCCityPhysicalInterruptFlags.Police, true);
        if (!TryComp<NCNcpdSuspectClaimComponent>(officer, out var claim) || claim.Suspect != suspect ||
            claim.SuspectHandle != args.Intention.Target.StableId || claim.ResponseDeadline is not { } deadline)
        {
            args.Failure = "ncpd-lawful-order-state-unavailable";
            return;
        }

        if (_cuffable.IsCuffed(suspect, requireFullyCuffed: false))
        {
            _useOfForce.End(officer, suspect, NCNcpdSuspectStatus.Restrained,
                NCNcpdForceLevel.Restraint, now, "suspect-already-restrained");
            args.Completed = true;
            return;
        }
        if (TryComp<MobStateComponent>(suspect, out var mobState) && mobState.CurrentState != MobState.Alive)
        {
            _useOfForce.End(officer, suspect, NCNcpdSuspectStatus.Incapacitated,
                NCNcpdForceLevel.Presence, now, "suspect-incapacitated");
            args.Failure = "ncpd-suspect-incapacitated";
            return;
        }
        if (TryComp<NCCityLocalPerceptionComponent>(officer, out var perception) &&
            perception.Observations.Any(value => value.EntityUid == suspect &&
                                                 value.Kind == NCCityLocalObservationKind.Hostile &&
                                                 value.ExpiresAt > now))
        {
            UpdateClaim(claim, NCNcpdSuspectStatus.Resisting, NCNcpdForceLevel.NonLethal, now,
                "suspect-is-an-active-threat");
            args.Failure = "ncpd-suspect-resisting";
            return;
        }
        if (now < deadline)
            return;

        var current = _transform.GetMapCoordinates(suspect);
        if (current.MapId != claim.OrderMap)
        {
            UpdateClaim(claim, NCNcpdSuspectStatus.Fleeing, NCNcpdForceLevel.Restraint, now,
                "suspect-left-order-map");
            args.Completed = true;
            return;
        }

        var displacementSquared = Vector2.DistanceSquared(current.Position, claim.OrderPosition);
        var inOrderRange = _interaction.InRangeUnobstructed(officer, suspect, policy.VerbalOrderRange);
        if (displacementSquared <= policy.ComplianceMovementTolerance * policy.ComplianceMovementTolerance &&
            inOrderRange)
        {
            _useOfForce.End(officer, suspect, NCNcpdSuspectStatus.Complying,
                NCNcpdForceLevel.Restraint, now, "suspect-remained-in-place");
        }
        else if (!inOrderRange || displacementSquared >= policy.FlightDistance * policy.FlightDistance)
        {
            UpdateClaim(claim, NCNcpdSuspectStatus.Fleeing, NCNcpdForceLevel.Restraint, now,
                "suspect-fled-order-position");
        }
        else
        {
            UpdateClaim(claim, NCNcpdSuspectStatus.Refused, NCNcpdForceLevel.Restraint, now,
                "suspect-did-not-comply");
        }
        args.Completed = true;
    }

    private static void UpdateClaim(
        NCNcpdSuspectClaimComponent claim,
        NCNcpdSuspectStatus status,
        NCNcpdForceLevel forceLevel,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now,
        string reason)
    {
        claim.Status = status;
        claim.ForceLevel = forceLevel;
        claim.UpdatedAt = now;
        claim.ResultReason = reason;
    }

    private bool Approach(EntityUid officer, EntityUid suspect, float range)
    {
        if (_transform.InRange(Transform(officer).Coordinates, Transform(suspect).Coordinates, range))
        {
            StopPoliceSteering(officer);
            return true;
        }

        // Profession code temporarily owns locomotion, but the actor keeps the same universal human HTN. Pausing
        // that root avoids two NPCSteering writers until this concrete police step has reached its target.
        SuspendHumanHtn(officer);
        _physicalExecution.SetInterrupt(officer, NCCityPhysicalInterruptFlags.Police, true);
        // Entity-relative coordinates follow a moving suspect while vanilla steering performs collision/path checks.
        if (_policeSteering.Contains(officer))
            return false;
        if (!_steeringLease.Acquire(officer, "ncpd-physical-action"))
        {
            ResumeHumanHtn(officer);
            _physicalExecution.SetInterrupt(officer, NCCityPhysicalInterruptFlags.Police, false);
            return false;
        }
        _policeSteering.Add(officer);
        _steering.Register(officer, new EntityCoordinates(suspect, Vector2.Zero));
        return false;
    }

    private void ExecuteRestraint(
        EntityUid officer,
        EntityUid suspect,
        NCNcpdUseOfForcePolicyPrototype policy,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now,
        NCHumanActionExecutionEvent args)
    {
        if (_cuffable.IsCuffed(suspect))
        {
            _restraintAttempts.Remove(officer);
            _useOfForce.End(officer, suspect, NCNcpdSuspectStatus.Restrained,
                NCNcpdForceLevel.Restraint, now, "restraints-confirmed");
            args.Completed = true;
            return;
        }
        if (TryComp<MobStateComponent>(suspect, out var mobState) && mobState.CurrentState != MobState.Alive)
        {
            _useOfForce.End(officer, suspect, NCNcpdSuspectStatus.Incapacitated,
                NCNcpdForceLevel.Presence, now, "suspect-incapacitated-before-restraint");
            args.Failure = "ncpd-suspect-incapacitated";
            return;
        }
        if (!Approach(officer, suspect, policy.RestraintRange))
            return;
        if (_restraintAttempts.TryGetValue(officer, out var pending) && pending.Suspect == suspect &&
            now < pending.RetryAt)
        {
            return;
        }
        if (!TryReadyRestraints(officer, out var restraints))
        {
            args.Failure = "ncpd-restraints-unavailable";
            return;
        }

        _restraintAttempts[officer] = new RestraintAttempt(suspect, now.Advance(policy.RestraintRetryDelay));
        if (!_cuffable.TryCuffing(officer, suspect, restraints))
            args.Failure = "ncpd-restraint-rejected-by-cuff-system";
    }

    private bool TryReadyRestraints(EntityUid officer, out EntityUid restraints)
    {
        foreach (var held in _hands.EnumerateHeld(officer))
        {
            if (HasComp<HandcuffComponent>(held))
            {
                restraints = held;
                return true;
            }
        }

        if (_inventory.TryGetSlotEntity(officer, "belt", out var belt) &&
            TryComp<StorageComponent>(belt, out var storage))
        {
            foreach (var stored in storage.Container.ContainedEntities.ToArray())
            {
                if (HasComp<HandcuffComponent>(stored) && _hands.TryPickupAnyHand(officer, stored))
                {
                    restraints = stored;
                    return true;
                }
            }
        }

        restraints = EntityUid.Invalid;
        return false;
    }

    private void ExecuteIncidentReport(
        NCHumanActionExecutionEvent args,
        EntityUid officer,
        EntityUid suspect,
        NCNcpdUseOfForcePolicyPrototype policy,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now)
    {
        if (!_pendingReports.TryGetValue(args.InstanceId, out var pending))
        {
            if (!TryGetObservedIncident(officer, args.Intention.Target.StableId, now, out var observed))
            {
                args.Failure = "ncpd-incident-evidence-expired";
                return;
            }
            if (!_entityParties.TryResolve(officer, out var reporter) ||
                !_entityParties.TryResolve(suspect, out var suspectParty))
            {
                args.Failure = "ncpd-incident-party-identity-unavailable";
                return;
            }

            _locations.EnsureTracked(observed.TargetEntity);
            _locations.EnsureTracked(officer);
            var location = _locations.TryGetContext(observed.TargetEntity, out var victimLocation)
                ? victimLocation.MostSpecific
                : _locations.TryGetContext(officer, out var officerLocation)
                    ? officerLocation.MostSpecific
                    : default;
            if (!location.IsValid)
            {
                args.Failure = "ncpd-incident-city-location-unavailable";
                return;
            }

            var participants = new List<NCIncidentParticipant>
            {
                new(reporter, NCIncidentParticipantRole.Reporter, true),
                new(suspectParty, NCIncidentParticipantRole.Suspect, true),
            };
            if (_entityParties.TryResolve(observed.TargetEntity, out var victimParty) &&
                participants.All(value => value.Party != victimParty))
            {
                participants.Add(new NCIncidentParticipant(victimParty, NCIncidentParticipantRole.Victim, true));
            }

            var eventKind = observed.Kind == NCCityLocalPerceivedEventKind.Killing ? "killing" : "assault";
            var incidentId = NCPersistentIdentity.CityIncident(observed.EventId, eventKind);
            var severity = observed.Kind == NCCityLocalPerceivedEventKind.Killing
                ? policy.ObservedKillingSeverity
                : policy.ObservedAssaultSeverity;
            var request = new NCCreateIncidentRequest(
                args.InstanceId,
                incidentId,
                NCCityIncidentKind.Violence,
                severity,
                location,
                participants,
                new NCIncidentCause(observed.EventId, "physical-perception", eventKind,
                    $"{observed.ActorHandle}->{observed.TargetHandle}"),
                NCIncidentMaterializationPolicy.AlwaysPhysical,
                null,
                reporter,
                args.IntentionId,
                observed.EventId,
                "ncpd-physical-report");
            var submittingReport = EnsureComp<NCNcpdIncidentReportComponent>(officer);
            submittingReport.ReportId = args.InstanceId;
            submittingReport.SourceWorldEventId = observed.EventId;
            submittingReport.IncidentId = incidentId;
            submittingReport.SuspectHandle = args.Intention.Target.StableId;
            submittingReport.ReportedAt = now;
            submittingReport.Status = NCNcpdIncidentReportStatus.Submitting;
            submittingReport.Failure = string.Empty;
            pending = new PendingIncidentReport(officer, CreateIncidentAsync(request, observed.EventId, incidentId));
            _pendingReports.Add(args.InstanceId, pending);
        }

        if (!pending.Operation.IsCompleted)
            return;

        _pendingReports.Remove(args.InstanceId);
        var result = pending.Operation.GetAwaiter().GetResult();
        var report = EnsureComp<NCNcpdIncidentReportComponent>(officer);
        report.ReportId = args.InstanceId;
        report.SourceWorldEventId = result.SourceWorldEventId;
        report.IncidentId = result.IncidentId;
        report.SuspectHandle = args.Intention.Target.StableId;
        report.ReportedAt = now;
        report.Status = result.Success
            ? NCNcpdIncidentReportStatus.Persisted
            : NCNcpdIncidentReportStatus.Rejected;
        report.Failure = result.Error ?? string.Empty;
        if (!result.Success)
        {
            args.Failure = result.Error ?? "ncpd-incident-persistence-rejected";
            return;
        }

        if (TryComp<NCNcpdSuspectClaimComponent>(officer, out var claim) && claim.Suspect == suspect)
        {
            claim.IncidentId = result.IncidentId;
            claim.UpdatedAt = now;
        }

        RaiseLocalEvent(new NCNcpdIncidentReportedEvent(
            report.ReportId,
            report.SourceWorldEventId,
            report.IncidentId,
            officer,
            report.SuspectHandle,
            now));
        args.Completed = true;
    }

    private bool TryGetObservedIncident(EntityUid officer, string suspectHandle,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now, out NCCityLocalPerceivedEvent observed)
    {
        observed = default;
        if (!TryComp<NCCityLocalPerceptionComponent>(officer, out var perception))
            return false;
        foreach (var candidate in perception.RecentEvents)
        {
            if (candidate.ActorHandle != suspectHandle || candidate.ExpiresAt <= now ||
                candidate.Kind is not (NCCityLocalPerceivedEventKind.Assault or NCCityLocalPerceivedEventKind.Killing))
                continue;
            observed = candidate;
            return true;
        }
        return false;
    }

    private async Task<IncidentReportActionResult> CreateIncidentAsync(
        NCCreateIncidentRequest request,
        Guid sourceWorldEventId,
        NCCityIncidentId incidentId)
    {
        try
        {
            var result = await _incidents.CreateAsync(request);
            return new IncidentReportActionResult(
                result.Status is NCIncidentMutationStatus.Applied or NCIncidentMutationStatus.Duplicate,
                sourceWorldEventId,
                incidentId,
                result.Error);
        }
        catch (Exception exception)
        {
            return new IncidentReportActionResult(false, sourceWorldEventId, incidentId,
                $"ncpd-incident-exception:{exception.GetType().Name}");
        }
    }

    private bool TryGetPolicy(EntityUid officer, out NCNcpdUseOfForcePolicyPrototype policy)
    {
        policy = default!;
        return TryComp<NCNcpdOfficerComponent>(officer, out var component) &&
               _prototypes.TryIndex(component.RoleProfile, out NCNcpdRoleProfilePrototype? role) && role != null &&
               _prototypes.TryIndex(role.ForcePolicy, out policy!);
    }

    private void OnOfficerShutdown(Entity<NCNcpdOfficerComponent> officer, ref ComponentShutdown args)
    {
        // Keep component shutdown ownership centralized and let other NCPD systems clean up through a domain event.
        RaiseLocalEvent(new NCNcpdOfficerRuntimeRemovedEvent(officer.Owner));
        _restraintAttempts.Remove(officer.Owner);
        foreach (var instanceId in _pendingReports.Where(value => value.Value.Officer == officer.Owner)
                     .Select(value => value.Key).ToArray())
            _pendingReports.Remove(instanceId);
        StopPoliceSteering(officer.Owner, false);
        _suspendedHumanHtn.Remove(officer.Owner);
        if (Exists(officer.Owner) && TryComp<NCNcpdSuspectClaimComponent>(officer.Owner, out var claim))
        {
            if (_clock.TryGetCurrentTime(out _, out var now) && claim.Suspect.IsValid())
            {
                _useOfForce.End(officer.Owner, claim.Suspect, NCNcpdSuspectStatus.Lost,
                    NCNcpdForceLevel.Presence, now, "officer-projection-ended");
            }
            RemCompDeferred<NCNcpdSuspectClaimComponent>(officer.Owner);
        }
    }

    private void OnInterrupted(NCHumanActionInterruptedEvent args)
    {
        if (args.ActionId.StartsWith("ncpd-", StringComparison.Ordinal) && args.EntityUid is { } officer)
        {
            _restraintAttempts.Remove(officer);
            _pendingReports.Remove(args.InstanceId);
            StopPoliceSteering(officer);
        }
    }

    private void StopPoliceSteering(EntityUid officer, bool restoreHumanHtn = true)
    {
        if (_policeSteering.Remove(officer) && Exists(officer))
        {
            _steering.Unregister(officer);
            _steeringLease.Release(officer, "ncpd-physical-action");
        }
        if (!restoreHumanHtn)
            return;
        _physicalExecution.SetInterrupt(officer, NCCityPhysicalInterruptFlags.Police, false);
        ResumeHumanHtn(officer);
    }

    private void SuspendHumanHtn(EntityUid officer)
    {
        if (_suspendedHumanHtn.Contains(officer) ||
            !TryComp<HTNComponent>(officer, out var htn) || !htn.Enabled)
        {
            return;
        }
        _htn.SetHTNEnabled((officer, htn), false);
        _suspendedHumanHtn.Add(officer);
    }

    private void ResumeHumanHtn(EntityUid officer)
    {
        if (!_suspendedHumanHtn.Remove(officer) || !Exists(officer) ||
            !TryComp<HTNComponent>(officer, out var htn))
        {
            return;
        }
        // The component's own YAML cooldown is the single tuning source for every profession.
        _htn.SetHTNEnabled((officer, htn), true, htn.PlanCooldown);
    }

    private readonly record struct RestraintAttempt(
        EntityUid Suspect,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime RetryAt);

    private readonly record struct PendingIncidentReport(EntityUid Officer, Task<IncidentReportActionResult> Operation);

    private readonly record struct IncidentReportActionResult(
        bool Success,
        Guid SourceWorldEventId,
        NCCityIncidentId IncidentId,
        string? Error);
}

/// <summary>Typed dispatch boundary emitted after a physical officer reports an observed incident.</summary>
public sealed class NCNcpdIncidentReportedEvent(
    Guid reportId,
    Guid sourceWorldEventId,
    NCCityIncidentId incidentId,
    EntityUid officer,
    string suspectHandle,
    Content.Shared._NC.Persistence.Time.NCActiveWorldTime reportedAt) : EntityEventArgs
{
    public Guid ReportId { get; } = reportId;
    public Guid SourceWorldEventId { get; } = sourceWorldEventId;
    public NCCityIncidentId IncidentId { get; } = incidentId;
    public EntityUid Officer { get; } = officer;
    public string SuspectHandle { get; } = suspectHandle;
    public Content.Shared._NC.Persistence.Time.NCActiveWorldTime ReportedAt { get; } = reportedAt;
}
