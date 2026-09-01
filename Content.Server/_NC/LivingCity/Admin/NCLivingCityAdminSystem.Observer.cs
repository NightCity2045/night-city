// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Population;
using Content.Server._NC.LivingCity.Simulation;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Admin;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Admin;

public sealed partial class NCLivingCityAdminSystem
{
    private const int MaximumObserverZones = 64;

    /// <summary>
    /// Produces one bounded read-only view from existing A-Life projections. Exact actor observation may perform one
    /// lazy aggregate read; the district view never scans persistent actors or ECS entities.
    /// </summary>
    private async void OnObserverRequest(NCLivingCityAdminObserverRequest request, EntitySessionEventArgs args)
    {
        if (request.RequestId == Guid.Empty)
            return;
        if (!CanRead(args))
        {
            SendObserver(args, request.RequestId, false, [], [], [], null, "not-authorized");
            return;
        }
        if (request.ZoneId == null || request.ActorId == null ||
            request.ZoneId.Length > MaximumTargetLength || request.ActorId.Length > MaximumTargetLength)
        {
            SendObserver(args, request.RequestId, true, [], [], [], null, "invalid-request");
            return;
        }
        if (!AcceptObserverRate(args.SenderSession.UserId))
        {
            SendObserver(args, request.RequestId, true, [], [], [], null, "rate-limited");
            return;
        }

        try
        {
            var projections = new List<NCCityZoneObserverProjection>();
            _zoneStates.GetObserverProjections(projections);
            var summary = BuildObserverSummary(projections);
            var zones = BuildObserverZones(projections, request.ZoneId.Trim(), out var zoneError);

            NCLivingCityAdminField[] actorFields = [];
            string? actorError = null;
            if (!string.IsNullOrWhiteSpace(request.ActorId))
                (actorFields, actorError) = await BuildObserverActorAsync(request.ActorId.Trim());

            SendObserver(
                args,
                request.RequestId,
                true,
                summary,
                zones,
                actorFields,
                actorError,
                zoneError);
        }
        catch (Exception exception)
        {
            Log.Error($"Living City F7 observer request failed: {exception}");
            SendObserver(args, request.RequestId, true, [], [], [], null, "observer-failure");
        }
    }

