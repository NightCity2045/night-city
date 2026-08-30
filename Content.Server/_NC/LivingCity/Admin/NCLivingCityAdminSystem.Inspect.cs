// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.City.Connectivity;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Admin;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Police;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Admin;

public sealed partial class NCLivingCityAdminSystem
{
    private Task<InspectionResult> InspectAsync(NCLivingCityAdminInspectRequest request) => request.Kind switch
    {
        NCLivingCityAdminInspectKind.Actor => InspectActorAsync(request.Target),
        NCLivingCityAdminInspectKind.Cognition => InspectCognitionAsync(request.Target),
        NCLivingCityAdminInspectKind.ActorMaterializationRequest =>
            Task.FromResult(InspectMaterializationRequest(request.Target)),
        NCLivingCityAdminInspectKind.ActorDematerializationRequest =>
            Task.FromResult(InspectDematerializationRequest(request.Target)),
        NCLivingCityAdminInspectKind.ActorRelevance => Task.FromResult(InspectRelevance(request.Target)),
        NCLivingCityAdminInspectKind.Group => InspectGroupAsync(request.Target),
        NCLivingCityAdminInspectKind.GroupMembership => InspectMembershipAsync(request.Target),
        NCLivingCityAdminInspectKind.GroupPhysicalizationRequest =>
            Task.FromResult(InspectGroupPhysicalizationRequest(request.Target)),
        NCLivingCityAdminInspectKind.GroupFormation => Task.FromResult(InspectGroupFormation(request.Target)),
        NCLivingCityAdminInspectKind.ZoneState => InspectZoneStateAsync(request.Target),
        NCLivingCityAdminInspectKind.Party => InspectPartyAsync(request.Target),
        NCLivingCityAdminInspectKind.Account => InspectAccountAsync(request.Target),
        NCLivingCityAdminInspectKind.Transaction => InspectTransactionAsync(request.Target),
        NCLivingCityAdminInspectKind.Employment => InspectEmploymentAsync(request.Target),
        NCLivingCityAdminInspectKind.Relationship => InspectRelationshipAsync(request.Target, request.SecondaryTarget),
        NCLivingCityAdminInspectKind.Reputation => InspectReputationAsync(request.Target),
        NCLivingCityAdminInspectKind.Memory => InspectMemoryAsync(request.Target),
        NCLivingCityAdminInspectKind.SocialProfile => InspectSocialProfileAsync(request.Target),
        NCLivingCityAdminInspectKind.RumorBook => InspectRumorBookAsync(request.Target),
        NCLivingCityAdminInspectKind.SocialObligation => InspectSocialObligationAsync(request.Target),
        NCLivingCityAdminInspectKind.Conversation => Task.FromResult(InspectConversation(request.Target)),
        NCLivingCityAdminInspectKind.EconomicStockNode => InspectEconomicStockNodeAsync(request.Target),
        NCLivingCityAdminInspectKind.EconomicCommodityBatch => InspectEconomicCommodityBatchAsync(request.Target),
        NCLivingCityAdminInspectKind.EconomicItem => InspectEconomicItemAsync(request.Target),
        NCLivingCityAdminInspectKind.EconomicQuote => InspectEconomicQuoteAsync(request.Target),
        NCLivingCityAdminInspectKind.EconomicReservation => InspectEconomicReservationAsync(request.Target),
        NCLivingCityAdminInspectKind.EconomicTransfer => InspectEconomicTransferAsync(request.Target),
        NCLivingCityAdminInspectKind.EconomicFulfillment => InspectEconomicFulfillmentAsync(request.Target),
        NCLivingCityAdminInspectKind.EconomicReconciliation => InspectEconomicReconciliationAsync(request.Target),
        NCLivingCityAdminInspectKind.MarketListing => InspectMarketListingAsync(request.Target),
        NCLivingCityAdminInspectKind.MarketRequest => InspectMarketRequestAsync(request.Target),
        NCLivingCityAdminInspectKind.Business => InspectBusinessAsync(request.Target),
        NCLivingCityAdminInspectKind.ProductionJob => InspectProductionJobAsync(request.Target),
        NCLivingCityAdminInspectKind.Shipment => InspectShipmentAsync(request.Target),
        NCLivingCityAdminInspectKind.Property => InspectPropertyAsync(request.Target),
        NCLivingCityAdminInspectKind.PropertyContents => InspectPropertyContentsAsync(request.Target),
        NCLivingCityAdminInspectKind.PoliceFine => InspectPoliceFineAsync(request.Target),
        NCLivingCityAdminInspectKind.PoliceWanted => InspectPoliceWantedAsync(request.Target),
        NCLivingCityAdminInspectKind.OutboxMessage => InspectOutboxMessageAsync(request.Target),
        NCLivingCityAdminInspectKind.CityZone => Task.FromResult(InspectCityZone(request.Target)),
        NCLivingCityAdminInspectKind.CityConnectivityZone =>
            Task.FromResult(InspectCityConnectivityZone(request.Target)),
        NCLivingCityAdminInspectKind.CityConnectivityValidation =>
            Task.FromResult(InspectCityConnectivityValidation()),
        NCLivingCityAdminInspectKind.Incident => InspectIncidentAsync(request.Target),
        NCLivingCityAdminInspectKind.CityService => InspectCityServiceAsync(request.Target),
        NCLivingCityAdminInspectKind.ServiceDispatch => InspectServiceDispatchAsync(request.Target),
        NCLivingCityAdminInspectKind.Faction => InspectFactionAsync(request.Target),
        NCLivingCityAdminInspectKind.DistrictSafety => Task.FromResult(InspectDistrictSafety(request.Target)),
        NCLivingCityAdminInspectKind.CityNews => InspectCityNewsAsync(request.Target),
        NCLivingCityAdminInspectKind.Estate => InspectEstateAsync(request.Target),
        NCLivingCityAdminInspectKind.NeuralRuntime => Task.FromResult(InspectNeuralRuntime()),
        _ => Task.FromResult(Fail("unsupported-inspection-kind")),
    };

    private async Task<InspectionResult> InspectActorAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-actor-id");
        var actorId = new NCActorId(raw);
        var actorTask = _actors.GetAsync(actorId, true);
        var healthTask = _actorDomains.GetHealthAsync(actorId, true);
        var inventoryTask = _actorDomains.GetInventoryAsync(actorId, true);
        await Task.WhenAll(actorTask, healthTask, inventoryTask);
        var actor = await actorTask;
        if (actor == null)
            return Fail("actor-not-found");

