// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Content.Server.Administration.Logs;
using Content.Server.Database;
using Content.Server._NC.City.Connectivity;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Server._NC.LivingCity.Ambient;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.CityServices;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Employment;
using Content.Server._NC.LivingCity.Groups;
using Content.Server._NC.LivingCity.Housing;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Market;
using Content.Server._NC.LivingCity.Lifecycle;
using Content.Server._NC.LivingCity.Neural;
using Content.Server._NC.LivingCity.Observability;
using Content.Server._NC.LivingCity.Police;
using Content.Server._NC.LivingCity.Population;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Simulation;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Persistence.Outbox;
using Content.Shared._NC.LivingCity.Admin;
using Content.Shared._NC.City.Zones;
using Content.Shared.Administration;
using Content.Shared.Administration.Managers;
using Content.Shared.Database;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.LivingCity.Admin;

/// <summary>
/// Permission-checked, request-driven F7 administration boundary. Every response is bounded and redacted; mutation
/// handlers live in domain-specific partials and call canonical domain systems directly.
/// </summary>
public sealed partial class NCLivingCityAdminSystem : EntitySystem
{
    private const int MaximumFields = 256;
    private const int MaximumSectionLength = 64;
    private const int MaximumKeyLength = 96;
    private const int MaximumValueLength = 512;
    private const int MaximumTargetLength = 160;
    private static readonly TimeSpan RequestInterval = TimeSpan.FromMilliseconds(200);

    [Dependency] private ISharedAdminManager _admins = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCCityConnectivitySystem _cityConnectivity = default!;
    [Dependency] private NCRoomPortalGraphSystem _roomConnectivity = default!;
    [Dependency] private NCCitySpawnCandidateSystem _spawnCandidates = default!;
    [Dependency] private NCCityConnectivityValidationSystem _connectivityValidation = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorDomainRegistrySystem _actorDomains = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _actorRuntime = default!;
    [Dependency] private NCCityActorMaterializationSystem _materialization = default!;
    [Dependency] private NCCityActorDematerializationSystem _dematerialization = default!;
    [Dependency] private NCCityRelevanceSystem _relevance = default!;
    [Dependency] private NCAmbientCrowdSystem _ambient = default!;
    [Dependency] private NCCityActorScheduleSchedulerSystem _scheduler = default!;
    [Dependency] private NCCityAbstractTravelSystem _travel = default!;
    [Dependency] private NCCityPhysicalExecutionSystem _physicalExecution = default!;
    [Dependency] private NCCognitionRegistrySystem _cognitionRegistry = default!;
    [Dependency] private NCCognitionSystem _cognition = default!;
    [Dependency] private NCCityPopulationImportSystem _population = default!;
    [Dependency] private NCCityZoneStateRegistrySystem _zoneStateRegistry = default!;
    [Dependency] private NCCityZoneStateProjectionSystem _zoneStates = default!;
    [Dependency] private NCCityGroupRegistrySystem _groups = default!;
    [Dependency] private NCCityGroupPhysicalizationSystem _groupPhysicalization = default!;
    [Dependency] private NCCityGroupRuntimeFormationSystem _groupFormations = default!;
    [Dependency] private NCCityGroupTrunkRouteSystem _groupRoutes = default!;
    [Dependency] private NCCityLocalPerceptionSystem _localPerception = default!;
    [Dependency] private NCCityPhysicalStateTreeSystem _physicalStateTree = default!;
    [Dependency] private NCCityPhysicalActionResolutionSystem _physicalActions = default!;
    [Dependency] private NCPartyDirectorySystem _parties = default!;
    [Dependency] private NCPartyResolverSystem _partyResolver = default!;
    [Dependency] private NCEconomyLedgerSystem _economy = default!;
    [Dependency] private NCEconomicAssetRegistrySystem _economicAssets = default!;
    [Dependency] private NCEconomicReconciliationSystem _economicReconciliation = default!;
    [Dependency] private NCMarketRegistrySystem _market = default!;
    [Dependency] private NCBusinessRegistrySystem _businesses = default!;
    [Dependency] private NCProductionSystem _production = default!;
    [Dependency] private NCShipmentSystem _shipments = default!;
    [Dependency] private NCShipmentCargoSystem _shipmentCargo = default!;
    [Dependency] private NCEmploymentRegistrySystem _employment = default!;
    [Dependency] private NCPropertyRegistrySystem _properties = default!;
    [Dependency] private NCPropertyContentSnapshotSystem _propertyContents = default!;
    [Dependency] private NCPoliceFineRegistrySystem _fines = default!;
    [Dependency] private NCRelationshipRegistrySystem _relationships = default!;
    [Dependency] private NCReputationRegistrySystem _reputations = default!;
    [Dependency] private NCMemoryRegistrySystem _memories = default!;
    [Dependency] private NCSocialProfileRegistrySystem _socialProfiles = default!;
    [Dependency] private NCRumorRegistrySystem _rumors = default!;
    [Dependency] private NCSocialObligationRegistrySystem _socialObligations = default!;
    [Dependency] private NCConversationSessionSystem _conversations = default!;
    [Dependency] private NCPersistentOutboxDispatcherSystem _outbox = default!;
    [Dependency] private NCAILoadGovernorSystem _loadGovernor = default!;
    [Dependency] private NCCityIncidentRegistrySystem _incidents = default!;
    [Dependency] private NCCityServiceSystem _cityServices = default!;
    [Dependency] private NCFactionRegistrySystem _factions = default!;
    [Dependency] private NCDistrictSafetyProjectionSystem _districtSafety = default!;
    [Dependency] private NCCityNewsRegistrySystem _cityNews = default!;
    [Dependency] private NCEstateRegistrySystem _estates = default!;
    [Dependency] private NCNeuralCognitionClientSystem _neural = default!;
    [Dependency] private NCLivingCityObservabilitySystem _observability = default!;