    private NCLivingCityAdminField[] BuildObserverSummary(
        IReadOnlyList<NCCityZoneObserverProjection> projections)
    {
        using var adminWork = _loadGovernor.MeasureWork(NCAILoadWorkKind.AdminSnapshots);
        var fields = new FieldBuilder();
        fields.Add("observer-round", "game-preset", _gameTicker.CurrentPreset?.ID ?? "none");
        fields.Add("observer-round", "night-city-rule", _nightCityRule.IsActive ? "active" : "inactive");
        if (_clock.TryGetCurrentTime(out _, out var now))
        {
            fields.Add("observer-round", "state", "active");
            fields.Add("observer-round", "active-world-time", now.Elapsed);
        }
        else
        {
            fields.Add("observer-round", "state", "inactive");
        }

        var districts = projections.Where(static value => value.ScopeSlot == NCZoneContextSlot.District).ToArray();
        var districtActors = districts.Sum(static value => value.TotalActors);
        var publicActors = _zoneStates.PublicActorCount;
        var abstractHomeActors = _zoneStates.AbstractHomeActorCount;
        var residenceAssignedActors = _zoneStates.ResidenceAssignedActorCount;
        fields.Add("observer-presence", "indexed-actors", _zoneStates.IndexedActorCount);
        fields.Add("observer-presence", "district-actors", districtActors);
        fields.Add("observer-presence", "residents", _zoneStates.ResidentCount);
        fields.Add("observer-presence", "narrative-actors", _zoneStates.NarrativeActorCount);
        fields.Add("observer-presence", "materialized", _actorRuntime.PhysicalActorCount);
        fields.Add("observer-presence", "public", publicActors);
        fields.Add("observer-presence", "abstract-home", abstractHomeActors);
        fields.Add("observer-presence", "working", _zoneStates.WorkingActorCount);
        fields.Add("observer-presence", "traveling", _zoneStates.TravelingActorCount);
        fields.Add("observer-presence", "residence-assigned", residenceAssignedActors);
        fields.Add("observer-presence", "residence-unassigned",
            Math.Max(0, _zoneStates.ActiveActorCount - residenceAssignedActors));
        fields.Add("observer-presence", "unknown-presence",
            Math.Max(0, _zoneStates.ActiveActorCount - publicActors - abstractHomeActors));

        fields.Add("observer-society", "formal-workers", _workforce.IndexedAssignmentCount);
        fields.Add("observer-society", "profession-profiles", _professions.ProfileCount);
        fields.Add("observer-society", "profession-jobs", _professions.JobCount);
        fields.Add("observer-society", "civilian-livelihoods-cached", _livelihoods.CachedActorCount);
        fields.Add("observer-society", "faction-profiles", _factionBootstrap.ProfileCount);
        fields.Add("observer-society", "faction-load-complete", _factionBootstrap.InitialLoadComplete);
        fields.Add("observer-society", "affiliated-actors-cached", _factionMemberships.CachedActorCount);
        fields.Add("observer-society", "physical-affiliations", _factionActors.ProjectedActors);
        fields.Add("observer-society", "configuration-error",
            _workforce.ConfigurationError ?? _factionBootstrap.ConfigurationError ?? "none");
        fields.Add("observer-human-mind", "active-conversations", _conversations.ActiveSessionCount);
        fields.Add("observer-human-mind", "recent-conversations", _conversations.RecentSessionCount);
        fields.Add("observer-human-mind", "physical-conversations", _neuralConversations.ActivePhysicalConversations);
        fields.Add("observer-human-mind", "ambient-conversations-scheduled",
            _neuralConversations.ScheduledAmbientInitiatives);
        var memory = _humanMemory.GetCounters();
        fields.Add("observer-human-mind", "memory-writes-in-flight", memory.WritesInFlight);
        fields.Add("observer-human-mind", "memories-remembered", memory.Remembered);
        fields.Add("observer-human-mind", "memory-rejections", memory.Rejected);
        fields.Add("observer-human-mind", "directives-in-flight", _humanDirectives.InFlight);
        fields.Add("observer-human-mind", "directives-accepted", _humanDirectives.Accepted);
        fields.Add("observer-human-mind", "directives-rejected", _humanDirectives.Rejected);
        fields.Add("observer-human-mind", "last-directive-result", _humanDirectives.LastResult);
        fields.Add("observer-human-mind", "actors-with-place-knowledge", _locationKnowledge.ActorCount);
        fields.Add("observer-affordances", "catalog-version", _affordances.Version);
        fields.Add("observer-affordances", "entries", _affordances.EntryCount);

        fields.Add("observer-pipeline", "projection-ready", _zoneStates.InitialLoadComplete);
        fields.Add("observer-pipeline", "projected-scopes", _zoneStates.ScopeCount);
        fields.Add("observer-pipeline", "dirty-scopes", _zoneStates.PendingScopeCount);
        fields.Add("observer-physical-lifecycle", "active", _physicalLifecycle.IsActive);
        fields.Add("observer-physical-lifecycle", "physical", _actorRuntime.PhysicalActorCount);
        fields.Add("observer-physical-lifecycle", "queued", _physicalLifecycle.QueuedCount);
        fields.Add("observer-physical-lifecycle", "loads-in-flight", _physicalLifecycle.LoadsInFlight);
        fields.Add("observer-gpt-brain", "scheduled", _physicalBrain.ScheduledActors);
        fields.Add("observer-gpt-brain", "in-flight", _physicalBrain.SubmissionsInFlight);
        fields.Add("observer-gpt-brain", "submitted", _physicalBrain.DecisionsSubmitted);
        fields.Add("observer-gpt-brain", "retries", _physicalBrain.SubmissionRetries);
        fields.Add("observer-gpt-actions", "running", _neuralExecution.RunningCount);
        fields.Add("observer-gpt-actions", "completed", _neuralExecution.CompletedCount);
        fields.Add("observer-gpt-actions", "failed", _neuralExecution.FailedCount);
        return fields.Build();
    }