        var fields = new FieldBuilder();
        var snapshot = actor.Snapshot;
        fields.Add("identity", "actor-id", actorId);
        fields.Add("identity", "full-name", snapshot.Identity.FullName);
        fields.Add("identity", "aliases", string.Join(", ", snapshot.Identity.Aliases));
        fields.Add("identity", "archetype", snapshot.Identity.ArchetypeId);
        if (_prototypes.TryIndex(new ProtoId<NCCityActorArchetypePrototype>(snapshot.Identity.ArchetypeId),
                out NCCityActorArchetypePrototype? archetype) && archetype != null)
        {
            var narrativeProfile = snapshot.Identity.NarrativeActorProfileId ?? archetype.NarrativeActorProfile?.Id;
            fields.Add("identity", "population-class",
                narrativeProfile == null ? "Resident" : "Actor");
            fields.Add("identity", "narrative-profile", narrativeProfile ?? "none");
        }
        fields.Add("identity", "aggregate-version", actor.AggregateVersion);
        fields.Add("identity", "data-version", snapshot.Identity.DataVersion);
        fields.Add("lifecycle", "status", snapshot.Lifecycle.Status);
        fields.Add("lifecycle", "frozen", snapshot.Lifecycle.Frozen);
        fields.Add("lifecycle", "died-at", snapshot.Lifecycle.DiedAt?.Elapsed);
        fields.Add("lifecycle", "archived-at", snapshot.Lifecycle.ArchivedAt?.Elapsed);
        fields.Add("location", "current-zone", snapshot.Location.CurrentZone);
        fields.Add("location", "destination-zone", snapshot.Location.DestinationZone);
        fields.Add("location", "home-zone", snapshot.Location.HomeZone);
        fields.Add("location", "work-zone", snapshot.Location.WorkZone);
        fields.Add("location", "travel-phase", snapshot.Location.TravelPhase);
        fields.Add("location", "travel-mode", snapshot.Location.TravelMode);
        fields.Add("location", "expected-arrival", snapshot.Location.ExpectedArrivalAt?.Elapsed);
        fields.Add("residence", "assigned", snapshot.Location.Residence is { ResidenceId.IsValid: true });
        if (snapshot.Location.Residence is { ResidenceId.IsValid: true } residence)
        {
            fields.Add("residence", "residence-id", residence.ResidenceId);
            fields.Add("residence", "profile", residence.ProfileId);
            fields.Add("residence", "kind", residence.Kind);
            fields.Add("residence", "quality", residence.Quality);
            fields.Add("residence", "district", residence.DistrictZone);
            fields.Add("residence", "local-zone", residence.LocalZone);
            fields.Add("residence", "weekly-cost", residence.WeeklyCost);
        }
        fields.Add("schedule", "routine", snapshot.Schedule.RoutineProfileId);
        fields.Add("schedule", "activity", snapshot.Schedule.CurrentActivity);
        fields.Add("schedule", "goal", snapshot.Schedule.CurrentGoal);
        fields.Add("schedule", "availability", snapshot.Schedule.Availability);
        fields.Add("schedule", "presence", snapshot.Schedule.PresenceMode);
        fields.Add("schedule", "next-update", snapshot.Schedule.NextUpdateAt.Elapsed);
        fields.Add("schedule", "commitment-count", snapshot.Schedule.Commitments.Length);
        fields.Add("physical-execution", "materialized", _actorRuntime.TryGetEntity(actorId, out var runtimeEntity));
        if (_physicalExecution.TryGetSnapshot(actorId, out var physical))
        {
            fields.Add("physical-execution", "entity", physical.EntityUid);
            fields.Add("physical-execution", "goal-id", physical.GoalId);
            fields.Add("physical-execution", "actor-version", physical.ActorVersion);
            fields.Add("physical-execution", "state", physical.State);
            fields.Add("physical-execution", "mode", physical.Mode);
            fields.Add("physical-execution", "behavior-owner", physical.BehaviorOwner);
            fields.Add("physical-execution", "interrupts", physical.Interrupts);
            fields.Add("physical-execution", "destination-zone", physical.DestinationZone);
            fields.Add("physical-execution", "destination", physical.Destination);
            fields.Add("physical-execution", "activity", physical.Activity);
            fields.Add("physical-execution", "goal", physical.Goal);
            fields.Add("physical-execution", "waypoint", $"{physical.WaypointIndex}/{physical.WaypointCount}");
            fields.Add("physical-execution", "replans", physical.ReplanAttempts);
            fields.Add("physical-execution", "rejected-room-portals", physical.RejectedRoomPortalCount);
            fields.Add("physical-execution", "rejected-city-portals", physical.RejectedCityPortalCount);
            fields.Add("physical-execution", "city-graph-version", physical.PlanVersion.CityGraphVersion);
            fields.Add("physical-execution", "room-graph-version", physical.PlanVersion.RoomGraphVersion);
            fields.Add("physical-execution", "last-failure", physical.LastFailure);
            if (TryComp<NCCityLocalPerceptionComponent>(physical.EntityUid, out var perception))
            {
                fields.Add("local-perception", "profile", perception.Profile);
                fields.Add("local-perception", "revision", perception.Revision);
                fields.Add("local-perception", "last-evaluated", perception.LastEvaluatedAt.Elapsed);
                fields.Add("local-perception", "next-evaluation", perception.NextEvaluationAt.Elapsed);
                fields.Add("local-perception", "observations", perception.Observations.Count);
                fields.Add("local-perception", "hostiles", perception.HostileCount);
                fields.Add("local-perception", "critical", perception.CriticalCount);
                fields.Add("local-perception", "nearest-hostile", perception.NearestHostile);
                fields.Add("local-perception", "nearest-critical", perception.NearestCritical);
            }
            if (TryComp<NCNcpdSuspectClaimComponent>(physical.EntityUid, out var suspectClaim))
            {
                fields.Add("ncpd-suspect", "source-world-event", suspectClaim.SourceWorldEventId);
                fields.Add("ncpd-suspect", "incident-id", suspectClaim.IncidentId);
                fields.Add("ncpd-suspect", "entity", suspectClaim.Suspect);
                fields.Add("ncpd-suspect", "handle", suspectClaim.SuspectHandle);
                fields.Add("ncpd-suspect", "basis", suspectClaim.Basis);
                fields.Add("ncpd-suspect", "status", suspectClaim.Status);
                fields.Add("ncpd-suspect", "force-level", suspectClaim.ForceLevel);
                fields.Add("ncpd-suspect", "claimed-at", suspectClaim.ClaimedAt.Elapsed);
                fields.Add("ncpd-suspect", "updated-at", suspectClaim.UpdatedAt.Elapsed);
                fields.Add("ncpd-suspect", "ordered-at", suspectClaim.OrderedAt?.Elapsed.ToString() ?? "none");
                fields.Add("ncpd-suspect", "response-deadline",
                    suspectClaim.ResponseDeadline?.Elapsed.ToString() ?? "none");
                fields.Add("ncpd-suspect", "result", suspectClaim.ResultReason);
            }
            if (TryComp<NCNcpdEvidenceSearchComponent>(physical.EntityUid, out var evidenceSearch))
                AddNcpdEvidenceSearch(fields, evidenceSearch);
            if (TryComp<NCNcpdEvidenceSubmissionComponent>(physical.EntityUid, out var evidenceSubmission))
                AddNcpdEvidenceSubmission(fields, evidenceSubmission);
            if (TryComp<NCNcpdWitnessInterviewComponent>(physical.EntityUid, out var witnessInterview))
                AddNcpdWitnessInterview(fields, witnessInterview);
            if (TryComp<NCNcpdInvestigationComponent>(physical.EntityUid, out var investigation))
                AddNcpdInvestigation(fields, investigation);
            foreach (var trace in physical.Trace.TakeLast(32))
            {
                var section = $"physical-trace-{trace.Sequence}";
                fields.Add(section, "at", trace.At.Elapsed);
                fields.Add(section, "state", trace.State);
                fields.Add(section, "event", trace.Event);
                fields.Add(section, "waypoint", trace.WaypointIndex);
                fields.Add(section, "waypoint-kind", trace.WaypointKind?.ToString() ?? "none");
                fields.Add(section, "detail", trace.Detail);
            }
        }
        foreach (var commitment in snapshot.Schedule.Commitments.Take(32))
        {
            var section = $"commitment-{commitment.CommitmentId}";
            fields.Add(section, "activity", commitment.Activity);
            fields.Add(section, "goal", commitment.Goal);
            fields.Add(section, "zone", commitment.TargetZone);
            fields.Add(section, "window", $"{commitment.StartsAt.Elapsed}..{commitment.EndsAt.Elapsed}");
            fields.Add(section, "availability", commitment.Availability);
            fields.Add(section, "priority", commitment.Priority);
        }

        var health = await healthTask;
        fields.Add("health", "present", health != null);
        if (health != null)
        {
            fields.Add("health", "aggregate-version", health.AggregateVersion);
            fields.Add("health", "condition", health.Snapshot.Summary.OverallCondition);
            fields.Add("health", "bleeding", health.Snapshot.Summary.Bleeding);
            fields.Add("health", "mobility", health.Snapshot.Summary.Mobility);
            fields.Add("health", "consciousness", health.Snapshot.Summary.Consciousness);
            fields.Add("health", "critical-wounds", health.Snapshot.Summary.CriticalWoundIds.Length);
            fields.Add("health", "treatment", health.Snapshot.Summary.ActiveTreatment);
            fields.Add("health", "cyberware", health.Snapshot.Summary.Cyberware);
            fields.Add("health", "full-payload-captured", health.Snapshot.MedicalStatePayload != null);
        }

        var inventory = await inventoryTask;
        fields.Add("inventory", "present", inventory != null);
        if (inventory != null)
        {
            fields.Add("inventory", "aggregate-version", inventory.AggregateVersion);
            fields.Add("inventory", "source-authority-version", inventory.Snapshot.SourceAuthorityVersion);
            fields.Add("inventory", "unique-items", inventory.Snapshot.Summary.UniqueItems.Length);
            fields.Add("inventory", "weapons", inventory.Snapshot.Summary.Weapons.Length);
            fields.Add("inventory", "documents", inventory.Snapshot.Summary.Documents.Length);
            fields.Add("inventory", "credentials", inventory.Snapshot.Summary.Credentials.Length);
            fields.Add("inventory", "commodities", inventory.Snapshot.Summary.Commodities.Length);
            fields.Add("inventory", "installed-cyberware", inventory.Snapshot.Summary.InstalledCyberware.Length);
            fields.Add("inventory", "equipped-loadout", inventory.Snapshot.Summary.EquippedLoadoutId);
        }
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectCognitionAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-actor-id");
        var actorId = new NCActorId(raw);
        var cognitionTask = _cognitionRegistry.GetCognitionAsync(actorId, true);
        var beliefsTask = _cognitionRegistry.GetBeliefsAsync(actorId, true);
        await Task.WhenAll(cognitionTask, beliefsTask);
        var cognition = await cognitionTask;
        var beliefs = await beliefsTask;
        if (cognition == null || beliefs == null)
            return Fail("cognition-not-found");