    private readonly Dictionary<NetUserId, TimeSpan> _lastRequestAt = [];
    private readonly NCLivingCityAdminConfirmationStore _confirmations = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCLivingCityAdminOverviewRequest>(OnOverviewRequest);
        SubscribeNetworkEvent<NCLivingCityAdminInspectRequest>(OnInspectRequest);
        SubscribeNetworkEvent<NCLivingCityAdminActorActionRequest>(OnActorActionRequest);
        SubscribeNetworkEvent<NCLivingCityAdminGroupActionRequest>(OnGroupActionRequest);
        SubscribeNetworkEvent<NCLivingCityAdminPopulationActionRequest>(OnPopulationActionRequest);
        SubscribeNetworkEvent<NCLivingCityAdminInfrastructureActionRequest>(OnInfrastructureActionRequest);
        SubscribeNetworkEvent<NCLivingCityAdminGeographyActionRequest>(OnGeographyActionRequest);
    }

    public override void Shutdown()
    {
        _lastRequestAt.Clear();
        _confirmations.Clear();
        base.Shutdown();
    }

    private async void OnOverviewRequest(NCLivingCityAdminOverviewRequest request, EntitySessionEventArgs args)
    {
        if (request.RequestId == Guid.Empty)
            return;
        if (!CanRead(args))
        {
            SendOverview(args, request.RequestId, false, [], "not-authorized");
            return;
        }
        if (!AcceptRate(args.SenderSession.UserId))
        {
            SendOverview(args, request.RequestId, true, [], "rate-limited");
            return;
        }

        var fields = new FieldBuilder();
        if (_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            fields.Add("clock", "state", "active");
            fields.Add("clock", "epoch", epoch.ToString());
            fields.Add("clock", "active-world-time", now.Elapsed.ToString());
        }
        else
        {
            fields.Add("clock", "state", "inactive");
        }

        fields.Add("actors", "cached", _actors.CachedActorCount);
        fields.Add("actors", "materialized", _actorRuntime.MaterializedActorCount);
        var ambient = _ambient.GetCounters();
        fields.Add("ambient", "active", ambient.Active);
        fields.Add("ambient", "interactive", ambient.InteractiveEntities);
        fields.Add("ambient", "decorative", ambient.DecorativeVisuals);
        fields.Add("ambient", "spawned", ambient.Spawned);
        fields.Add("ambient", "despawned", ambient.Despawned);
        fields.Add("ambient", "spawn-rejected", ambient.SpawnRejected);
        fields.Add("ambient", "threat-reactions", ambient.ThreatReactions);
        fields.Add("ambient", "configuration-error", ambient.ConfigurationError ?? "none");
        fields.Add("materialization", "active", _materialization.IsActive);
        fields.Add("materialization", "queued", _materialization.PendingRequestCount);
        fields.Add("materialization", "in-flight", _materialization.InFlightCount);
        fields.Add("materialization", "retained", _materialization.RetainedResultCount);
        fields.Add("dematerialization", "active", _dematerialization.IsActive);
        fields.Add("dematerialization", "queued", _dematerialization.PendingRequestCount);
        fields.Add("dematerialization", "in-flight", _dematerialization.InFlightCount);
        fields.Add("dematerialization", "retained", _dematerialization.RetainedResultCount);
        fields.Add("relevance", "active", _relevance.IsActive);
        fields.Add("relevance", "catalog-ready", _relevance.CatalogRecovered);
        fields.Add("relevance", "catalog-actors", _relevance.CatalogActorCount);
        fields.Add("relevance", "clusters", _relevance.ClusterCount);
        fields.Add("scheduler", "initial-load-complete", _scheduler.InitialLoadComplete);
        fields.Add("scheduler", "queued", _scheduler.QueuedActorCount);
        fields.Add("scheduler", "in-flight", _scheduler.InFlightCount);
        fields.Add("scheduler", "pending-retries", _scheduler.PendingMutationCount);
        fields.Add("abstract-travel", "initial-load-complete", _travel.InitialLoadComplete);
        fields.Add("abstract-travel", "queued", _travel.QueuedActorCount);
        fields.Add("abstract-travel", "in-flight", _travel.InFlightCount);
        fields.Add("abstract-travel", "pending-retries", _travel.PendingMutationCount);

        var physical = _physicalExecution.GetCounters();
        fields.Add("physical-execution", "active", physical.Active);
        fields.Add("physical-execution", "tracked", physical.TrackedActors);
        fields.Add("physical-execution", "scheduled", physical.ScheduledExecutions);
        fields.Add("physical-execution", "intent-loads-queued", physical.QueuedIntentLoads);
        fields.Add("physical-execution", "intent-loads-in-flight", physical.IntentLoadsInFlight);
        fields.Add("physical-execution", "mutations-in-flight", physical.MutationsInFlight);
        fields.Add("physical-execution", "plans-built", physical.PlansBuilt);
        fields.Add("physical-execution", "waypoints-completed", physical.WaypointsCompleted);
        fields.Add("physical-execution", "replans", physical.Replans);
        fields.Add("physical-execution", "portal-traversals", physical.PortalTraversals);
        fields.Add("physical-execution", "arrivals", physical.Arrivals);
        fields.Add("physical-execution", "blocked-goals", physical.BlockedGoals);
        fields.Add("physical-execution", "failed-goals", physical.FailedGoals);
        fields.Add("physical-execution", "configuration-error", physical.ConfigurationError ?? "none");

        var stateTree = _physicalStateTree.GetCounters();
        fields.Add("physical-state-tree", "evaluations", stateTree.Evaluations);
        fields.Add("physical-state-tree", "transitions", stateTree.Transitions);
        fields.Add("physical-state-tree", "invalid-definitions", stateTree.InvalidDefinitions);
        var perception = _localPerception.GetCounters();
        fields.Add("local-perception", "active", perception.Active);
        fields.Add("local-perception", "tracked", perception.TrackedActors);
        fields.Add("local-perception", "scheduled", perception.ScheduledUpdates);
        fields.Add("local-perception", "evaluations", perception.Evaluations);
        fields.Add("local-perception", "observations", perception.Observations);
        fields.Add("local-perception", "rejected-configurations", perception.RejectedConfigurations);
        fields.Add("local-perception", "configuration-error", perception.ConfigurationError ?? "none");
        var actions = _physicalActions.GetCounters();
        fields.Add("physical-actions", "attempts", actions.Attempts);
        fields.Add("physical-actions", "succeeded", actions.Succeeded);
        fields.Add("physical-actions", "failed", actions.Failed);
        fields.Add("physical-actions", "unavailable", actions.Unavailable);
        fields.Add("physical-actions", "unhandled", actions.Unhandled);

        var cognition = _cognition.GetCounters();
        fields.Add("cognition", "active", cognition.Active);
        fields.Add("cognition", "tracked", cognition.TrackedActors);
        fields.Add("cognition", "scheduled", cognition.ScheduledActors);
        fields.Add("cognition", "loads-in-flight", cognition.LoadsInFlight);
        fields.Add("cognition", "planning-frontiers", cognition.PlanningFrontiers);
        fields.Add("cognition", "cached-plans", cognition.CachedPlans);
        fields.Add("cognition", "pending-invalidations", cognition.PendingInvalidations);
        fields.Add("cognition", "decisions", cognition.Decisions);
        fields.Add("cognition", "intention-switches", cognition.IntentionSwitches);
        fields.Add("cognition", "plans-built", cognition.PlansBuilt);
        fields.Add("cognition", "plans-invalidated", cognition.PlansInvalidated);
        fields.Add("cognition", "fallback-plans", cognition.FallbackPlans);
        fields.Add("cognition", "mcts-nodes-expanded", cognition.MctsNodesExpanded);
        fields.Add("cognition", "configuration-error", cognition.ConfigurationError ?? "none");

        fields.Add("incidents", "cached", _incidents.CachedCount);
        fields.Add("services", "cached", _cityServices.CachedServiceCount);
        fields.Add("services", "contracts", _cityServices.CachedContractCount);
        fields.Add("services", "dispatches", _cityServices.CachedDispatchCount);
        var neural = _neural.GetRuntimeSnapshot();
        fields.Add("neural", "active", neural.Active);
        fields.Add("neural", "budget-mode", neural.BudgetMode);
        fields.Add("neural", "queued", neural.Queued);
        fields.Add("neural", "in-flight", neural.InFlight);
        fields.Add("neural", "tokens", neural.SessionInputTokens + neural.SessionOutputTokens);
        fields.Add("neural", "accepted", neural.Accepted);
        fields.Add("neural", "fallbacks", neural.Fallbacks);
        fields.Add("neural", "validation-rejects", neural.ValidationRejects);
        fields.Add("neural", "timeouts", neural.Timeouts);
        fields.Add("neural", "consecutive-failures", neural.ConsecutiveFailures);
        fields.Add("neural", "circuit-open-until", neural.CircuitOpenUntil?.Elapsed.ToString() ?? "none");

        var load = _loadGovernor.GetSnapshot();
        fields.Add("load-governor", "active", load.Active);
        fields.Add("load-governor", "state", load.State);
        fields.Add("load-governor", "pressure-reasons", load.Reasons);
        fields.Add("load-governor", "smoothed-frame-ms", load.SmoothedFrameMilliseconds.ToString("0.00"));
        fields.Add("load-governor", "alife-work-ratio", load.AILifeWorkRatio.ToString("P1"));
        fields.Add("load-governor", "materialized", load.MaterializedActors);
        fields.Add("load-governor", "reserved-materializations", load.ReservedMaterializations);
        fields.Add("load-governor", "effective-soft-limit", load.EffectiveSoftMaterializedBudget);
        fields.Add("load-governor", "hard-limit", load.HardMaterializedBudget);
        fields.Add("load-governor", "maximum-queue-ratio", load.MaximumQueueRatio.ToString("P1"));
        fields.Add("load-governor", "backlogged-subsystems", load.BackloggedSubsystems);
        fields.Add("load-governor", "candidate-samples", load.CandidateSamples);
        fields.Add("load-governor", "transitions", load.TransitionCount);
        fields.Add("load-governor", "last-transition", load.LastTransitionAt.IsValid
            ? load.LastTransitionAt.Elapsed.ToString()
            : "none");
        fields.Add("load-governor", "admissions-accepted", load.AdmissionAccepted);
        fields.Add("load-governor", "admissions-rejected", load.AdmissionRejected);
        fields.Add("load-governor", "configuration-error", load.ConfigurationError ?? "none");

        fields.Add("groups", "cached", _groups.CachedGroupCount);
        fields.Add("groups", "cached-memberships", _groups.CachedMembershipCount);
        fields.Add("group-physicalization", "active", _groupPhysicalization.IsActive);
        fields.Add("group-physicalization", "catalog-ready", _groupPhysicalization.CatalogRecovered);
        fields.Add("group-physicalization", "catalog-groups", _groupPhysicalization.CatalogGroupCount);
        fields.Add("group-physicalization", "materialized-groups", _groupFormations.MaterializedGroupCount);
        fields.Add("group-physicalization", "queued", _groupPhysicalization.PendingRequestCount);
        fields.Add("group-physicalization", "in-flight", _groupPhysicalization.InFlightCount);
        fields.Add("group-physicalization", "retained", _groupPhysicalization.RetainedResultCount);
        var trunks = _groupRoutes.GetCounters();
        fields.Add("group-trunk-routes", "cached", trunks.CachedRoutes);
        fields.Add("group-trunk-routes", "published", trunks.PublishedRoutes);
        fields.Add("group-trunk-routes", "cache-hits", trunks.CacheHits);
        fields.Add("group-trunk-routes", "invalidations", trunks.Invalidations);

        var population = _population.Progress;
        fields.Add("population-import", "running", population.Running);
        fields.Add("population-import", "profile", population.ProfileId ?? "none");
        fields.Add("population-import", "processed", $"{population.Processed}/{population.Requested}");
        fields.Add("population-import", "created", population.Created);
        fields.Add("population-import", "existing", population.Existing);
        fields.Add("population-import", "conflicts", population.Conflicts);
        fields.Add("population-import", "failed", population.Failed);
        fields.Add("zone-states", "initial-load-complete", _zoneStates.InitialLoadComplete);
        fields.Add("zone-states", "indexed-actors", _zoneStates.IndexedActorCount);
        fields.Add("zone-states", "scopes", _zoneStates.ScopeCount);
        fields.Add("zone-states", "pending", _zoneStates.PendingScopeCount);
        fields.Add("zone-states", "in-flight", _zoneStates.InFlightWriteCount);
        fields.Add("zone-states", "cached", _zoneStateRegistry.CachedScopeCount);

        var cityZones = new List<NCZoneInfo>();
        var cityRooms = new List<NCRoomNodeInfo>();
        var roomPortals = new List<NCRoomPortalInfo>();
        var cityPortals = new List<Content.Shared._NC.City.Connectivity.NCCityPortalInfo>();
        var connectivityIssues = new List<NCCityConnectivityValidationIssue>();
        _zones.GetZones(cityZones);
        _roomConnectivity.GetRooms(cityRooms);
        _roomConnectivity.GetPortals(roomPortals);
        var cityPortalCount = 0;
        foreach (var zone in cityZones)
        {
            _cityConnectivity.GetOutgoingPortals(zone.Id, cityPortals);
            cityPortalCount += cityPortals.Count;
        }
        var connectivity = _connectivityValidation.Validate(connectivityIssues);
        fields.Add("geography", "zones", cityZones.Count);
        fields.Add("geography", "city-graph-version", _cityConnectivity.GraphVersion);
        fields.Add("geography", "city-portals", cityPortalCount);
        fields.Add("geography", "room-graph-version", _roomConnectivity.GraphVersion);
        fields.Add("geography", "rooms", cityRooms.Count);
        fields.Add("geography", "room-portals", roomPortals.Count);
        fields.Add("geography", "spawn-index-version", _spawnCandidates.IndexVersion);
        fields.Add("geography", "validation-version", connectivity.ValidationVersion);
        fields.Add("geography", "validation-errors", connectivity.Errors);
        fields.Add("geography", "validation-warnings", connectivity.Warnings);

        fields.Add("authorities", "parties", _parties.CachedPartyCount);
        fields.Add("authorities", "accounts", _economy.CachedAccountCount);
        fields.Add("authorities", "transactions", _economy.CachedTransactionCount);
        fields.Add("economic-assets", "stock-nodes", _economicAssets.CachedStockNodeCount);
        fields.Add("economic-assets", "commodity-batches", _economicAssets.CachedCommodityBatchCount);
        fields.Add("economic-assets", "unique-items", _economicAssets.CachedItemInstanceCount);
        fields.Add("economic-assets", "quotes", _economicAssets.CachedQuoteCount);
        fields.Add("economic-assets", "reservations", _economicAssets.CachedReservationCount);
        fields.Add("economic-assets", "transfers", _economicAssets.CachedTransferCount);
        fields.Add("economic-assets", "fulfillments", _economicAssets.CachedFulfillmentCount);
        fields.Add("market", "listings", _market.CachedListingCount);
        fields.Add("market", "requests", _market.CachedRequestCount);
        fields.Add("market", "businesses", _businesses.CachedBusinessCount);
        fields.Add("market", "production-jobs", _production.CachedJobCount);
        fields.Add("market", "production-scheduled", _production.ScheduledJobCount);
        fields.Add("market", "shipments", _shipments.CachedShipmentCount);
        fields.Add("market", "shipments-scheduled", _shipments.ScheduledShipmentCount);
        fields.Add("market", "physical-cargo", _shipmentCargo.PhysicalCargoCount);
        fields.Add("market", "handoff-points", _shipmentCargo.HandoffPointCount);
        fields.Add("authorities", "employments", _employment.CachedEmploymentCount);
        fields.Add("authorities", "employment-indexes", _employment.CachedEmployeeIndexCount);
        fields.Add("authorities", "relationships", _relationships.CachedRelationshipCount);
        fields.Add("authorities", "relationship-indexes", _relationships.CachedObserverIndexCount);
        fields.Add("authorities", "reputations", _reputations.CachedReputationCount);
        fields.Add("authorities", "memory-books", _memories.CachedBookCount);
        fields.Add("authorities", "social-profiles", _socialProfiles.CachedProfileCount);
        fields.Add("authorities", "rumor-books", _rumors.CachedBookCount);
        fields.Add("authorities", "social-obligations", _socialObligations.CachedObligationCount);
        fields.Add("authorities", "social-obligation-indexes", _socialObligations.CachedIndexCount);
        fields.Add("runtime", "active-conversations", _conversations.ActiveSessionCount);
        fields.Add("runtime", "recent-conversations", _conversations.RecentSessionCount);

        fields.Add("outbox", "active", _outbox.IsActive);
        fields.Add("outbox", "fetch-in-flight", _outbox.FetchInFlight);
        fields.Add("outbox", "deliveries-in-flight", _outbox.DeliveriesInFlight);
        fields.Add("outbox", "handlers", _outbox.RegisteredHandlerCount);
        fields.Add("outbox", "delivered-session", _outbox.DeliveredThisSession);
        fields.Add("outbox", "failed-session", _outbox.FailedThisSession);
        fields.Add("outbox", "exhausted-session", _outbox.ExhaustedThisSession);
        fields.Add("outbox", "last-fetch-error", _outbox.LastFetchError ?? "none");

        try
        {
            var storageNow = DateTime.UtcNow;
            var outbox = await _database.GetNCOutboxStatusAsync(storageNow, Math.Max(1, _outbox.MaximumAttempts));
            fields.Add("outbox-storage", "pending", outbox.Pending);
            fields.Add("outbox-storage", "ready", outbox.Ready);
            fields.Add("outbox-storage", "exhausted", outbox.Exhausted);
            var oldestAge = outbox.OldestUnprocessedAt is { } pendingSince
                ? TimeSpan.FromTicks(Math.Max(0, (storageNow - pendingSince).Ticks))
                : (TimeSpan?) null;
            fields.Add("outbox-storage", "oldest-unprocessed-age",
                oldestAge?.ToString() ?? "none");
            _observability.ReportOutboxStatus(oldestAge, outbox.Exhausted);
        }
        catch (Exception exception)
        {
            Log.Error($"Living City F7 outbox status read failed: {exception}");
            fields.Add("outbox-storage", "error", "storage-read-failed");
        }

        var observability = _observability.GetSnapshot();
        fields.Add("observability", "active", observability.Active);
        fields.Add("observability", "configuration-error", observability.ConfigurationError ?? "none");
        fields.Add("observability", "frame-p50-ms", observability.Load.FrameP50Milliseconds.ToString("0.00"));
        fields.Add("observability", "frame-p95-ms", observability.Load.FrameP95Milliseconds.ToString("0.00"));
        fields.Add("observability", "frame-p99-ms", observability.Load.FrameP99Milliseconds.ToString("0.00"));
        fields.Add("observability", "alife-work-ms", observability.Load.AILifeWorkMilliseconds.ToString("0.00"));
        foreach (var (kind, milliseconds) in observability.Load.WorkMillisecondsByKind)
            fields.Add("alife-cpu", kind.ToString(), milliseconds.ToString("0.00"));
        fields.Add("observability", "allocated-sample-bytes", observability.Load.AllocatedBytesSinceLastSample);
        fields.Add("observability", "gc-collections",
            $"{observability.Load.Gen0CollectionsSinceLastSample}/" +
            $"{observability.Load.Gen1CollectionsSinceLastSample}/" +
            $"{observability.Load.Gen2CollectionsSinceLastSample}");
        fields.Add("persistence", "reads", observability.Persistence.Reads);
        fields.Add("persistence", "writes", observability.Persistence.Writes);
        fields.Add("persistence", "failures", observability.Persistence.Failures);
        fields.Add("persistence", "version-conflicts", observability.Persistence.VersionConflicts);
        fields.Add("persistence", "duplicate-replays", observability.Persistence.DuplicateReplays);
        fields.Add("persistence", "latency-p50-ms", observability.Persistence.LatencyP50Milliseconds.ToString("0.00"));
        fields.Add("persistence", "latency-p95-ms", observability.Persistence.LatencyP95Milliseconds.ToString("0.00"));
        fields.Add("persistence", "latency-p99-ms", observability.Persistence.LatencyP99Milliseconds.ToString("0.00"));
        fields.Add("admin-network", "responses", observability.Admin.Responses);
        fields.Add("admin-network", "rejected", observability.Admin.RejectedResponses);
        fields.Add("admin-network", "errors", observability.Admin.ErrorResponses);
        fields.Add("admin-network", "approximate-payload-bytes", observability.Admin.ApproximatePayloadBytes);
        fields.Add("alerts", "active", observability.Alerts.Count(alert => alert.Active));
        fields.Add("alerts", "retained", observability.Alerts.Count);
        foreach (var alert in observability.Alerts)
        {
            fields.Add("alerts", alert.Id,
                $"{(alert.Active ? "active" : "resolved")} {alert.Severity}; " +
                $"value={alert.Value:0.##}; threshold={alert.Threshold:0.##}; occurrences={alert.Occurrences}");
        }

        SendOverview(args, request.RequestId, true, fields.Build(), null);
    }

    private async void OnInspectRequest(NCLivingCityAdminInspectRequest request, EntitySessionEventArgs args)
    {
        if (request.RequestId == Guid.Empty)
            return;
        if (!CanRead(args))
        {
            SendInspection(args, request, false, false, [], "not-authorized");
            return;
        }
        if (!ValidInspectionRequest(request))
        {
            SendInspection(args, request, true, false, [], "invalid-request");
            return;
        }
        if (!AcceptRate(args.SenderSession.UserId))
        {
            SendInspection(args, request, true, false, [], "rate-limited");
            return;
        }

        try
        {
            var result = await InspectAsync(request);
            SendInspection(args, request, true, result.Success, result.Fields, result.Error);
        }
        catch (Exception exception)
        {
            Log.Error($"Living City F7 inspection {request.Kind} failed: {exception}");
            SendInspection(args, request, true, false, [], "storage-or-validation-failure");
        }
    }

    private bool CanRead(EntitySessionEventArgs args) =>
        _admins.HasAdminFlag(args.SenderSession, AdminFlags.Server) ||
        _admins.HasAdminFlag(args.SenderSession, AdminFlags.Mapping) ||
        _admins.HasAdminFlag(args.SenderSession, AdminFlags.Admin);

    private bool CanMutate(EntitySessionEventArgs args) =>
        _admins.HasAdminFlag(args.SenderSession, AdminFlags.Server) ||
        _admins.HasAdminFlag(args.SenderSession, AdminFlags.Mapping);

    private bool AcceptRate(NetUserId userId)
    {
        var now = _timing.RealTime;
        if (_lastRequestAt.TryGetValue(userId, out var last) && now - last < RequestInterval)
            return false;
        if (_lastRequestAt.Count >= 256 && !_lastRequestAt.ContainsKey(userId))
            _lastRequestAt.Clear();
        _lastRequestAt[userId] = now;
        return true;
    }

    private static bool ValidInspectionRequest(NCLivingCityAdminInspectRequest request) =>
        Enum.IsDefined(request.Kind) && request.Target != null && request.SecondaryTarget != null &&
        request.Target.Length <= MaximumTargetLength && request.SecondaryTarget.Length <= MaximumTargetLength &&
        !string.IsNullOrWhiteSpace(request.Target);

    private Guid IssueConfirmation(NetUserId userId, string fingerprint)
        => _confirmations.Issue(userId, fingerprint, _timing.RealTime, TimeSpan.FromSeconds(15));

    private bool ConsumeConfirmation(NetUserId userId, Guid token, string fingerprint)
        => _confirmations.Consume(userId, token, fingerprint, _timing.RealTime);

    private static string Fingerprint(params string[] values)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', values));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static bool SuccessfulResponse(NCLivingCityAdminMutationStatus status) => status is
        NCLivingCityAdminMutationStatus.ConfirmationRequired or
        NCLivingCityAdminMutationStatus.Applied or
        NCLivingCityAdminMutationStatus.Duplicate;

    private static string AdminAuditSource(NetUserId userId, string reason)
    {
        var prefix = $"admin-f7:{userId}:";
        var normalized = reason.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return prefix + normalized[..Math.Min(normalized.Length, Math.Max(0, 128 - prefix.Length))];
    }

    private void AddAdminAudit(
        EntitySessionEventArgs args,
        string operation,
        string target,
        string reason,
        string result)
    {
        var normalizedReason = reason.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalizedReason.Length > 256)
            normalizedReason = normalizedReason[..256];
        _adminLog.Add(
            LogType.Action,
            LogImpact.Medium,
            $"{args.SenderSession.Name} ({args.SenderSession.UserId}) used F7 Living City {operation} " +
            $"on {target}: result={result}; reason={normalizedReason}");
    }

    private void SendOverview(
        EntitySessionEventArgs args,
        Guid requestId,
        bool authorized,
        NCLivingCityAdminField[] fields,
        string? error)
    {
        NCLivingCityAdminTelemetry.Record(authorized && error == null, error, fields);
        RaiseNetworkEvent(new NCLivingCityAdminOverviewSnapshot(requestId, authorized, fields, error),
            args.SenderSession);
    }

    private void SendInspection(
        EntitySessionEventArgs args,
        NCLivingCityAdminInspectRequest request,
        bool authorized,
        bool success,
        NCLivingCityAdminField[] fields,
        string? error)
    {
        NCLivingCityAdminTelemetry.Record(authorized && success, error, fields);
        RaiseNetworkEvent(new NCLivingCityAdminInspectResult(
            request.RequestId, request.Kind, authorized, success, fields, error), args.SenderSession);
    }

    private sealed record InspectionResult(
        bool Success,
        NCLivingCityAdminField[] Fields,
        string? Error);

    private sealed class FieldBuilder
    {
        private readonly List<NCLivingCityAdminField> _fields = [];

        public void Add(string section, string key, object? value)
        {
            if (_fields.Count >= MaximumFields)
                return;
            _fields.Add(new NCLivingCityAdminField(
                Bound(section, MaximumSectionLength),
                Bound(key, MaximumKeyLength),
                Bound(value?.ToString() ?? "none", MaximumValueLength)));
        }

        public NCLivingCityAdminField[] Build() => _fields.ToArray();

        private static string Bound(string value, int maximum)
        {
            var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length <= maximum ? normalized : normalized[..maximum];
        }
    }
}