    private NCLivingCityAdminObserverZone[] BuildObserverZones(
        IReadOnlyList<NCCityZoneObserverProjection> projections,
        string filter,
        out string? error)
    {
        using var adminWork = _loadGovernor.MeasureWork(NCAILoadWorkKind.AdminSnapshots);
        error = null;
        IEnumerable<NCCityZoneObserverProjection> selected;
        if (filter.Length == 0)
        {
            selected = projections.Where(static value => value.ScopeSlot == NCZoneContextSlot.District);
        }
        else
        {
            if (!NCZoneId.TryParse(filter, out var zoneId))
            {
                error = "invalid-zone-id";
                return [];
            }
            selected = projections.Where(value => value.ScopeZone == zoneId);
        }

        var result = new List<NCLivingCityAdminObserverZone>();
        foreach (var projection in selected.Take(MaximumObserverZones))
        {
            if (!_zones.TryGetZone(projection.ScopeZone, out var zone))
                continue;
            result.Add(new NCLivingCityAdminObserverZone(
                projection.ScopeZone.ToString(),
                BoundObserverText(zone.Name, 128),
                projection.ScopeSlot.ToString(),
                projection.TotalActors,
                projection.LivingActors,
                projection.FrozenActors,
                projection.TravelingActors,
                projection.PublicActors,
                projection.AbstractHomeActors,
                projection.ResidenceAssignedActors,
                projection.Activity,
                projection.Scarcity,
                projection.Threat,
                projection.Control));
        }

        if (filter.Length > 0 && result.Count == 0)
            error = "zone-state-not-found";
        return result.ToArray();
    }