        var fields = new FieldBuilder();
        fields.Add("persistent", "actor-id", actorId);
        fields.Add("persistent", "cognition-version", cognition.AggregateVersion);
        fields.Add("persistent", "belief-set-version", beliefs.AggregateVersion);
        fields.Add("persistent", "belief-link-version", cognition.Snapshot.BeliefSetVersion);
        fields.Add("persistent", "decision-policy", cognition.Snapshot.DecisionPolicyId);
        fields.Add("persistent", "capability-profile", cognition.Snapshot.CapabilityProfileId);
        fields.Add("persistent", "routing-profile", cognition.Snapshot.RoutingProfileId);
        fields.Add("persistent", "next-decision", cognition.Snapshot.NextDecisionAt.Elapsed);
        fields.Add("persistent", "belief-count", beliefs.Snapshot.Beliefs.Length);
        AddIntention(fields, "persistent-intention", cognition.Snapshot.CurrentIntention);

        if (!_cognition.TryGetSnapshot(actorId, out var runtime))
        {
            fields.Add("runtime", "present", false);
            return Ok(fields);
        }
        fields.Add("runtime", "present", true);
        fields.Add("runtime", "entity", runtime.EntityUid?.ToString() ?? "abstract");
        fields.Add("runtime", "actor-version", runtime.ActorVersion);
        fields.Add("runtime", "last-decision", runtime.LastDecisionAt.Elapsed);
        fields.Add("runtime", "next-decision", runtime.NextDecisionAt.Elapsed);
        fields.Add("runtime", "fact-count", runtime.Facts.Length);
        fields.Add("runtime", "desire-count", runtime.Desires.Length);
        fields.Add("runtime", "last-failure", runtime.LastFailure ?? "none");
        fields.Add("runtime", "human-state", runtime.HumanActivity.State);
        fields.Add("runtime", "idle-reason", runtime.HumanActivity.Reason);
        fields.Add("runtime", "fallback-action", runtime.HumanActivity.FallbackAction);
        fields.Add("runtime", "next-wake", runtime.HumanActivity.NextWakeAt.Elapsed);
        AddIntention(fields, "runtime-intention", runtime.Intention);
        if (runtime.EntityUid is { } physical &&
            TryComp<NCNcpdIncidentReportComponent>(physical, out var policeReport))
        {
            fields.Add("ncpd-incident", "report-id", policeReport.ReportId);
            fields.Add("ncpd-incident", "source-world-event", policeReport.SourceWorldEventId);
            fields.Add("ncpd-incident", "incident-id", policeReport.IncidentId);
            fields.Add("ncpd-incident", "suspect-handle", policeReport.SuspectHandle);
            fields.Add("ncpd-incident", "reported-at", policeReport.ReportedAt.Elapsed);
            fields.Add("ncpd-incident", "status", policeReport.Status);
            fields.Add("ncpd-incident", "failure", policeReport.Failure.Length == 0 ? "none" : policeReport.Failure);
        }
        if (runtime.EntityUid is { } officer && TryComp<NCNcpdSuspectClaimComponent>(officer, out var suspectClaim))
        {
            fields.Add("ncpd-suspect", "source-world-event", suspectClaim.SourceWorldEventId);
            fields.Add("ncpd-suspect", "incident-id", suspectClaim.IncidentId);
            fields.Add("ncpd-suspect", "party", suspectClaim.SuspectParty);
            fields.Add("ncpd-suspect", "entity", suspectClaim.Suspect);
            fields.Add("ncpd-suspect", "handle", suspectClaim.SuspectHandle);
            fields.Add("ncpd-suspect", "basis", suspectClaim.Basis);
            fields.Add("ncpd-suspect", "status", suspectClaim.Status);
            fields.Add("ncpd-suspect", "force-level", suspectClaim.ForceLevel);
            fields.Add("ncpd-suspect", "updated-at", suspectClaim.UpdatedAt.Elapsed);
            fields.Add("ncpd-suspect", "result", suspectClaim.ResultReason);
        }
        if (runtime.EntityUid is { } evidenceOfficer &&
            TryComp<NCNcpdEvidenceSearchComponent>(evidenceOfficer, out var evidenceSearch))
        {
            AddNcpdEvidenceSearch(fields, evidenceSearch);
        }
        if (runtime.EntityUid is { } submittingOfficer &&
            TryComp<NCNcpdEvidenceSubmissionComponent>(submittingOfficer, out var evidenceSubmission))
        {
            AddNcpdEvidenceSubmission(fields, evidenceSubmission);
        }
        if (runtime.EntityUid is { } interviewingOfficer &&
            TryComp<NCNcpdWitnessInterviewComponent>(interviewingOfficer, out var witnessInterview))
        {
            AddNcpdWitnessInterview(fields, witnessInterview);
        }
        if (runtime.EntityUid is { } wantedOfficer &&
            TryComp<NCNcpdWantedProjectionComponent>(wantedOfficer, out var wanted))
        {
            fields.Add("ncpd-wanted", "wanted-id", wanted.WantedId.IsValid ? wanted.WantedId.ToString() : "pending");
            fields.Add("ncpd-wanted", "incident-id", wanted.IncidentId);
            fields.Add("ncpd-wanted", "subject", wanted.Subject);
            fields.Add("ncpd-wanted", "status", wanted.Status);
            fields.Add("ncpd-wanted", "submitting", wanted.Submitting);
            fields.Add("ncpd-wanted", "updated-at", wanted.UpdatedAt.Elapsed);
            fields.Add("ncpd-wanted", "failure", wanted.Failure.Length == 0 ? "none" : wanted.Failure);
        }
        if (runtime.EntityUid is { } inspector &&
            TryComp<NCNcpdInvestigationComponent>(inspector, out var investigation))
        {
            AddNcpdInvestigation(fields, investigation);
        }
        if (runtime.EntityUid is { } detainee && TryComp<NCNcpdCustodyComponent>(detainee, out var custody))
        {
            fields.Add("ncpd-custody", "booking-id",
                custody.BookingId == Guid.Empty ? "pending" : custody.BookingId.ToString("D"));
            fields.Add("ncpd-custody", "incident-id", custody.IncidentId);
            fields.Add("ncpd-custody", "detainee", custody.Detainee);
            fields.Add("ncpd-custody", "arresting-officer", custody.ArrestingOfficer);
            fields.Add("ncpd-custody", "status", custody.Status);
            fields.Add("ncpd-custody", "updated-at", custody.UpdatedAt.Elapsed);
            fields.Add("ncpd-custody", "failure", custody.Failure.Length == 0 ? "none" : custody.Failure);
        }
        if (_neuralSuggestions.TryGetSuggestionSnapshot(actorId, out var neural))
        {
            fields.Add("neural-intent", "request-id", neural.RequestId);
            fields.Add("neural-intent", "capability", neural.CapabilityId);
            fields.Add("neural-intent", "goal", neural.GoalKey);
            fields.Add("neural-intent", "target-handle", neural.TargetHandle.Length == 0 ? "none" : neural.TargetHandle);
            fields.Add("neural-intent", "confidence", neural.Confidence);
            fields.Add("neural-intent", "accepted-at", neural.AcceptedAt.Elapsed);
            fields.Add("neural-intent", "expires-at", neural.ExpiresAt.Elapsed);
        }