    private async Task<(NCLivingCityAdminField[] Fields, string? Error)> BuildObserverActorAsync(string target)
    {
        if (!Guid.TryParse(target, out var raw))
            return ([], "invalid-actor-id");
        var actorId = new NCActorId(raw);
        var actor = await _actors.GetAsync(actorId);
        if (actor == null)
            return ([], "actor-not-found");

        var fields = new FieldBuilder();
        var snapshot = actor.Snapshot;
        fields.Add("observer-actor", "actor-id", actorId);
        fields.Add("observer-actor", "full-name", snapshot.Identity.FullName);
        fields.Add("observer-actor", "archetype", snapshot.Identity.ArchetypeId);
        fields.Add("observer-actor", "appearance-seed", snapshot.Identity.AppearanceSeed);
        fields.Add("observer-actor", "biography-seed", snapshot.Identity.BiographySeed);
        if (_prototypes.TryIndex(new ProtoId<NCCityActorArchetypePrototype>(snapshot.Identity.ArchetypeId),
                out NCCityActorArchetypePrototype? archetype) && archetype != null)
        {
            var narrativeProfile = snapshot.Identity.NarrativeActorProfileId ?? archetype.NarrativeActorProfile?.Id;
            fields.Add("observer-actor", "population-class",
                narrativeProfile == null ? "Resident" : "Actor");
            fields.Add("observer-actor", "narrative-profile",
                narrativeProfile ?? "none");
            if (narrativeProfile != null &&
                _prototypes.TryIndex(new ProtoId<NCNarrativeActorProfilePrototype>(narrativeProfile),
                    out NCNarrativeActorProfilePrototype? narrative) &&
                _prototypes.TryIndex(narrative.PersonaProfile, out NCNeuralPersonaProfilePrototype? persona))
            {
                fields.Add("observer-personality", "biography", BoundObserverText(persona.Biography, 512));
                fields.Add("observer-personality", "habits", string.Join(", ", persona.HabitKeys));
            }
        }
        fields.Add("observer-actor", "status", snapshot.Lifecycle.Status);
        fields.Add("observer-actor", "frozen", snapshot.Lifecycle.Frozen);
        fields.Add("observer-actor", "cognition-tier", snapshot.CognitionTier);
        fields.Add("observer-actor", "aggregate-version", actor.AggregateVersion);

        fields.Add("observer-location", "current-zone", snapshot.Location.CurrentZone);
        fields.Add("observer-location", "destination-zone", snapshot.Location.DestinationZone);
        fields.Add("observer-location", "home-zone", snapshot.Location.HomeZone);
        fields.Add("observer-location", "work-zone", snapshot.Location.WorkZone);
        fields.Add("observer-location", "travel-phase", snapshot.Location.TravelPhase);
        fields.Add("observer-location", "travel-mode", snapshot.Location.TravelMode);
        fields.Add("observer-location", "expected-arrival", snapshot.Location.ExpectedArrivalAt?.Elapsed);
        fields.Add("observer-location", "presence", snapshot.Schedule.PresenceMode);
        fields.Add("observer-location", "known-zones", _locationKnowledge.CountKnown(actorId));

        var residence = snapshot.Location.Residence;
        fields.Add("observer-residence", "assigned", residence is { ResidenceId.IsValid: true });
        if (residence is { ResidenceId.IsValid: true })
        {
            fields.Add("observer-residence", "residence-id", residence.ResidenceId);
            fields.Add("observer-residence", "residence-profile", residence.ProfileId);
            fields.Add("observer-residence", "residence-kind", residence.Kind);
            fields.Add("observer-residence", "residence-quality", residence.Quality);
            fields.Add("observer-residence", "residence-district", residence.DistrictZone);
            fields.Add("observer-residence", "residence-local-zone", residence.LocalZone);
            fields.Add("observer-residence", "weekly-cost", residence.WeeklyCost);
        }

        fields.Add("observer-intent", "activity", snapshot.Schedule.CurrentActivity);
        fields.Add("observer-intent", "goal", snapshot.Schedule.CurrentGoal);
        fields.Add("observer-intent", "availability", snapshot.Schedule.Availability);
        fields.Add("observer-intent", "next-update", snapshot.Schedule.NextUpdateAt.Elapsed);
        fields.Add("observer-intent", "commitments", snapshot.Schedule.Commitments.Length);
        fields.Add("observer-intent", "late",
            snapshot.Schedule.CurrentActivity.Equals("work", StringComparison.OrdinalIgnoreCase) &&
            snapshot.Location.CurrentZone != snapshot.Location.WorkZone);
        fields.Add("observer-intent", "physical", _actorRuntime.TryGetEntity(actorId, out var entityUid));
        fields.Add("observer-intent", "entity", entityUid.IsValid() ? entityUid : "recovery-pending");

        var employment = await _employment.FindCurrentByEmployeeAsync(NCPartyRef.ForActor(actorId));
        if (employment != null)
        {
            fields.Add("observer-employment", "employer", employment.Snapshot.Employer);
            fields.Add("observer-employment", "position", employment.Snapshot.PositionId);
            fields.Add("observer-employment", "status", employment.Snapshot.Status);
            fields.Add("observer-employment", "salary", employment.Snapshot.SalaryAmount);
        }
        else
        {
            fields.Add("observer-employment", "status", "unemployed");
        }
        if (_professions.TryGetProfile(actorId, out var profession))
        {
            fields.Add("observer-profession", "profile", profession.ID);
            fields.Add("observer-profession", "organization", profession.Organization);
            fields.Add("observer-profession", "branch", profession.BranchKey);
            fields.Add("observer-profession", "shift-profile", snapshot.Schedule.RoutineProfileId);
            fields.Add("observer-profession", "workplace", profession.WorkAffordance);
            if (_workforce.TryGetAssignment(actorId, out var organization, out var job) &&
                _prototypes.TryIndex(organization, out Content.Shared.Roles.DepartmentPrototype? department) &&
                department != null && _prototypes.TryIndex(job, out Content.Shared.Roles.JobPrototype? position) &&
                position != null)
            {
                fields.Add("observer-profession", "position", job);
                fields.Add("observer-profession", "authority-rank",
                    department.NCPositionRanks.GetValueOrDefault(job));
                fields.Add("observer-profession", "equipment", position.StartingGear?.Id ?? "none");
            }
            fields.Add("observer-profession", "skills", string.Join(", ", profession.SkillKeys));
            fields.Add("observer-profession", "capability-overlays",
                string.Join(", ", profession.CapabilityOverlays.Select(value => value.Id)));
        }

        var livelihood = await _livelihoods.GetAsync(actorId);
        fields.Add("observer-livelihood", "profile", livelihood?.Snapshot.ProfileId ?? "none");

        var memoryBook = await _memories.GetAsync(NCPartyRef.ForActor(actorId));
        fields.Add("observer-memory", "episodes", memoryBook?.Snapshot.Episodes.Count ?? 0);
        fields.Add("observer-memory", "summaries", memoryBook?.Snapshot.Summaries.Count ?? 0);
        fields.Add("observer-memory", "total-remembered", memoryBook?.Snapshot.TotalRemembered ?? 0);
        if (memoryBook?.Snapshot.Episodes.OrderByDescending(value => value.CreatedAt).FirstOrDefault() is { } episode)
        {
            fields.Add("observer-memory", "latest-event", episode.EventTypeKey);
            fields.Add("observer-memory", "latest-at", episode.CreatedAt.Elapsed);
            fields.Add("observer-memory", "latest-emotions", string.Join(", ", episode.EmotionalTagKeys));
        }

        var social = await _socialProfiles.GetAsync(actorId);
        if (social != null)
        {
            fields.Add("observer-personality", "temperament", social.Snapshot.Personality.Temperament);
            fields.Add("observer-personality", "risk", social.Snapshot.Personality.RiskTolerance);
            fields.Add("observer-personality", "aggression", social.Snapshot.Personality.Aggression);
            fields.Add("observer-personality", "empathy", social.Snapshot.Personality.Empathy);
            fields.Add("observer-personality", "speech-register", social.Snapshot.Speech.Register);
            fields.Add("observer-personality", "speech-vocabulary", social.Snapshot.Speech.Vocabulary);
            fields.Add("observer-personality", "language", social.Snapshot.Speech.PreferredLanguage);
        }

        if (_clock.TryGetCurrentTime(out _, out var observerNow) &&
            _localPerception.TryGetSnapshot(actorId, observerNow, out var observations, out var perceivedEvents))
        {
            fields.Add("observer-perception", "subjects", observations.Length);
            fields.Add("observer-perception", "events", perceivedEvents.Length);
            fields.Add("observer-perception", "situational-state", InferSituationalState(perceivedEvents));
            foreach (var observed in observations.Take(8))
            {
                var section = $"observer-perception-subject-{observed.SubjectHandle}";
                fields.Add(section, "kind", observed.Kind);
                fields.Add(section, "name", BoundObserverText(Name(observed.EntityUid), 128));
                fields.Add(section, "confidence", observed.Confidence.ToString("0.00"));
            }
            foreach (var perceived in perceivedEvents.Take(8))
            {
                var section = $"observer-perception-event-{perceived.EventId:N}";
                fields.Add(section, "kind", perceived.Kind);
                fields.Add(section, "actor", perceived.ActorHandle);
                fields.Add(section, "target", perceived.TargetHandle);
                fields.Add(section, "observed-at", perceived.ObservedAt.Elapsed);
            }
        }
        var conversations = _conversations.GetForParticipant(NCPartyRef.ForActor(actorId), 50);
        var activeConversations = conversations.Where(value => value.Status is NCConversationStatus.Starting or
            NCConversationStatus.Active or NCConversationStatus.WaitingForActor or
            NCConversationStatus.WaitingForPlayer).ToArray();
        fields.Add("observer-memory", "active-conversations", activeConversations.Length);
        if (activeConversations.FirstOrDefault() is { } conversation)
        {
            fields.Add("observer-conversation", "session", conversation.SessionId);
            fields.Add("observer-conversation", "channel", conversation.Channel);
            fields.Add("observer-conversation", "status", conversation.Status);
            fields.Add("observer-conversation", "version", conversation.ConversationVersion);
            fields.Add("observer-conversation", "participants", conversation.Participants.Count);
            fields.Add("observer-conversation", "topics", string.Join(", ", conversation.CurrentTopicKeys));
            fields.Add("observer-conversation", "turns", conversation.Turns.Count);
        }

        var neuralRuntime = _neural.GetRuntimeSnapshot();
        fields.Add("observer-neural", "runtime-data", _neural.TryGetActorRuntimeSnapshot(actorId, out var neural));
        fields.Add("observer-neural", "budget-mode", neuralRuntime.BudgetMode);
        if (neural != null)
        {
            fields.Add("observer-neural", "access-tier", neural.AccessTier);
            fields.Add("observer-neural", "outstanding-requests", neural.OutstandingRequests);
            fields.Add("observer-neural", "session-token-hard-cap", neural.SessionTokenHardCap);
            fields.Add("observer-neural", "input-tokens", neural.InputTokens);
            fields.Add("observer-neural", "cached-input-tokens", neural.CachedInputTokens);
            fields.Add("observer-neural", "output-tokens", neural.OutputTokens);
            fields.Add("observer-neural", "cost-usd-micros", neural.CostUsdMicros);
            fields.Add("observer-neural", "results", neural.Results);
            fields.Add("observer-neural", "accepted", neural.Accepted);
            fields.Add("observer-neural", "last-request", neural.LastRequestId);
            fields.Add("observer-neural", "last-kind", neural.LastRequestKind);
            fields.Add("observer-neural", "last-status", neural.LastStatus);
            fields.Add("observer-neural", "last-failure-kind", neural.LastFailureKind);
            fields.Add("observer-neural", "last-reason",
                neural.LastReason.Length == 0 ? "none" : neural.LastReason);
            fields.Add("observer-neural", "last-capability",
                neural.LastCapabilityId.Length == 0 ? "none" : neural.LastCapabilityId);
            fields.Add("observer-neural", "last-summary",
                neural.LastSummaryKey.Length == 0 ? "none" : neural.LastSummaryKey);
            fields.Add("observer-neural", "provider-model",
                neural.LastProviderModel.Length == 0 ? "none" : neural.LastProviderModel);
        }
        if (entityUid.IsValid() && TryComp<NCHumanDirectiveComponent>(entityUid, out var directive))
        {
            fields.Add("observer-directive", "request", directive.RequestId);
            fields.Add("observer-directive", "action", directive.Action);
            fields.Add("observer-directive", "order", directive.Order);
            fields.Add("observer-directive", "target-zone", directive.TargetZone);
            fields.Add("observer-directive", "accepted-at", directive.AcceptedAt.Elapsed);
        }

        var affiliations = await _factionMemberships.GetAsync(actorId);
        fields.Add("observer-affiliations", "count", affiliations?.Snapshot.Memberships.Count ?? 0);
        if (affiliations != null)
        {
            for (var index = 0; index < Math.Min(affiliations.Snapshot.Memberships.Count, 8); index++)
            {
                var membership = affiliations.Snapshot.Memberships[index];
                var section = $"observer-affiliation-{index + 1}";
                fields.Add(section, "faction", membership.FactionId);
                fields.Add(section, "role", membership.Role);
                fields.Add(section, "status", membership.Status);
            }
        }
        if (_neuralSuggestions.TryGetSuggestionSnapshot(actorId, out var suggestion))
        {
            fields.Add("observer-gpt-decision", "request", suggestion.RequestId);
            fields.Add("observer-gpt-decision", "capability", suggestion.CapabilityId);
            fields.Add("observer-gpt-decision", "goal", suggestion.GoalKey);
            fields.Add("observer-gpt-decision", "target", suggestion.TargetHandle);
            fields.Add("observer-gpt-decision", "confidence", suggestion.Confidence);
            fields.Add("observer-gpt-decision", "accepted-at", suggestion.AcceptedAt.Elapsed);
            fields.Add("observer-gpt-decision", "expires-at", suggestion.ExpiresAt.Elapsed);
        }

        if (_neuralExecution.TryGetSnapshot(actorId, out var plan))
        {
            fields.Add("observer-gpt-plan", "id", plan.PlanId);
            fields.Add("observer-gpt-plan", "capability", plan.CapabilityId);
            fields.Add("observer-gpt-plan", "step", $"{plan.CurrentStep}/{plan.StepCount}");
            fields.Add("observer-gpt-plan", "expires-at", plan.ExpiresAt.Elapsed);
        }

        if (_humanActions.TryGetSnapshot(actorId, out var action))
        {
            fields.Add("observer-human-action", "id", action.ActionId);
            fields.Add("observer-human-action", "state", action.State);
            fields.Add("observer-human-action", "executor", action.Executor);
            fields.Add("observer-human-action", "target", action.Target);
            fields.Add("observer-human-action", "started-at", action.StartedAt.Elapsed);
            fields.Add("observer-human-action", "updated-at", action.UpdatedAt.Elapsed);
            fields.Add("observer-human-action", "timeout-at", action.TimeoutAt.Elapsed);
            fields.Add("observer-human-action", "failure", action.Failure ?? "none");
            foreach (var trace in action.Trace.TakeLast(8))
            {
                var section = $"observer-human-action-trace-{trace.Sequence}";
                fields.Add(section, "at", trace.At.Elapsed);
                fields.Add(section, "state", trace.State);
                fields.Add(section, "event", trace.Event);
                fields.Add(section, "detail", trace.Detail);
            }
        }

        return (fields.Build(), null);
    }

    private void SendObserver(
        EntitySessionEventArgs args,
        Guid requestId,
        bool authorized,
        NCLivingCityAdminField[] summary,
        NCLivingCityAdminObserverZone[] zones,
        NCLivingCityAdminField[] actorFields,
        string? actorError,
        string? error)
    {
        NCLivingCityAdminTelemetry.Record(authorized && error == null, error, summary);
        RaiseNetworkEvent(new NCLivingCityAdminObserverSnapshot(
            requestId,
            authorized,
            summary,
            zones,
            actorFields,
            actorError,
            error), args.SenderSession);
    }

    private static string BoundObserverText(string value, int maximum)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum];
    }

    private static string InferSituationalState(IEnumerable<NCCityLocalPerceivedEvent> events)
    {
        var kinds = events.Select(value => value.Kind).ToHashSet();
        if (kinds.Contains(NCCityLocalPerceivedEventKind.Killing) ||
            kinds.Contains(NCCityLocalPerceivedEventKind.BodyDiscovered))
            return "shocked";
        if (kinds.Overlaps([
                NCCityLocalPerceivedEventKind.Assault,
                NCCityLocalPerceivedEventKind.Gunshot,
                NCCityLocalPerceivedEventKind.WeaponDisplayed,
                NCCityLocalPerceivedEventKind.Fire,
                NCCityLocalPerceivedEventKind.SevereInjury,
            ]))
            return "alert";
        return "baseline";
    }
}