        foreach (var fact in runtime.Facts.Take(8))
        {
            var section = $"fact-{fact.Key}";
            fields.Add(section, "domain", fact.Domain);
            fields.Add(section, "source", fact.Source);
            fields.Add(section, "source-version", fact.SourceVersion);
            fields.Add(section, "subject", fact.Subject.IsValid ? fact.Subject.StableId : "self");
            fields.Add(section, "value", CognitionValue(fact.Value));
        }
        foreach (var belief in runtime.Beliefs.Take(12))
        {
            var section = $"belief-{belief.BeliefId:D}";
            fields.Add(section, "key", belief.Key);
            fields.Add(section, "source", belief.Source);
            fields.Add(section, "source-version", belief.SourceVersion);
            fields.Add(section, "confidence", belief.Confidence.ToString("0.000"));
            fields.Add(section, "freshness", belief.Freshness);
            fields.Add(section, "value", CognitionValue(belief.Value));
        }
        foreach (var desire in runtime.Desires.Take(8))
        {
            var section = $"desire-{desire.DesireId:D}";
            fields.Add(section, "capability", desire.CapabilityId);
            fields.Add(section, "task", desire.TaskKind);
            fields.Add(section, "utility", desire.Utility.ToString("0.000"));
            fields.Add(section, "priority", desire.Priority);
        }
        if (runtime.Plan != null)
        {
            var plan = runtime.Plan;
            fields.Add("plan", "plan-id", plan.PlanId);
            fields.Add("plan", "planner", plan.Planner);
            fields.Add("plan", "status", plan.Status);
            fields.Add("plan", "step", $"{plan.CurrentStep}/{plan.Steps.Length}");
            fields.Add("plan", "expanded-nodes", plan.ExpandedNodes);
            fields.Add("plan", "seed", plan.DeterministicSeed);
            fields.Add("plan", "failure", plan.Failure ?? "none");
            foreach (var dependency in plan.Dependencies.Take(6))
            {
                var section = $"dependency-{dependency.Kind}-{dependency.StableId}";
                fields.Add(section, "version", dependency.Version);
            }
            foreach (var step in plan.Steps.Take(8))
            {
                var section = $"plan-step-{step.StepId}";
                fields.Add(section, "action", step.ActionId);
                fields.Add(section, "depends-on", string.Join(",", step.DependsOn));
                fields.Add(section, "optional", step.Optional);
            }
        }
        foreach (var trace in runtime.Trace.TakeLast(8))
        {
            var section = $"cognition-trace-{trace.Sequence}";
            fields.Add(section, "at", trace.At.Elapsed);
            fields.Add(section, "stage", trace.Stage);
            fields.Add(section, "event", trace.Event);
            fields.Add(section, "detail", trace.Detail);
        }
        return Ok(fields);
    }

    private void AddNcpdEvidenceSearch(FieldBuilder fields, NCNcpdEvidenceSearchComponent search)
    {
        fields.Add("ncpd-evidence-search", "incident-id", search.IncidentId);
        fields.Add("ncpd-evidence-search", "suspect-entity", search.Suspect);
        fields.Add("ncpd-evidence-search", "suspect-party", search.SuspectParty);
        fields.Add("ncpd-evidence-search", "status", search.Status);
        fields.Add("ncpd-evidence-search", "inspected-items", search.InspectedItems);
        fields.Add("ncpd-evidence-search", "dangerous-items", search.DangerousItems.Count);
        fields.Add("ncpd-evidence-search", "seized-items", search.SeizedItems.Count);
        fields.Add("ncpd-evidence-search", "updated-at", search.UpdatedAt.Elapsed);
        fields.Add("ncpd-evidence-search", "failure", search.Failure.Length == 0 ? "none" : search.Failure);
        foreach (var item in search.DangerousItems.Take(8))
        {
            var section = $"ncpd-dangerous-item-{item}";
            fields.Add(section, "entity", item);
            fields.Add(section, "name", Exists(item) ? MetaData(item).EntityName : "missing");
            AddNcpdEvidenceItem(fields, section, item);
        }
        foreach (var item in search.SeizedItems.Take(8))
        {
            var section = $"ncpd-seized-item-{item}";
            fields.Add(section, "entity", item);
            fields.Add(section, "name", Exists(item) ? MetaData(item).EntityName : "missing");
            AddNcpdEvidenceItem(fields, section, item);
        }
    }

    private static void AddNcpdEvidenceSubmission(
        FieldBuilder fields,
        NCNcpdEvidenceSubmissionComponent submission)
    {
        fields.Add("ncpd-evidence-submission", "incident-id", submission.IncidentId);
        fields.Add("ncpd-evidence-submission", "status", submission.Status);
        fields.Add("ncpd-evidence-submission", "destination", submission.Destination);
        fields.Add("ncpd-evidence-submission", "receiver", submission.Receiver);
        fields.Add("ncpd-evidence-submission", "submitted-items", submission.SubmittedItems);
        fields.Add("ncpd-evidence-submission", "updated-at", submission.UpdatedAt.Elapsed);
        fields.Add("ncpd-evidence-submission", "failure",
            submission.Failure.Length == 0 ? "none" : submission.Failure);
    }

    private static void AddNcpdWitnessInterview(
        FieldBuilder fields,
        NCNcpdWitnessInterviewComponent interview)
    {
        fields.Add("ncpd-witness-interview", "incident-id", interview.IncidentId);
        fields.Add("ncpd-witness-interview", "source-world-event", interview.SourceWorldEventId);
        fields.Add("ncpd-witness-interview", "status", interview.Status);
        fields.Add("ncpd-witness-interview", "candidates", interview.Candidates);
        fields.Add("ncpd-witness-interview", "recorded-statements", interview.RecordedStatements);
        fields.Add("ncpd-witness-interview", "current-witness", interview.CurrentWitness);
        fields.Add("ncpd-witness-interview", "updated-at", interview.UpdatedAt.Elapsed);
        fields.Add("ncpd-witness-interview", "failure",
            interview.Failure.Length == 0 ? "none" : interview.Failure);
    }

    private static void AddNcpdInvestigation(
        FieldBuilder fields,
        NCNcpdInvestigationComponent investigation)
    {
        fields.Add("ncpd-investigation", "incident-id", investigation.IncidentId);
        fields.Add("ncpd-investigation", "source-world-event", investigation.SourceWorldEventId);
        fields.Add("ncpd-investigation", "incident-version", investigation.IncidentVersion);
        fields.Add("ncpd-investigation", "status", investigation.Status);
        fields.Add("ncpd-investigation", "evidence", investigation.EvidenceCount);
        fields.Add("ncpd-investigation", "witnesses", investigation.WitnessCount);
        fields.Add("ncpd-investigation", "updated-at", investigation.UpdatedAt.Elapsed);
        fields.Add("ncpd-investigation", "failure",
            investigation.Failure.Length == 0 ? "none" : investigation.Failure);
    }

    private void AddNcpdEvidenceItem(FieldBuilder fields, string section, EntityUid item)
    {
        if (!Exists(item) || !TryComp<NCNcpdEvidenceItemComponent>(item, out var evidence))
            return;
        fields.Add(section, "evidence-id", evidence.EvidenceId);
        fields.Add(section, "incident-id", evidence.IncidentId);
        fields.Add(section, "seized-from", evidence.SeizedFrom);
        fields.Add(section, "seized-by", evidence.SeizedBy);
        fields.Add(section, "status", evidence.Status);
        fields.Add(section, "updated-at", evidence.UpdatedAt.Elapsed);
        fields.Add(section, "failure", evidence.Failure.Length == 0 ? "none" : evidence.Failure);
    }

    private static void AddIntention(
        FieldBuilder fields,
        string section,
        Content.Shared._NC.LivingCity.Cognition.NCCognitionIntentionSnapshot? intention)
    {
        fields.Add(section, "present", intention != null);
        if (intention == null)
            return;
        fields.Add(section, "id", intention.IntentionId);
        fields.Add(section, "capability", intention.CapabilityId);
        fields.Add(section, "task", intention.TaskKind);
        fields.Add(section, "utility", intention.UtilityAtCommitment.ToString("0.000"));
        fields.Add(section, "commitment", intention.Commitment.ToString("0.000"));
        fields.Add(section, "minimum-until", intention.MinimumCommitmentUntil.Elapsed);
        fields.Add(section, "expires", intention.ExpiresAt.Elapsed);
    }

    private static string CognitionValue(
        Content.Shared._NC.LivingCity.Cognition.NCCognitionValue value) => value.Kind switch
    {
        Content.Shared._NC.LivingCity.Cognition.NCCognitionValueKind.Boolean => value.Boolean.ToString(),
        Content.Shared._NC.LivingCity.Cognition.NCCognitionValueKind.Number => value.Number.ToString("0.###"),
        Content.Shared._NC.LivingCity.Cognition.NCCognitionValueKind.StableKey => value.StableKey,
        _ => "unknown",
    };

    private InspectionResult InspectMaterializationRequest(string target)
    {
        if (!TryGuid(target, out var requestId) || !_materialization.TryGetRequest(requestId, out var state))
            return Fail("materialization-request-not-found");
        var fields = new FieldBuilder();
        fields.Add("request", "request-id", state.RequestId);
        fields.Add("request", "actor-id", state.ActorId);
        fields.Add("request", "reason", state.Reason);
        fields.Add("request", "status", state.Status);
        fields.Add("request", "priority", state.Priority);
        fields.Add("request", "attempts", state.Attempts);
        fields.Add("window", "requested-at", state.RequestedAt.Elapsed);
        fields.Add("window", "expires-at", state.ExpiresAt.Elapsed);
        fields.Add("window", "error", state.Error);
        return Ok(fields);
    }

    private InspectionResult InspectDematerializationRequest(string target)
    {
        if (!TryGuid(target, out var requestId) || !_dematerialization.TryGetRequest(requestId, out var state))
            return Fail("dematerialization-request-not-found");
        var fields = new FieldBuilder();
        fields.Add("request", "request-id", state.RequestId);
        fields.Add("request", "actor-id", state.ActorId);
        fields.Add("request", "reason", state.Reason);
        fields.Add("request", "status", state.Status);
        fields.Add("request", "attempts", state.Attempts);
        fields.Add("window", "requested-at", state.RequestedAt.Elapsed);
        fields.Add("window", "expires-at", state.ExpiresAt.Elapsed);
        fields.Add("window", "error", state.Error);
        return Ok(fields);
    }

    private InspectionResult InspectRelevance(string target)
    {
        if (!TryGuid(target, out var raw) || !_relevance.TryGetLod(new NCActorId(raw), out var lod))
            return Fail("actor-absent-from-relevance-catalog");
        var fields = new FieldBuilder();
        fields.Add("actor", "actor-id", raw.ToString("D"));
        fields.Add("actor", "lod", lod);
        fields.Add("catalog", "active", _relevance.IsActive);
        fields.Add("catalog", "recovered", _relevance.CatalogRecovered);
        fields.Add("catalog", "actors", _relevance.CatalogActorCount);
        fields.Add("catalog", "clusters", _relevance.ClusterCount);
        foreach (var cluster in _relevance.Clusters.Take(32))
        {
            var section = $"cluster-{cluster.ClusterId:D}";
            fields.Add(section, "map", cluster.MapId);
            fields.Add(section, "players", cluster.AttachedPlayerCount);
            fields.Add(section, "anchors", cluster.AnchorEntities.Count);
            fields.Add(section, "zones", cluster.ActiveZoneIds.Count);
            fields.Add(section, "materialized", cluster.MaterializedActorCount);
        }
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectGroupAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-group-id");
        var group = await _groups.GetAsync(new NCGroupId(raw), true);
        if (group == null)
            return Fail("group-not-found");
        var snapshot = group.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("identity", "group-id", snapshot.GroupId);
        fields.Add("identity", "stable-local-id", snapshot.StableLocalId);
        fields.Add("identity", "display-name", snapshot.DisplayName);
        fields.Add("identity", "profile", snapshot.ProfileId);
        fields.Add("identity", "faction", snapshot.FactionId);
        fields.Add("identity", "aggregate-version", group.AggregateVersion);
        fields.Add("state", "status", snapshot.Status);
        fields.Add("state", "alert", snapshot.AlertState);
        fields.Add("state", "leader", snapshot.LeaderActorId);
        fields.Add("state", "member-count", snapshot.Members.Length);
        fields.Add("state", "formation", snapshot.FormationPolicy);
        fields.Add("state", "materialize-together", snapshot.MaterializeTogether);
        fields.Add("route", "current-zone", snapshot.Route.CurrentZone);
        fields.Add("route", "destination-zone", snapshot.Route.DestinationZone);
        fields.Add("route", "travel-mode", snapshot.Route.TravelMode);
        fields.Add("route", "expected-arrival", snapshot.Route.ExpectedArrivalAt?.Elapsed);
        fields.Add("directive", "purpose", snapshot.Directive.Purpose);
        fields.Add("directive", "order", snapshot.Directive.CurrentOrder);
        fields.Add("directive", "mission", snapshot.Directive.MissionReference);
        fields.Add("directive", "incident", snapshot.Directive.IncidentId);
        fields.Add("resources", "transport-capacity", snapshot.Resources.TransportCapacity);
        fields.Add("resources", "cargo-units", snapshot.Resources.CargoUnits);
        fields.Add("resources", "supply-units", snapshot.Resources.SupplyUnits);
        fields.Add("resources", "morale", snapshot.Resources.Morale);
        foreach (var member in snapshot.Members.Take(64))
        {
            var section = $"member-{member.ActorId}";
            fields.Add(section, "role", member.Role);
            fields.Add(section, "joined-at", member.JoinedAt.Elapsed);
            fields.Add(section, "leader", member.ActorId == snapshot.LeaderActorId);
        }
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectMembershipAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-actor-id");
        var membership = await _groups.GetMembershipAsync(new NCActorId(raw), true);
        if (membership == null)
            return Fail("group-membership-not-found");
        var fields = new FieldBuilder();
        fields.Add("membership", "actor-id", membership.Snapshot.ActorId);
        fields.Add("membership", "group-id", membership.Snapshot.GroupId);
        fields.Add("membership", "role", membership.Snapshot.Role);
        fields.Add("membership", "joined-at", membership.Snapshot.JoinedAt?.Elapsed);
        fields.Add("membership", "changed-at", membership.Snapshot.ChangedAt.Elapsed);
        fields.Add("membership", "aggregate-version", membership.AggregateVersion);
        return Ok(fields);
    }

    private InspectionResult InspectGroupPhysicalizationRequest(string target)
    {
        if (!TryGuid(target, out var requestId) || !_groupPhysicalization.TryGetRequest(requestId, out var state))
            return Fail("group-physicalization-request-not-found");
        var fields = new FieldBuilder();
        fields.Add("request", "request-id", state.RequestId);
        fields.Add("request", "group-id", state.GroupId);
        fields.Add("request", "direction", state.Direction);
        fields.Add("request", "reason", state.Reason);
        fields.Add("request", "status", state.Status);
        fields.Add("request", "priority", state.Priority);
        fields.Add("request", "attempts", state.Attempts);
        fields.Add("progress", "members", $"{state.CompletedMembers}/{state.TotalMembers}");
        fields.Add("window", "requested-at", state.RequestedAt.Elapsed);
        fields.Add("window", "expires-at", state.ExpiresAt.Elapsed);
        fields.Add("window", "error", state.Error);
        return Ok(fields);
    }

    private InspectionResult InspectGroupFormation(string target)
    {
        if (!TryGuid(target, out var raw) || !_groupFormations.TryGetGroup(new NCGroupId(raw), out var formation))
            return Fail("group-formation-not-found");
        var fields = new FieldBuilder();
        fields.Add("formation", "group-id", formation.GroupId);
        fields.Add("formation", "source-group-version", formation.SourceGroupVersion);
        fields.Add("formation", "policy", formation.Policy);
        fields.Add("formation", "leader-actor", formation.LeaderActorId);
        fields.Add("formation", "member-count", formation.Members.Count);
        if (_groupRoutes.TryGetSnapshot(formation.GroupId, out var trunk))
        {
            fields.Add("trunk-route", "destination-zone", trunk.DestinationZone);
            fields.Add("trunk-route", "travel-mode", trunk.TravelMode);
            fields.Add("trunk-route", "waypoints", trunk.WaypointCount);
            fields.Add("trunk-route", "city-graph-version", trunk.PlanVersion.CityGraphVersion);
            fields.Add("trunk-route", "room-graph-version", trunk.PlanVersion.RoomGraphVersion);
            fields.Add("trunk-route", "generation", trunk.Generation);
        }
        foreach (var member in formation.Members.Take(64))
        {
            var section = $"slot-{member.SlotIndex:D3}";
            fields.Add(section, "actor-id", member.ActorId);
            fields.Add(section, "role", member.Role);
            fields.Add(section, "leader", member.IsLeader);
            fields.Add(section, "offset", member.DesiredOffset);
        }
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectZoneStateAsync(string target)
    {
        if (!NCZoneId.TryParse(target.Trim(), out var zoneId))
            return Fail("invalid-zone-id");
        var state = await _zoneStateRegistry.GetAsync(zoneId, true);
        if (state == null)
            return Fail("zone-state-not-found");
        var snapshot = state.Snapshot;
        var population = snapshot.Population;
        var fields = new FieldBuilder();
        fields.Add("zone", "zone-id", snapshot.ScopeZone);
        fields.Add("zone", "scope-slot", snapshot.ScopeSlot);
        fields.Add("zone", "aggregate-version", state.AggregateVersion);
        fields.Add("zone", "captured-at", snapshot.CapturedAt.Elapsed);
        fields.Add("population", "total", population.TotalActors);
        fields.Add("population", "living", population.LivingActors);
        fields.Add("population", "dead", population.DeadActors);
        fields.Add("population", "frozen", population.FrozenActors);
        fields.Add("population", "traveling", population.TravelingActors);
        fields.Add("population", "weighted-activity", population.WeightedActivityUnits);
        foreach (var value in population.Statuses)
            fields.Add("status-buckets", value.Status.ToString(), value.Count);
        foreach (var value in population.CognitionTiers)
            fields.Add("cognition-buckets", value.Tier.ToString(), value.Count);
        foreach (var value in population.Availability)
            fields.Add("availability-buckets", value.Availability.ToString(), value.Count);
        foreach (var value in population.Archetypes.Take(64))
            fields.Add("archetype-buckets", value.ArchetypeId, value.Count);
        foreach (var signal in snapshot.Signals)
        {
            var section = $"signal-{signal.Kind}";
            fields.Add(section, "availability", signal.Availability);
            fields.Add(section, "value", signal.Value);
            fields.Add(section, "source", signal.Source);
            fields.Add(section, "source-version", signal.SourceVersion);
        }
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectPartyAsync(string target)
    {
        if (!NCPartyRef.TryParse(target.Trim(), out var party))
            return Fail("invalid-party-reference");
        var resolution = await _partyResolver.ResolveAsync(party, true);
        if (!resolution.IsResolved)
            return Fail(resolution.Error ?? "party-not-found");
        var fields = new FieldBuilder();
        fields.Add("party", "reference", resolution.Party);
        fields.Add("party", "kind", resolution.Party.Kind);
        fields.Add("party", "display-name", resolution.DisplayName);
        fields.Add("party", "lifecycle", resolution.Lifecycle);
        fields.Add("party", "source-version", resolution.SourceVersion);
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectAccountAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-account-id");
        var account = await _economy.GetAccountAsync(new NCAccountId(raw), true);
        if (account == null)
            return Fail("account-not-found");
        var snapshot = account.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("account", "account-id", snapshot.AccountId);
        fields.Add("account", "owner", snapshot.Owner);
        fields.Add("account", "kind", snapshot.Kind);
        fields.Add("account", "currency", snapshot.CurrencyId);
        fields.Add("account", "balance", snapshot.Balance);
        fields.Add("account", "allows-negative", snapshot.AllowsNegativeBalance);
        fields.Add("account", "status", snapshot.Status);
        fields.Add("account", "aggregate-version", account.AggregateVersion);
        fields.Add("account", "created-at", snapshot.CreatedAt.Elapsed);
        fields.Add("account", "updated-at", snapshot.UpdatedAt.Elapsed);
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectTransactionAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-transaction-id");
        var transaction = await _economy.GetTransactionAsync(new NCTransactionId(raw), true);
        if (transaction == null)
            return Fail("transaction-not-found");
        var snapshot = transaction.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("transaction", "transaction-id", snapshot.TransactionId);
        fields.Add("transaction", "kind", snapshot.Kind);
        fields.Add("transaction", "currency", snapshot.CurrencyId);
        fields.Add("transaction", "reference", snapshot.Reference);
        fields.Add("transaction", "initiator", snapshot.Initiator);
        fields.Add("transaction", "posted-at", snapshot.PostedAt.Elapsed);
        fields.Add("transaction", "aggregate-version", transaction.AggregateVersion);
        for (var i = 0; i < snapshot.Legs.Length; i++)
        {
            fields.Add($"leg-{i:D3}", "account-id", snapshot.Legs[i].AccountId);
            fields.Add($"leg-{i:D3}", "amount", snapshot.Legs[i].Amount);
            fields.Add($"leg-{i:D3}", "balance-after", snapshot.Legs[i].BalanceAfter);
        }
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectEmploymentAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-employment-id");
        var employment = await _employment.GetAsync(new NCEmploymentId(raw), true);
        if (employment == null)
            return Fail("employment-not-found");
        var snapshot = employment.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("employment", "employment-id", snapshot.EmploymentId);
        fields.Add("employment", "employee", snapshot.Employee);
        fields.Add("employment", "employer", snapshot.Employer);
        fields.Add("employment", "position", snapshot.PositionId);
        fields.Add("employment", "status", snapshot.Status);
        fields.Add("employment", "salary", snapshot.SalaryAmount);
        fields.Add("employment", "payroll-interval-ticks", snapshot.PayrollIntervalTicks);
        fields.Add("employment", "next-payroll", snapshot.NextPayrollAt?.Elapsed);
        fields.Add("employment", "employer-account", snapshot.EmployerAccountId);
        fields.Add("employment", "payee-account", snapshot.PayeeAccountId);
        fields.Add("employment", "started-at", snapshot.StartedAt.Elapsed);
        fields.Add("employment", "changed-at", snapshot.ChangedAt.Elapsed);
        fields.Add("employment", "aggregate-version", employment.AggregateVersion);
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectRelationshipAsync(string target, string secondary)
    {
        if (!NCPartyRef.TryParse(target.Trim(), out var observer) ||
            !NCPartyRef.TryParse(secondary.Trim(), out var subject))
        {
            return Fail("invalid-observer-or-subject-party");
        }
        var relationship = await _relationships.GetAsync(observer, subject, true);
        if (relationship == null)
            return Fail("relationship-not-found");
        var snapshot = relationship.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("relationship", "relationship-id", snapshot.RelationshipId);
        fields.Add("relationship", "observer", snapshot.Observer);
        fields.Add("relationship", "subject", snapshot.Subject);
        fields.Add("relationship", "trust", snapshot.Trust);
        fields.Add("relationship", "fear", snapshot.Fear);
        fields.Add("relationship", "respect", snapshot.Respect);
        fields.Add("relationship", "affection", snapshot.Affection);
        fields.Add("relationship", "hostility", snapshot.Hostility);
        fields.Add("relationship", "debt", snapshot.Debt);
        fields.Add("relationship", "obligation", snapshot.Obligation);
        fields.Add("relationship", "interactions", snapshot.InteractionCount);
        fields.Add("relationship", "created-at", snapshot.CreatedAt.Elapsed);
        fields.Add("relationship", "updated-at", snapshot.UpdatedAt.Elapsed);
        fields.Add("relationship", "last-effect", snapshot.LastEffectId);
        fields.Add("relationship", "aggregate-version", relationship.AggregateVersion);
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectReputationAsync(string target)
    {
        if (!NCPartyRef.TryParse(target.Trim(), out var subject))
            return Fail("invalid-subject-party");
        var reputation = await _reputations.GetAsync(subject, true);
        if (reputation == null)
            return Fail("reputation-not-found");
        var snapshot = reputation.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("reputation", "reputation-id", snapshot.ReputationId);
        fields.Add("reputation", "subject", snapshot.Subject);
        fields.Add("reputation", "entries", snapshot.Entries.Count);
        fields.Add("reputation", "aggregate-version", reputation.AggregateVersion);
        foreach (var entry in snapshot.Entries.Take(128))
        {
            var section = $"entry-{entry.EntryId}";
            fields.Add(section, "channel", entry.Audience.Channel);
            fields.Add(section, "party", entry.Audience.Party);
            fields.Add(section, "domain", entry.Audience.DomainKey);
            fields.Add(section, "score", entry.Score);
            fields.Add(section, "visibility", entry.Visibility);
            fields.Add(section, "evidence", entry.Evidence);
            fields.Add(section, "events", entry.EventCount);
            fields.Add(section, "updated-at", entry.UpdatedAt.Elapsed);
            fields.Add(section, "last-effect", entry.LastEffectId);
        }
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectMemoryAsync(string target)
    {
        if (!NCPartyRef.TryParse(target.Trim(), out var owner))
            return Fail("invalid-memory-owner-party");
        var memory = await _memories.GetAsync(owner, true);
        if (memory == null)
            return Fail("memory-book-not-found");
        var snapshot = memory.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("memory-book", "book-id", snapshot.BookId);
        fields.Add("memory-book", "owner", snapshot.Owner);
        fields.Add("memory-book", "episodes", snapshot.Episodes.Count);
        fields.Add("memory-book", "summaries", snapshot.Summaries.Count);
        fields.Add("memory-book", "total-remembered", snapshot.TotalRemembered);
        fields.Add("memory-book", "total-forgotten", snapshot.TotalForgotten);
        fields.Add("memory-book", "total-consolidated", snapshot.TotalConsolidated);
        fields.Add("memory-book", "aggregate-version", memory.AggregateVersion);
        foreach (var episode in snapshot.Episodes.Take(24))
        {
            var section = $"episode-{episode.MemoryId}";
            fields.Add(section, "event-type", episode.EventTypeKey);
            fields.Add(section, "source-kind", episode.Source.Kind);
            fields.Add(section, "source-party", episode.Source.SourceParty);
            fields.Add(section, "participants", string.Join(",", episode.Participants));
            fields.Add(section, "emotions", string.Join(",", episode.EmotionalTagKeys));
            fields.Add(section, "salience", episode.Salience);
            fields.Add(section, "confidence", episode.Confidence);
            fields.Add(section, "privacy", episode.Privacy);
            fields.Add(section, "retention", episode.RetentionClass);
            fields.Add(section, "critical", episode.Critical);
            fields.Add(section, "created-at", episode.CreatedAt.Elapsed);
            fields.Add(section, "expires-at", episode.ExpiresAt?.Elapsed);
            fields.Add(section, "recalls", episode.RecallCount);
        }
        foreach (var summary in snapshot.Summaries.Take(24))
        {
            var section = $"summary-{summary.SummaryId}";
            fields.Add(section, "consolidation-key", summary.ConsolidationKey);
            fields.Add(section, "occurrences", summary.OccurrenceCount);
            fields.Add(section, "event-types", string.Join(",", summary.EventTypeKeys));
            fields.Add(section, "participants", string.Join(",", summary.Participants));
            fields.Add(section, "emotions", string.Join(",", summary.EmotionalTagKeys));
            fields.Add(section, "maximum-salience", summary.MaximumSalience);
            fields.Add(section, "minimum-confidence", summary.MinimumConfidence);
            fields.Add(section, "privacy", summary.Privacy);
            fields.Add(section, "expires-at", summary.ExpiresAt.Elapsed);
            fields.Add(section, "recalls", summary.RecallCount);
        }
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectSocialProfileAsync(string target)
    {
        if (!Guid.TryParse(target.Trim(), out var value) || value == Guid.Empty)
            return Fail("invalid-actor-id");
        var profile = await _socialProfiles.GetAsync(new NCActorId(value), true);
        if (profile == null)
            return Fail("social-profile-not-found");
        var snapshot = profile.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("social-profile", "actor-id", snapshot.ActorId);
        fields.Add("social-profile", "personality-profile", snapshot.PersonalityProfileId);
        fields.Add("social-profile", "values-profile", snapshot.ValuesProfileId);
        fields.Add("social-profile", "preference-profile", snapshot.PreferenceProfileId);
        fields.Add("social-profile", "speech-profile", snapshot.SpeechProfileId);
        fields.Add("personality", "temperament", snapshot.Personality.Temperament);
        fields.Add("personality", "risk-tolerance", snapshot.Personality.RiskTolerance);
        fields.Add("personality", "aggression", snapshot.Personality.Aggression);
        fields.Add("personality", "empathy", snapshot.Personality.Empathy);
        fields.Add("personality", "secrecy", snapshot.Personality.Secrecy);
        fields.Add("personality", "loyalty", snapshot.Personality.Loyalty);
        fields.Add("speech", "language", snapshot.Speech.PreferredLanguage);
        fields.Add("speech", "locale", snapshot.Speech.PreferredLocale);
        fields.Add("speech", "languages", string.Join(",", snapshot.Speech.Languages));
        fields.Add("speech", "forbidden-topics", string.Join(",", snapshot.Speech.ForbiddenTopics));
        foreach (var modifier in snapshot.CapabilityUtilityModifiers.Take(64))
            fields.Add("utility", modifier.Key, modifier.Value);
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectRumorBookAsync(string target)
    {
        if (!NCPartyRef.TryParse(target.Trim(), out var owner))
            return Fail("invalid-rumor-owner-party");
        var book = await _rumors.GetAsync(owner, true);
        if (book == null)
            return Fail("rumor-book-not-found");
        var fields = new FieldBuilder();
        fields.Add("rumor-book", "owner", book.Snapshot.Owner);
        fields.Add("rumor-book", "knowledge", book.Snapshot.Knowledge.Count);
        fields.Add("rumor-book", "total-learned", book.Snapshot.TotalLearned);
        fields.Add("rumor-book", "total-propagated", book.Snapshot.TotalPropagated);
        fields.Add("rumor-book", "total-forgotten", book.Snapshot.TotalForgotten);
        fields.Add("rumor-book", "aggregate-version", book.AggregateVersion);
        foreach (var rumor in book.Snapshot.Knowledge.Take(24))
        {
            var section = $"rumor-{rumor.KnowledgeId}";
            fields.Add(section, "information-id", rumor.InformationId);
            fields.Add(section, "topic", rumor.TopicKey);
            fields.Add(section, "subject", $"{rumor.Subject.Kind}:{rumor.Subject.StableId}");
            fields.Add(section, "originator", rumor.Originator);
            fields.Add(section, "immediate-source", rumor.ImmediateSource);
            fields.Add(section, "source-events", string.Join(",", rumor.SourceWorldEventIds));
            fields.Add(section, "confidence", rumor.Confidence);
            fields.Add(section, "distortion", rumor.Distortion);
            fields.Add(section, "hops", rumor.HopCount);
            fields.Add(section, "privacy", rumor.Privacy);
            fields.Add(section, "expires-at", rumor.ExpiresAt?.Elapsed);
        }
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectSocialObligationAsync(string target)
    {
        if (!Guid.TryParse(target.Trim(), out var value) || value == Guid.Empty)
            return Fail("invalid-social-obligation-id");
        var obligation = await _socialObligations.GetAsync(new NCSocialObligationId(value), true);
        if (obligation == null)
            return Fail("social-obligation-not-found");
        var snapshot = obligation.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("obligation", "obligation-id", snapshot.ObligationId);
        fields.Add("obligation", "kind", snapshot.Kind);
        fields.Add("obligation", "status", snapshot.Status);
        fields.Add("obligation", "debtor", snapshot.Debtor);
        fields.Add("obligation", "creditor", snapshot.Creditor);
        fields.Add("obligation", "purpose", snapshot.Terms.PurposeKey);
        fields.Add("obligation", "quantity", snapshot.Terms.Quantity);
        fields.Add("obligation", "unit", snapshot.Terms.UnitKey);
        fields.Add("obligation", "privacy", snapshot.Privacy);
        fields.Add("obligation", "created-at", snapshot.CreatedAt.Elapsed);
        fields.Add("obligation", "due-at", snapshot.DueAt?.Elapsed);
        fields.Add("obligation", "resolved-at", snapshot.ResolvedAt?.Elapsed);
        fields.Add("obligation", "aggregate-version", obligation.AggregateVersion);
        return Ok(fields);
    }

    private InspectionResult InspectConversation(string target)
    {
        if (!Guid.TryParse(target.Trim(), out var value) || value == Guid.Empty ||
            !_conversations.TryGet(new NCConversationSessionId(value), out var session))
            return Fail("conversation-not-found");
        var fields = new FieldBuilder();
        fields.Add("conversation", "session-id", session.SessionId);
        fields.Add("conversation", "participants", string.Join(",", session.Participants));
        fields.Add("conversation", "channel", session.Channel);
        fields.Add("conversation", "location", $"{session.LocationContext.Kind}:{session.LocationContext.StableId}");
        fields.Add("conversation", "status", session.Status);
        fields.Add("conversation", "version", session.ConversationVersion);
        fields.Add("conversation", "language", session.Language);
        fields.Add("conversation", "locale", session.Locale);
        fields.Add("conversation", "maximum-disclosure", session.MaximumDisclosure);
        fields.Add("conversation", "topics", string.Join(",", session.CurrentTopicKeys));
        fields.Add("conversation", "turns", session.Turns.Count);
        fields.Add("conversation", "end-reason", session.EndReason);
        foreach (var turn in session.Turns.Take(32))
        {
            var section = $"turn-{turn.Sequence}";
            fields.Add(section, "speaker", turn.Speaker);
            fields.Add(section, "act", turn.Act);
            fields.Add(section, "semantic-key", turn.SemanticKey);
            fields.Add(section, "external-reference", turn.ExternalReference);
            fields.Add(section, "at", turn.At.Elapsed);
        }
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectPropertyAsync(string target)
    {
        if (!NCApartmentId.TryParse(target.Trim(), out var propertyId))
            return Fail("invalid-property-id");
        var property = await _properties.GetAsync(propertyId, true);
        if (property == null)
            return Fail("property-not-found");
        var snapshot = property.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("property", "property-id", snapshot.PropertyId);
        fields.Add("property", "building-id", snapshot.BuildingId);
        fields.Add("property", "listing-id", snapshot.ListingId);
        fields.Add("property", "owner", snapshot.Owner);
        fields.Add("property", "status", snapshot.Status);
        fields.Add("property", "purchase-price", snapshot.PurchasePrice);
        fields.Add("property", "acquired-at", snapshot.AcquiredAt?.Elapsed);
        fields.Add("property", "access-revision", snapshot.AccessRevision);
        fields.Add("property", "rent-policy", snapshot.RentPolicyId);
        fields.Add("property", "access-policy", snapshot.AccessPolicyId);
        fields.Add("property", "template", snapshot.CurrentTemplateId);
        fields.Add("property", "template-revision", snapshot.CurrentTemplateRevision);
        fields.Add("property", "active-deployment", snapshot.ActiveDeploymentId);
        fields.Add("property", "interior-state", snapshot.InteriorState);
        fields.Add("property", "next-rent-due", snapshot.NextRentDueAt?.Elapsed);
        fields.Add("property", "last-settlement", snapshot.LastSettlementTransactionId);
        fields.Add("property", "rooms", string.Join(",", snapshot.RoomIds));
        fields.Add("property", "occupants", snapshot.Occupants.Length);
        fields.Add("property", "access-grants", snapshot.AccessGrants.Length);
        fields.Add("property", "aggregate-version", property.AggregateVersion);
        foreach (var occupant in snapshot.Occupants.Take(32))
        {
            var section = $"occupant-{occupant.Occupant}";
            fields.Add(section, "party", occupant.Occupant);
            fields.Add(section, "kind", occupant.Kind);
            fields.Add(section, "started-at", occupant.StartedAt.Elapsed);
            fields.Add(section, "valid-until", occupant.ValidUntil?.Elapsed);
        }
        foreach (var grant in snapshot.AccessGrants.Take(32))
        {
            var section = $"grant-{grant.GrantId}";
            fields.Add(section, "subject", grant.Subject);
            fields.Add(section, "permissions", grant.Permissions);
            fields.Add(section, "access-revision", grant.AccessRevision);
            fields.Add(section, "issued-at", grant.IssuedAt.Elapsed);
            fields.Add(section, "valid-until", grant.ValidUntil?.Elapsed);
        }
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectPropertyContentsAsync(string target)
    {
        if (!NCApartmentId.TryParse(target.Trim(), out var propertyId))
            return Fail("invalid-property-id");
        var contents = await _propertyContents.GetAsync(propertyId, true);
        if (contents == null)
            return Fail("property-contents-not-found");
        var snapshot = contents.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("property-contents", "property-id", snapshot.PropertyId);
        fields.Add("property-contents", "present", snapshot.Present);
        fields.Add("property-contents", "template", snapshot.TemplateId);
        fields.Add("property-contents", "template-revision", snapshot.TemplateRevision);
        fields.Add("property-contents", "format-version", snapshot.FormatVersion);
        fields.Add("property-contents", "payload-characters", snapshot.Payload.Length);
        fields.Add("property-contents", "entities", snapshot.EntityCount);
        fields.Add("property-contents", "resident-entities", snapshot.ResidentEntityCount);
        fields.Add("property-contents", "captured-at", snapshot.CapturedAt.Elapsed);
        fields.Add("property-contents", "aggregate-version", contents.AggregateVersion);
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectPoliceFineAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-police-fine-id");
        var fine = await _fines.GetAsync(new NCPoliceFineId(raw), true);
        if (fine == null)
            return Fail("police-fine-not-found");
        var snapshot = fine.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("police-fine", "fine-id", snapshot.FineId);
        fields.Add("police-fine", "target", snapshot.Target);
        fields.Add("police-fine", "target-name", snapshot.TargetName);
        fields.Add("police-fine", "article", snapshot.Article);
        fields.Add("police-fine", "reason", snapshot.Reason);
        fields.Add("police-fine", "amount", snapshot.Amount);
        fields.Add("police-fine", "status", snapshot.Status);
        fields.Add("police-fine", "issued-by", snapshot.IssuedBy);
        fields.Add("police-fine", "issued-by-name", snapshot.IssuedByName);
        fields.Add("police-fine", "issued-at", snapshot.IssuedAt.Elapsed);
        fields.Add("police-fine", "due-at", snapshot.DueAt.Elapsed);
        fields.Add("police-fine", "updated-at", snapshot.UpdatedAt.Elapsed);
        fields.Add("police-fine", "paid-at", snapshot.PaidAt?.Elapsed);
        fields.Add("police-fine", "payment-transaction", snapshot.PaymentTransactionId);
        fields.Add("police-fine", "aggregate-version", fine.AggregateVersion);
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectOutboxMessageAsync(string target)
    {
        if (!TryGuid(target, out var messageId))
            return Fail("invalid-outbox-message-id");
        var message = await _database.GetNCOutboxMessageAsync(messageId);
        if (message == null)
            return Fail("outbox-message-not-found");
        var fields = new FieldBuilder();
        fields.Add("outbox-message", "message-id", message.MessageId);
        fields.Add("outbox-message", "event-id", message.EventId);
        fields.Add("outbox-message", "message-type", message.MessageType);
        fields.Add("outbox-message", "payload-characters", message.Payload.Length);
        fields.Add("outbox-message", "attempts", message.Attempts);
        fields.Add("outbox-message", "created-at", message.CreatedAt.ToString("O"));
        fields.Add("outbox-message", "available-at", message.AvailableAt.ToString("O"));
        fields.Add("outbox-message", "processed-at", message.ProcessedAt?.ToString("O"));
        fields.Add("outbox-message", "last-error", message.LastError);
        return Ok(fields);
    }

    private InspectionResult InspectCityZone(string target)
    {
        if (!NCZoneId.TryParse(target.Trim(), out var zoneId))
            return Fail("invalid-city-zone-id");
        if (!_zones.TryGetZone(zoneId, out var zone))
            return Fail("city-zone-not-found");

        var fields = new FieldBuilder();
        fields.Add("city-zone", "zone-id", zone.Id);
        fields.Add("city-zone", "name", zone.Name);
        fields.Add("city-zone", "kind", zone.Kind);
        fields.Add("city-zone", "context-slot", zone.ContextSlot);
        fields.Add("city-zone", "parent", zone.Parent);
        fields.Add("city-zone", "priority", zone.Priority);
        fields.Add("city-zone", "hierarchy-rank", zone.HierarchyRank);
        fields.Add("city-zone", "activity-mode", zone.ActivityMode);
        fields.Add("city-zone", "network",
            _zones.TryGetZoneNetwork(zoneId, out var networkId) ? networkId : "unknown");

        var geometry = new List<NCZoneGeometryInfo>();
        _zones.GetGeometry(zoneId, geometry);
        fields.Add("geometry", "count", geometry.Count);
        for (var i = 0; i < geometry.Count && i < 48; i++)
        {
            var item = geometry[i];
            fields.Add("geometry", $"part-{i}",
                $"{item.Kind}; global={item.Global}; z={item.Z}; range={item.MinZ}..{item.MaxZ}; " +
                $"vertices={item.Vertices.Count}; chunks={item.Chunks.Count}");
        }
        if (geometry.Count > 48)
            fields.Add("geometry", "truncated", geometry.Count - 48);
        return Ok(fields);
    }

    private InspectionResult InspectCityConnectivityZone(string target)
    {
        if (!NCZoneId.TryParse(target.Trim(), out var zoneId))
            return Fail("invalid-city-zone-id");
        if (!_zones.TryGetZone(zoneId, out var zone))
            return Fail("city-zone-not-found");

        var fields = new FieldBuilder();
        fields.Add("connectivity-zone", "zone-id", zone.Id);
        fields.Add("connectivity-zone", "name", zone.Name);
        fields.Add("connectivity-zone", "context-slot", zone.ContextSlot);
        fields.Add("versions", "city-graph", _cityConnectivity.GraphVersion);
        fields.Add("versions", "room-graph", _roomConnectivity.GraphVersion);
        fields.Add("versions", "spawn-index", _spawnCandidates.IndexVersion);

        var rooms = new List<NCRoomNodeInfo>();
        _roomConnectivity.GetRooms(zoneId, rooms);
        fields.Add("rooms", "count", rooms.Count);
        foreach (var room in rooms.Take(48))
        {
            fields.Add("rooms", $"room-{room.Id.Value}",
                $"anchor={room.Anchor}; navigation-nodes={room.NavigationNodeCount}");
        }
        if (rooms.Count > 48)
            fields.Add("rooms", "truncated", rooms.Count - 48);

        var portals = new List<NCCityPortalInfo>();
        _cityConnectivity.GetOutgoingPortals(zoneId, portals);
        fields.Add("city-portals", "count", portals.Count);
        foreach (var portal in portals.Take(48))
        {
            fields.Add("city-portals", $"portal-{portal.Id.Value}",
                $"{portal.Kind} -> {portal.ToZone}; z={portal.EntryTarget.Z}->{portal.ExitTarget.Z}; " +
                $"cost={portal.BaseTravelSeconds + portal.ThreatCost:0.###}; enabled={portal.Enabled}; " +
                $"stable={portal.StableId.IsValid}");
        }
        if (portals.Count > 48)
            fields.Add("city-portals", "truncated", portals.Count - 48);

        var candidates = new List<NCCitySpawnCandidateInfo>();
        foreach (var profile in _prototypes.EnumeratePrototypes<NCCitySpawnCandidateProfilePrototype>()
                     .OrderBy(static profile => profile.ID, StringComparer.Ordinal))
        {
            _spawnCandidates.GetCandidates(profile.ID, zoneId, candidates);
            fields.Add("spawn-candidates", profile.ID, candidates.Count);
        }

        var issues = new List<NCCityConnectivityValidationIssue>();
        var validation = _connectivityValidation.Validate(issues);
        var matchingIssues = issues.Where(issue => issue.ZoneId == zoneId).ToArray();
        fields.Add("validation", "version", validation.ValidationVersion);
        fields.Add("validation", "zone-issues", matchingIssues.Length);
        foreach (var issue in matchingIssues.Take(64))
            fields.Add("validation", $"{issue.Severity}-{issue.Code}", issue.Message);
        if (matchingIssues.Length > 64)
            fields.Add("validation", "truncated", matchingIssues.Length - 64);
        return Ok(fields);
    }

    private InspectionResult InspectCityConnectivityValidation()
    {
        var fields = new FieldBuilder();
        var zoneErrors = new List<NCZoneValidationError>();
        var zonesValid = _zones.ValidateAll(zoneErrors);
        fields.Add("zone-validation", "valid", zonesValid);
        fields.Add("zone-validation", "errors", zoneErrors.Count);
        foreach (var error in zoneErrors.Take(96))
            fields.Add("zone-validation", $"{error.ZoneSet}-{error.Zone}", error.Message);
        if (zoneErrors.Count > 96)
            fields.Add("zone-validation", "truncated", zoneErrors.Count - 96);

        var issues = new List<NCCityConnectivityValidationIssue>();
        var summary = _connectivityValidation.Validate(issues);
        fields.Add("connectivity-validation", "validation-version", summary.ValidationVersion);
        fields.Add("connectivity-validation", "city-graph-version", summary.CityGraphVersion);
        fields.Add("connectivity-validation", "room-graph-version", summary.RoomGraphVersion);
        fields.Add("connectivity-validation", "spawn-index-version", summary.SpawnIndexVersion);
        fields.Add("connectivity-validation", "errors", summary.Errors);
        fields.Add("connectivity-validation", "warnings", summary.Warnings);
        foreach (var issue in issues.Take(128))
            fields.Add("connectivity-validation", $"{issue.Severity}-{issue.Code}-{issue.ZoneId}", issue.Message);
        if (issues.Count > 128)
            fields.Add("connectivity-validation", "truncated", issues.Count - 128);
        return Ok(fields);
    }

    private static bool TryGuid(string value, out Guid result) =>
        Guid.TryParse(value.Trim(), out result) && result != Guid.Empty;

    private static InspectionResult Ok(FieldBuilder fields) => new(true, fields.Build(), null);

    private static InspectionResult Fail(string error) => new(false, [], error);
}
