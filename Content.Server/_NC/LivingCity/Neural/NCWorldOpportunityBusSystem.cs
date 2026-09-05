// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Affordances;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Jobs;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Neural;

public enum NCWorldOpportunityPublishStatus : byte
{
    Added,
    Updated,
    Duplicate,
    Resolved,
    CapacityRejected,
    Invalid,
    Inactive,
}

/// <summary>
/// One actor-scoped, round-local reason GPT may choose to act. This record is context only and carries no mutation
/// authority. EntityUid is deliberately absent.
/// </summary>
public sealed record NCWorldOpportunity(
    NCWorldOpportunityId OpportunityId,
    NCActorId ActorId,
    string Kind,
    NCWorldOpportunityTargetKind TargetKind,
    string TargetHandle,
    IReadOnlyList<string> AffordanceKeys,
    IReadOnlyList<string> RequiredCapabilityIds,
    int Reward,
    int Urgency,
    int Risk,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime ExpiresAt,
    string SourceKey,
    Guid CorrelationId,
    string AssignmentKey = "",
    IReadOnlyList<string>? CompletionCapabilityIds = null);

public readonly record struct NCWorldOpportunityLimits(
    int MaximumTotal,
    int MaximumPerActor,
    int MaximumPerQuery,
    int MaximumAffordanceKeys,
    int RewardWeight,
    int UrgencyWeight,
    int RiskWeight);

/// <summary>Validated, allocation-free runtime form of one data-authored observed-event rule.</summary>
internal sealed record NCObservedEventOpportunityRule(
    string StableKey,
    ProtoId<JobPrototype> Job,
    NCCityLocalPerceivedEventKind ObservedEvent,
    string OpportunityKind,
    IReadOnlyList<string> AffordanceKeys,
    IReadOnlyList<string> RequiredCapabilityIds,
    IReadOnlyList<string> CompletionCapabilityIds,
    int Reward,
    int Urgency,
    int Risk,
    TimeSpan Lifetime);

/// <summary>Validated runtime form of one data-authored station-work rule.</summary>
internal sealed record NCStationWorkOpportunityRule(
    string StableKey,
    NCStationWorkOpportunitySource Source,
    string OpportunityKind,
    IReadOnlyList<string> AffordanceKeys,
    IReadOnlyList<string> RequiredCapabilityIds,
    IReadOnlyList<string> CompletionCapabilityIds,
    int Reward,
    int Urgency,
    int Risk,
    TimeSpan Lifetime);

/// <summary>Verified domain executors may retire a correlated opportunity after their own authority commits.</summary>
public sealed class NCWorldOpportunityResolvedEvent(
    NCActorId actorId,
    NCWorldOpportunityId opportunityId,
    Guid correlationId,
    NCActiveWorldTime completedAt) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public NCWorldOpportunityId OpportunityId { get; } = opportunityId;
    public Guid CorrelationId { get; } = correlationId;
    public NCActiveWorldTime CompletedAt { get; } = completedAt;
}

/// <summary>
/// Event-driven registry supplying the single WorldOpportunities packet for each physical GPT actor. Producers only
/// describe verified observations or obligations; GPT remains the sole ordinary intent selector.
/// </summary>
public sealed partial class NCWorldOpportunityBusSystem : EntitySystem
{
    public static readonly ProtoId<NCWorldOpportunityPolicyPrototype> DefaultPolicy =
        new("NCDefaultWorldOpportunities");

    private const string PerceptionSource = "perception";
    private const string AssignmentSource = "assignment";
    private const string AffordanceSource = "affordance";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityLocalPerceptionSystem _perception = default!;
    [Dependency] private NCCityAffordanceCatalogSystem _affordances = default!;
    [Dependency] private NCStationWorkOpportunityProbeSystem _stationWork = default!;

    private readonly NCWorldOpportunityStore _store = new();
    private readonly Dictionary<(Guid RequestId, int CommandIndex), NCWorldOpportunityId> _commandLinks = [];
    private readonly Dictionary<NCCityLocalPerceivedEventKind, NCObservedEventOpportunityRule[]>
        _observedEventRules = [];
    private readonly Dictionary<ProtoId<JobPrototype>, NCStationWorkOpportunityRule[]> _stationWorkRules = [];
    private NCSessionEpoch _epoch;
    private bool _active;

    public int OpportunityCount => _store.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Reset());
        SubscribeLocalEvent<NCCityLocalIncidentObservedEvent>(OnIncidentObserved);
        SubscribeLocalEvent<NCNeuralActionStepEvent>(OnActionStep);
        SubscribeLocalEvent<NCNeuralCommandResultEvent>(OnCommandResult);
        SubscribeLocalEvent<NCWorldOpportunityResolvedEvent>(OnOpportunityResolved);
    }

    /// <summary>
    /// Adapts one already-bounded subjective sensorium into the central bus. It never performs a global entity scan.
    /// </summary>
    public bool RefreshSubjectiveSources(
        NCActorId actorId,
        IReadOnlyList<NCCityLocalObservation> observations,
        IReadOnlyList<NCCityLocalPerceivedEvent> perceivedEvents,
        IReadOnlyList<NCNeuralKnownLocation> knownLocations,
        ProtoId<JobPrototype>? actorJob,
        string assignmentKey,
        NCActiveWorldTime now)
    {
        if (!TryGetPolicy(out var policy, out var limits) || !Current(now) || !actorId.IsValid)
            return false;

        var visibleHandles = observations.Select(value => value.SubjectHandle)
            .Where(Stable)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var perceivedEvent in perceivedEvents)
        {
            PublishPerceivedEvent(actorId, perceivedEvent, policy, limits, now);
            PublishObservedEventRules(actorId, actorJob, visibleHandles, perceivedEvent, limits, now);
        }
        PublishStationWorkRules(actorId, actorJob, observations, limits, now);

        foreach (var location in knownLocations)
        {
            if (location.Kind != "assigned-workplace" || !Stable(location.Handle) || !Stable(assignmentKey))
                continue;
            var correlation = NCPersistentIdentity.Correlation(
                "world-opportunity",
                AssignmentSource,
                $"{actorId}:{assignmentKey}");
            var opportunity = Create(
                actorId,
                "task.report-to-assigned-workplace",
                NCWorldOpportunityTargetKind.Location,
                location.Handle,
                ["task.report-to-assigned-workplace"],
                policy.JobObligationCapabilities.Select(value => value.Id).ToArray(),
                policy.JobObligationReward,
                policy.JobObligationUrgency,
                policy.JobObligationRisk,
                now,
                now.Advance(policy.JobObligationLifetime),
                AssignmentSource,
                correlation,
                assignmentKey);
            _store.TryPublish(opportunity, now, limits, out _);

            var activityCorrelation = NCPersistentIdentity.Correlation(
                "world-opportunity",
                AssignmentSource,
                $"{actorId}:{assignmentKey}:activity");
            var activity = Create(
                actorId,
                "task.inspect-assigned-workplace",
                NCWorldOpportunityTargetKind.Location,
                location.Handle,
                ["task.inspect-assigned-workplace"],
                policy.JobActivityCapabilities.Select(value => value.Id).ToArray(),
                policy.JobObligationReward,
                policy.JobObligationUrgency,
                policy.JobObligationRisk,
                now,
                now.Advance(policy.JobObligationLifetime),
                AssignmentSource,
                activityCorrelation,
                assignmentKey);
            _store.TryPublish(activity, now, limits, out _);
        }

        // Affordance discovery operates only on the perception system's bounded list of already-visible providers.
        var affordanceBuffer = new List<NCCityAffordanceInfo>();
        foreach (var observation in observations)
        {
            if (!Exists(observation.EntityUid) || !Stable(observation.SubjectHandle))
                continue;
            _affordances.GetForProvider(observation.EntityUid, affordanceBuffer);
            var keys = affordanceBuffer
                .Where(value => value.PhysicalProvider && Stable(value.KnowledgeKey))
                .Select(value => value.KnowledgeKey)
                .Distinct(StringComparer.Ordinal)
                .Take(policy.MaximumAffordanceKeys)
                .ToArray();
            if (keys.Length == 0)
                continue;
            var correlation = NCPersistentIdentity.Correlation(
                "world-opportunity",
                AffordanceSource,
                $"{actorId}:{observation.SubjectHandle}");
            var opportunity = Create(
                actorId,
                "nearby-actionable-affordance",
                NCWorldOpportunityTargetKind.Subject,
                observation.SubjectHandle,
                keys,
                [],
                policy.NearbyAffordanceReward,
                policy.NearbyAffordanceUrgency,
                policy.NearbyAffordanceRisk,
                now,
                now.Advance(policy.NearbyAffordanceLifetime),
                AffordanceSource,
                correlation);
            _store.TryPublish(opportunity, now, limits, out _);
        }
        return true;
    }

    /// <summary>Returns only opportunities visible and eligible for this actor's current request.</summary>
    public NCNeuralWorldOpportunity[] Query(
        NCActorId actorId,
        IReadOnlySet<string> visibleSubjectHandles,
        IReadOnlySet<string> knownLocationHandles,
        IReadOnlySet<string> allowedCapabilityIds,
        IReadOnlySet<string> assignmentKeys,
        NCActiveWorldTime now,
        int maximum)
    {
        if (!TryGetPolicy(out _, out var limits) || !Current(now))
            return [];
        return _store.Query(actorId, visibleSubjectHandles, knownLocationHandles, allowedCapabilityIds,
            assignmentKeys, now, Math.Min(maximum, limits.MaximumPerQuery), limits);
    }

    /// <summary>Publishes a domain-authored opportunity without choosing how the actor will respond.</summary>
    public NCWorldOpportunityPublishStatus TryPublish(NCWorldOpportunity opportunity)
    {
        if (!TryGetPolicy(out _, out var limits) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || !_active || epoch != _epoch)
        {
            return NCWorldOpportunityPublishStatus.Inactive;
        }
        return _store.TryPublish(opportunity, now, limits, out _);
    }

    /// <summary>Retires only an exact actor/id/correlation tuple after an authoritative outcome.</summary>
    public bool TryResolve(
        NCActorId actorId,
        NCWorldOpportunityId opportunityId,
        Guid correlationId,
        NCActiveWorldTime completedAt)
    {
        if (!Current(completedAt) || !TryGetPolicy(out _, out var limits))
            return false;
        return _store.TryResolve(actorId, opportunityId, correlationId, completedAt, limits);
    }

    public static NCWorldOpportunityId CreateStableId(
        NCActorId actorId,
        string sourceKey,
        Guid correlationId,
        string kind) =>
        new(NCPersistentIdentity.Command(
            "world-opportunity",
            sourceKey,
            $"{actorId}:{correlationId:D}:{kind}"));

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        _epoch = args.Epoch;
        _active = true;
        CompileObservedEventRules();
        CompileStationWorkRules();
    }

    private void OnIncidentObserved(NCCityLocalIncidentObservedEvent args)
    {
        if (!_active || !_runtime.TryGetActor(args.Observer, out var actorId) ||
            !TryGetPolicy(out var policy, out var limits) || !Current(args.ObservedAt) ||
            !_perception.TryGetSnapshot(actorId, args.ObservedAt, out var observations, out var events))
        {
            return;
        }
        var actorJob = TryComp<NCNpcJobComponent>(args.Observer, out var job)
            ? job.Job
            : (ProtoId<JobPrototype>?) null;
        var visibleHandles = observations.Select(value => value.SubjectHandle)
            .Where(Stable)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var perceivedEvent in events)
        {
            if (perceivedEvent.EventId != args.EventId)
                continue;
            PublishPerceivedEvent(actorId, perceivedEvent, policy, limits, args.ObservedAt);
            PublishObservedEventRules(actorId, actorJob, visibleHandles, perceivedEvent, limits, args.ObservedAt);
            return;
        }
    }

    private void OnActionStep(NCNeuralActionStepEvent args)
    {
        var commandIndex = args.Command.OriginalIndex;
        if (!_active || commandIndex < 0 || string.IsNullOrWhiteSpace(args.Command.TargetHandle) ||
            _commandLinks.ContainsKey((args.BatchId, commandIndex)) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch ||
            !TryGetPolicy(out _, out var limits) ||
            !_store.TryFindUniqueCommandMatch(args.ActorId, args.Command.TargetHandle, args.Command.CapabilityId,
                now, limits,
                out var opportunityId))
        {
            return;
        }
        // Command-result correlation is request-scoped and bounded by the same policy as the bus.
        if (_commandLinks.Count < limits.MaximumTotal)
            _commandLinks[(args.BatchId, commandIndex)] = opportunityId;
    }

    private void OnCommandResult(NCNeuralCommandResultEvent args)
    {
        var key = (args.Result.RequestId, args.Result.CommandIndex);
        if (!_active || !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch ||
            !_commandLinks.Remove(key, out var opportunityId) ||
            !CanResolveCommandResult(args.Result.State, args.Result.UpdatedAtTicks, now))
            return;
        if (TryGetPolicy(out _, out var limits))
            _store.TryResolve(args.ActorId, opportunityId, null,
                new NCActiveWorldTime(args.Result.UpdatedAtTicks), limits);
    }

    private void OnOpportunityResolved(NCWorldOpportunityResolvedEvent args) =>
        TryResolve(args.ActorId, args.OpportunityId, args.CorrelationId, args.CompletedAt);

    private void PublishPerceivedEvent(
        NCActorId actorId,
        NCCityLocalPerceivedEvent perceivedEvent,
        NCWorldOpportunityPolicyPrototype policy,
        NCWorldOpportunityLimits limits,
        NCActiveWorldTime now)
    {
        if (!Urgent(perceivedEvent.Kind) || perceivedEvent.EventId == Guid.Empty)
            return;
        var targetHandle = ThreatActorTarget(perceivedEvent.Kind)
            ? perceivedEvent.ActorHandle
            : perceivedEvent.TargetHandle;
        if (!Stable(targetHandle))
            return;
        var kind = $"event.{perceivedEvent.Kind.ToString().ToLowerInvariant()}";
        var threat = ThreatActorTarget(perceivedEvent.Kind) || perceivedEvent.Kind == NCCityLocalPerceivedEventKind.Fire;
        var opportunity = Create(
            actorId,
            kind,
            NCWorldOpportunityTargetKind.Subject,
            targetHandle,
            [kind],
            threat ? policy.UrgentEventCapabilities.Select(value => value.Id).ToArray() : [],
            policy.UrgentEventReward,
            policy.UrgentEventUrgency,
            policy.UrgentEventRisk,
            perceivedEvent.ObservedAt,
            perceivedEvent.ExpiresAt < now.Advance(policy.UrgentEventLifetime)
                ? perceivedEvent.ExpiresAt
                : now.Advance(policy.UrgentEventLifetime),
            PerceptionSource,
            perceivedEvent.EventId);
        _store.TryPublish(opportunity, now, limits, out _);
    }

    private void PublishObservedEventRules(
        NCActorId actorId,
        ProtoId<JobPrototype>? actorJob,
        IReadOnlySet<string> visibleHandles,
        NCCityLocalPerceivedEvent perceivedEvent,
        NCWorldOpportunityLimits limits,
        NCActiveWorldTime now)
    {
        if (!_observedEventRules.TryGetValue(perceivedEvent.Kind, out var rules))
            return;
        foreach (var rule in rules)
        {
            if (TryCreateObservedEventOpportunity(actorId, actorJob, visibleHandles, perceivedEvent, rule, now,
                    out var opportunity))
            {
                _store.TryPublish(opportunity, now, limits, out _);
            }
        }
    }

    internal static bool TryCreateObservedEventOpportunity(
        NCActorId actorId,
        ProtoId<JobPrototype>? actorJob,
        IReadOnlySet<string> visibleHandles,
        NCCityLocalPerceivedEvent perceivedEvent,
        NCObservedEventOpportunityRule rule,
        NCActiveWorldTime now,
        out NCWorldOpportunity opportunity)
    {
        opportunity = default!;
        if (!actorId.IsValid || actorJob is not { } job || job != rule.Job ||
            perceivedEvent.Kind != rule.ObservedEvent || perceivedEvent.EventId == Guid.Empty ||
            perceivedEvent.ObservedAt > now || perceivedEvent.ExpiresAt <= now ||
            !Stable(perceivedEvent.TargetHandle) || perceivedEvent.TargetHandle == "self" ||
            !visibleHandles.Contains(perceivedEvent.TargetHandle))
        {
            return false;
        }

        var sourceKey = $"rule.{rule.StableKey}";
        var expiresAt = now.Advance(rule.Lifetime);
        if (perceivedEvent.ExpiresAt < expiresAt)
            expiresAt = perceivedEvent.ExpiresAt;
        opportunity = Create(
            actorId,
            rule.OpportunityKind,
            NCWorldOpportunityTargetKind.Subject,
            perceivedEvent.TargetHandle,
            rule.AffordanceKeys,
            rule.RequiredCapabilityIds,
            rule.Reward,
            rule.Urgency,
            rule.Risk,
            perceivedEvent.ObservedAt,
            expiresAt,
            sourceKey,
            perceivedEvent.EventId,
            $"job:{rule.Job.Id}",
            rule.CompletionCapabilityIds);
        return true;
    }

    private static NCWorldOpportunity Create(
        NCActorId actorId,
        string kind,
        NCWorldOpportunityTargetKind targetKind,
        string targetHandle,
        IReadOnlyList<string> affordanceKeys,
        IReadOnlyList<string> requiredCapabilityIds,
        int reward,
        int urgency,
        int risk,
        NCActiveWorldTime createdAt,
        NCActiveWorldTime expiresAt,
        string sourceKey,
        Guid correlationId,
        string assignmentKey = "",
        IReadOnlyList<string>? completionCapabilityIds = null) =>
        new(
            CreateStableId(actorId, sourceKey, correlationId, kind),
            actorId,
            kind,
            targetKind,
            targetHandle,
            affordanceKeys,
            requiredCapabilityIds,
            reward,
            urgency,
            risk,
            createdAt,
            expiresAt,
            sourceKey,
            correlationId,
            assignmentKey,
            completionCapabilityIds);

    internal static bool CanResolveCommandResult(string state, long updatedAtTicks, NCActiveWorldTime now) =>
        state == "completed" && now.IsValid && updatedAtTicks >= 0 && updatedAtTicks <= now.Ticks;

    private void CompileObservedEventRules()
    {
        _observedEventRules.Clear();
        if (!TryGetPolicy(out var policy, out _))
            return;

        var compiled = new Dictionary<NCCityLocalPerceivedEventKind, List<NCObservedEventOpportunityRule>>();
        foreach (var prototype in _prototypes.EnumeratePrototypes<NCObservedEventOpportunityRulePrototype>()
                     .OrderBy(value => value.ID, StringComparer.Ordinal))
        {
            if (!TryCompileObservedEventRule(prototype, policy, out var rule))
            {
                Log.Error($"Invalid observed-event opportunity rule: {prototype.ID}");
                continue;
            }

            if (!compiled.TryGetValue(rule.ObservedEvent, out var rules))
            {
                rules = [];
                compiled.Add(rule.ObservedEvent, rules);
            }
            rules.Add(rule);
        }

        foreach (var (kind, rules) in compiled)
            _observedEventRules.Add(kind, rules.ToArray());
    }

    private void CompileStationWorkRules()
    {
        _stationWorkRules.Clear();
        if (!TryGetPolicy(out var policy, out _))
            return;

        var compiled = new Dictionary<ProtoId<JobPrototype>, List<NCStationWorkOpportunityRule>>();
        foreach (var prototype in _prototypes.EnumeratePrototypes<NCStationWorkOpportunityRulePrototype>()
                     .OrderBy(value => value.ID, StringComparer.Ordinal))
        {
            if (!TryCompileStationWorkRule(prototype, policy, out var rule))
            {
                Log.Error($"Invalid station-work opportunity rule: {prototype.ID}");
                continue;
            }

            foreach (var job in prototype.Jobs)
            {
                if (!compiled.TryGetValue(job, out var rules))
                    compiled[job] = rules = [];
                rules.Add(rule);
            }
        }

        foreach (var (job, rules) in compiled)
            _stationWorkRules.Add(job, rules.ToArray());
    }

    private bool TryCompileStationWorkRule(
        NCStationWorkOpportunityRulePrototype prototype,
        NCWorldOpportunityPolicyPrototype policy,
        out NCStationWorkOpportunityRule rule)
    {
        rule = default!;
        var required = prototype.RequiredCapabilities.Select(value => value.Id).ToArray();
        var completion = prototype.CompletionCapabilities.Select(value => value.Id).ToArray();
        if (!Stable(prototype.StableKey) || prototype.StableKey.Length > 96 ||
            !Enum.IsDefined(prototype.Source) || !Stable(prototype.OpportunityKind) ||
            prototype.Jobs.Count == 0 || prototype.Jobs.Distinct().Count() != prototype.Jobs.Count ||
            prototype.Jobs.Any(job => !_prototypes.HasIndex(job)) ||
            prototype.AffordanceKeys.Count is <= 0 ||
            prototype.AffordanceKeys.Count > policy.MaximumAffordanceKeys ||
            prototype.AffordanceKeys.Distinct(StringComparer.Ordinal).Count() != prototype.AffordanceKeys.Count ||
            prototype.AffordanceKeys.Any(value => !Stable(value)) || required.Length is <= 0 or > 32 ||
            required.Distinct(StringComparer.Ordinal).Count() != required.Length ||
            completion.Length is <= 0 or > 32 ||
            completion.Distinct(StringComparer.Ordinal).Count() != completion.Length ||
            required.Any(value => !Stable(value) ||
                                  !_prototypes.HasIndex(new ProtoId<NCCognitionCapabilityPrototype>(value))) ||
            completion.Any(value => !required.Contains(value, StringComparer.Ordinal)) ||
            !ScoreValue(prototype.Reward) || !ScoreValue(prototype.Urgency) || !ScoreValue(prototype.Risk) ||
            prototype.Lifetime <= TimeSpan.Zero)
        {
            return false;
        }

        rule = new NCStationWorkOpportunityRule(
            prototype.StableKey,
            prototype.Source,
            prototype.OpportunityKind,
            prototype.AffordanceKeys.ToArray(),
            required,
            completion,
            prototype.Reward,
            prototype.Urgency,
            prototype.Risk,
            prototype.Lifetime);
        return true;
    }

    private void PublishStationWorkRules(
        NCActorId actorId,
        ProtoId<JobPrototype>? actorJob,
        IReadOnlyList<NCCityLocalObservation> observations,
        NCWorldOpportunityLimits limits,
        NCActiveWorldTime now)
    {
        if (actorJob is not { } job || !_stationWorkRules.TryGetValue(job, out var rules))
            return;

        foreach (var observation in observations)
        {
            if (!Exists(observation.EntityUid) || !Stable(observation.SubjectHandle))
                continue;
            foreach (var rule in rules)
            {
                if (!_stationWork.IsAvailable(rule.Source, observation.EntityUid))
                    continue;
                var sourceKey = $"job-work.{rule.StableKey}";
                var correlation = NCPersistentIdentity.Correlation(
                    "world-opportunity",
                    sourceKey,
                    $"{actorId}:{observation.SubjectHandle}");
                var opportunity = Create(
                    actorId,
                    rule.OpportunityKind,
                    NCWorldOpportunityTargetKind.Subject,
                    observation.SubjectHandle,
                    rule.AffordanceKeys,
                    rule.RequiredCapabilityIds,
                    rule.Reward,
                    rule.Urgency,
                    rule.Risk,
                    now,
                    now.Advance(rule.Lifetime),
                    sourceKey,
                    correlation,
                    $"job:{job.Id}",
                    rule.CompletionCapabilityIds);
                _store.TryPublish(opportunity, now, limits, out _);
            }
        }
    }

    private bool TryCompileObservedEventRule(
        NCObservedEventOpportunityRulePrototype prototype,
        NCWorldOpportunityPolicyPrototype policy,
        out NCObservedEventOpportunityRule rule)
    {
        rule = default!;
        var required = prototype.RequiredCapabilities.Select(value => value.Id).ToArray();
        var completion = prototype.CompletionCapabilities.Select(value => value.Id).ToArray();
        if (!Stable(prototype.StableKey) || prototype.StableKey.Length > 96 ||
            !Enum.TryParse(prototype.ObservedEvent, false, out NCCityLocalPerceivedEventKind observedEvent) ||
            !Stable(prototype.OpportunityKind) || !_prototypes.HasIndex(prototype.Job) ||
            prototype.AffordanceKeys.Count is <= 0 ||
            prototype.AffordanceKeys.Count > policy.MaximumAffordanceKeys ||
            prototype.AffordanceKeys.Distinct(StringComparer.Ordinal).Count() != prototype.AffordanceKeys.Count ||
            prototype.AffordanceKeys.Any(value => !Stable(value)) || required.Length is <= 0 or > 32 ||
            required.Distinct(StringComparer.Ordinal).Count() != required.Length ||
            completion.Length is <= 0 or > 32 ||
            completion.Distinct(StringComparer.Ordinal).Count() != completion.Length ||
            required.Any(value => !Stable(value) ||
                                  !_prototypes.HasIndex(new ProtoId<NCCognitionCapabilityPrototype>(value))) ||
            completion.Any(value => !required.Contains(value, StringComparer.Ordinal)) ||
            !ScoreValue(prototype.Reward) || !ScoreValue(prototype.Urgency) || !ScoreValue(prototype.Risk) ||
            prototype.Lifetime <= TimeSpan.Zero)
        {
            return false;
        }

        rule = new NCObservedEventOpportunityRule(
            prototype.StableKey,
            prototype.Job,
            observedEvent,
            prototype.OpportunityKind,
            prototype.AffordanceKeys.ToArray(),
            required,
            completion,
            prototype.Reward,
            prototype.Urgency,
            prototype.Risk,
            prototype.Lifetime);
        return true;
    }

    private bool TryGetPolicy(
        out NCWorldOpportunityPolicyPrototype policy,
        out NCWorldOpportunityLimits limits)
    {
        limits = default;
        if (!_prototypes.TryIndex(DefaultPolicy, out policy!) || !Valid(policy))
            return false;
        limits = new NCWorldOpportunityLimits(
            policy.MaximumTotal,
            policy.MaximumPerActor,
            policy.MaximumPerQuery,
            policy.MaximumAffordanceKeys,
            policy.RewardWeight,
            policy.UrgencyWeight,
            policy.RiskWeight);
        return true;
    }

    private bool Current(NCActiveWorldTime now) =>
        now.IsValid && _active && _clock.TryGetCurrentTime(out var epoch, out var current) && epoch == _epoch &&
        now <= current;

    private static bool Valid(NCWorldOpportunityPolicyPrototype policy) =>
        policy.MaximumTotal is > 0 and <= 65536 &&
        policy.MaximumPerActor is > 0 and <= 256 &&
        policy.MaximumPerQuery is > 0 and <= 128 &&
        policy.MaximumPerQuery <= policy.MaximumPerActor &&
        policy.MaximumAffordanceKeys is > 0 and <= 32 &&
        policy.RewardWeight is >= 0 and <= 100 &&
        policy.UrgencyWeight is >= 0 and <= 100 &&
        policy.RiskWeight is >= 0 and <= 100 &&
        ScoreValue(policy.UrgentEventReward) && ScoreValue(policy.UrgentEventUrgency) &&
        ScoreValue(policy.UrgentEventRisk) && ScoreValue(policy.JobObligationReward) &&
        ScoreValue(policy.JobObligationUrgency) && ScoreValue(policy.JobObligationRisk) &&
        ScoreValue(policy.NearbyAffordanceReward) && ScoreValue(policy.NearbyAffordanceUrgency) &&
        ScoreValue(policy.NearbyAffordanceRisk) && policy.UrgentEventLifetime > TimeSpan.Zero &&
        policy.JobObligationLifetime > TimeSpan.Zero && policy.NearbyAffordanceLifetime > TimeSpan.Zero &&
        policy.UrgentEventCapabilities.Count <= 16 && policy.JobObligationCapabilities.Count <= 16 &&
        policy.JobActivityCapabilities.Count <= 16;

    private static bool ScoreValue(int value) => value is >= 0 and <= 1000;

    private static bool Urgent(NCCityLocalPerceivedEventKind kind) => kind is
        NCCityLocalPerceivedEventKind.Assault or
        NCCityLocalPerceivedEventKind.Killing or
        NCCityLocalPerceivedEventKind.Gunshot or
        NCCityLocalPerceivedEventKind.WeaponDisplayed or
        NCCityLocalPerceivedEventKind.HeldItemDisplayed or
        NCCityLocalPerceivedEventKind.SevereInjury or
        NCCityLocalPerceivedEventKind.BodyDiscovered or
        NCCityLocalPerceivedEventKind.Fire or
        NCCityLocalPerceivedEventKind.VerbalThreat;

    private static bool ThreatActorTarget(NCCityLocalPerceivedEventKind kind) => kind is
        NCCityLocalPerceivedEventKind.Assault or
        NCCityLocalPerceivedEventKind.Killing or
        NCCityLocalPerceivedEventKind.Gunshot or
        NCCityLocalPerceivedEventKind.WeaponDisplayed or
        NCCityLocalPerceivedEventKind.HeldItemDisplayed or
        NCCityLocalPerceivedEventKind.VerbalThreat;

    internal static bool Stable(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private void Reset()
    {
        _active = false;
        _epoch = default;
        _store.Clear();
        _commandLinks.Clear();
        _observedEventRules.Clear();
        _stationWorkRules.Clear();
    }
}

/// <summary>Pure bounded store kept separate so ranking, expiry, deduplication and isolation are unit-testable.</summary>
internal sealed class NCWorldOpportunityStore
{
    private const int MaximumCapabilities = 32;

    private readonly Dictionary<NCActorId, Dictionary<NCWorldOpportunityId, NCWorldOpportunity>> _byActor = [];
    private readonly Dictionary<NCWorldOpportunityId, NCActiveWorldTime> _resolved = [];

    public int Count { get; private set; }

    public NCWorldOpportunityPublishStatus TryPublish(
        NCWorldOpportunity opportunity,
        NCActiveWorldTime now,
        NCWorldOpportunityLimits limits,
        out NCWorldOpportunity? stored)
    {
        stored = null;
        if (!Valid(opportunity, now, limits))
            return NCWorldOpportunityPublishStatus.Invalid;
        CleanupActor(opportunity.ActorId, now);
        if (_resolved.TryGetValue(opportunity.OpportunityId, out var resolvedUntil))
        {
            if (resolvedUntil > now)
                return NCWorldOpportunityPublishStatus.Resolved;
            _resolved.Remove(opportunity.OpportunityId);
        }
        _byActor.TryGetValue(opportunity.ActorId, out var actorEntries);
        actorEntries ??= [];
        if (actorEntries.TryGetValue(opportunity.OpportunityId, out var existing))
        {
            if (!SameIdentity(existing, opportunity))
                return NCWorldOpportunityPublishStatus.Invalid;
            var merged = Merge(existing, opportunity, limits.MaximumAffordanceKeys);
            if (Equivalent(existing, merged))
            {
                stored = existing;
                return NCWorldOpportunityPublishStatus.Duplicate;
            }
            actorEntries[opportunity.OpportunityId] = merged;
            stored = merged;
            return NCWorldOpportunityPublishStatus.Updated;
        }

        if (Count >= limits.MaximumTotal)
        {
            CleanupAll(now);
            if (Count >= limits.MaximumTotal && !TryEvictLower(opportunity, limits))
                return NCWorldOpportunityPublishStatus.CapacityRejected;
        }
        if (!_byActor.ContainsKey(opportunity.ActorId))
            _byActor.Add(opportunity.ActorId, actorEntries);
        if (actorEntries.Count >= limits.MaximumPerActor && !TryEvictLower(actorEntries, opportunity, limits))
            return NCWorldOpportunityPublishStatus.CapacityRejected;
        actorEntries[opportunity.OpportunityId] = opportunity;
        Count++;
        stored = opportunity;
        return NCWorldOpportunityPublishStatus.Added;
    }

    public NCNeuralWorldOpportunity[] Query(
        NCActorId actorId,
        IReadOnlySet<string> visibleSubjectHandles,
        IReadOnlySet<string> knownLocationHandles,
        IReadOnlySet<string> allowedCapabilityIds,
        IReadOnlySet<string> assignmentKeys,
        NCActiveWorldTime now,
        int maximum,
        NCWorldOpportunityLimits limits)
    {
        if (!actorId.IsValid || !now.IsValid || maximum <= 0)
            return [];
        CleanupActor(actorId, now);
        if (!_byActor.TryGetValue(actorId, out var entries))
            return [];
        return entries.Values
            .Where(value => Visible(value, visibleSubjectHandles, knownLocationHandles) &&
                            Eligible(value, allowedCapabilityIds, assignmentKeys))
            .OrderByDescending(value => Score(value, limits))
            .ThenByDescending(value => value.Reward)
            .ThenByDescending(value => value.Urgency)
            .ThenBy(value => value.Risk)
            .ThenBy(value => value.CreatedAt.Ticks)
            .ThenBy(value => value.OpportunityId.Value)
            .Take(Math.Min(maximum, limits.MaximumPerQuery))
            .Select(Project)
            .ToArray();
    }

    public bool TryFindUniqueCommandMatch(
        NCActorId actorId,
        string targetHandle,
        string capabilityId,
        NCActiveWorldTime now,
        NCWorldOpportunityLimits limits,
        out NCWorldOpportunityId opportunityId)
    {
        opportunityId = default;
        CleanupActor(actorId, now);
        if (!_byActor.TryGetValue(actorId, out var entries))
            return false;
        var matches = entries.Values
            .Where(value => value.TargetHandle == targetHandle &&
                            CompletionCapabilities(value).Contains(capabilityId, StringComparer.Ordinal))
            .OrderByDescending(value => Score(value, limits))
            .ThenBy(value => value.OpportunityId.Value)
            .Take(2)
            .ToArray();
        // Without an explicit OpportunityId in the command, ambiguity must keep every opportunity alive.
        if (matches.Length != 1)
            return false;
        opportunityId = matches[0].OpportunityId;
        return true;
    }

    public bool TryResolve(
        NCActorId actorId,
        NCWorldOpportunityId opportunityId,
        Guid? correlationId,
        NCActiveWorldTime completedAt,
        NCWorldOpportunityLimits limits)
    {
        CleanupActor(actorId, completedAt);
        if (!_byActor.TryGetValue(actorId, out var entries) ||
            !entries.TryGetValue(opportunityId, out var opportunity) ||
            correlationId is { } correlation && opportunity.CorrelationId != correlation)
        {
            return false;
        }
        entries.Remove(opportunityId);
        Count--;
        if (entries.Count == 0)
            _byActor.Remove(actorId);
        if (opportunity.ExpiresAt > completedAt)
        {
            TrimResolved(limits.MaximumTotal - 1);
            _resolved[opportunityId] = opportunity.ExpiresAt;
        }
        return true;
    }

    public void Clear()
    {
        _byActor.Clear();
        _resolved.Clear();
        Count = 0;
    }

    internal static long Score(NCWorldOpportunity opportunity, NCWorldOpportunityLimits limits) =>
        (long) opportunity.Reward * limits.RewardWeight +
        (long) opportunity.Urgency * limits.UrgencyWeight -
        (long) opportunity.Risk * limits.RiskWeight;

    private static NCNeuralWorldOpportunity Project(NCWorldOpportunity value) =>
        new(value.TargetHandle, value.AffordanceKeys, true, string.Empty)
        {
            OpportunityId = value.OpportunityId.ToString(),
            Kind = value.Kind,
            TargetKind = value.TargetKind == NCWorldOpportunityTargetKind.Subject ? "subject" : "location",
            RequiredCapabilityIds = value.RequiredCapabilityIds,
            Reward = value.Reward,
            Urgency = value.Urgency,
            Risk = value.Risk,
            CreatedAtTicks = value.CreatedAt.Ticks,
            ExpiresAtTicks = value.ExpiresAt.Ticks,
            SourceKey = value.SourceKey,
            CorrelationId = value.CorrelationId.ToString("D"),
        };

    private static bool Visible(
        NCWorldOpportunity opportunity,
        IReadOnlySet<string> visibleSubjectHandles,
        IReadOnlySet<string> knownLocationHandles) =>
        opportunity.TargetKind switch
        {
            NCWorldOpportunityTargetKind.Subject => visibleSubjectHandles.Contains(opportunity.TargetHandle),
            NCWorldOpportunityTargetKind.Location => knownLocationHandles.Contains(opportunity.TargetHandle),
            _ => false,
        };

    private static bool Eligible(
        NCWorldOpportunity opportunity,
        IReadOnlySet<string> allowedCapabilityIds,
        IReadOnlySet<string> assignmentKeys) =>
        (opportunity.AssignmentKey.Length == 0 || assignmentKeys.Contains(opportunity.AssignmentKey)) &&
        opportunity.RequiredCapabilityIds.All(allowedCapabilityIds.Contains);

    private bool TryEvictLower(NCWorldOpportunity incoming, NCWorldOpportunityLimits limits)
    {
        NCActorId selectedActor = default;
        NCWorldOpportunity? selected = null;
        foreach (var actorEntries in _byActor)
        foreach (var candidate in actorEntries.Value.Values)
        {
            if (selected == null || Score(candidate, limits) < Score(selected, limits))
            {
                selectedActor = actorEntries.Key;
                selected = candidate;
            }
        }
        if (selected == null || Score(selected, limits) >= Score(incoming, limits))
            return false;
        Remove(selectedActor, selected.OpportunityId);
        return true;
    }

    private bool TryEvictLower(
        Dictionary<NCWorldOpportunityId, NCWorldOpportunity> entries,
        NCWorldOpportunity incoming,
        NCWorldOpportunityLimits limits)
    {
        var selected = entries.Values
            .OrderBy(value => Score(value, limits))
            .ThenBy(value => value.OpportunityId.Value)
            .First();
        if (Score(selected, limits) >= Score(incoming, limits))
            return false;
        entries.Remove(selected.OpportunityId);
        Count--;
        return true;
    }

    private void CleanupActor(NCActorId actorId, NCActiveWorldTime now)
    {
        if (_byActor.TryGetValue(actorId, out var entries))
        {
            foreach (var expired in entries.Values.Where(value => value.ExpiresAt <= now)
                         .Select(value => value.OpportunityId).ToArray())
            {
                entries.Remove(expired);
                Count--;
            }
            if (entries.Count == 0)
                _byActor.Remove(actorId);
        }
        foreach (var expired in _resolved.Where(value => value.Value <= now).Select(value => value.Key).ToArray())
            _resolved.Remove(expired);
    }

    private void CleanupAll(NCActiveWorldTime now)
    {
        foreach (var actorId in _byActor.Keys.ToArray())
            CleanupActor(actorId, now);
    }

    private void TrimResolved(int maximum)
    {
        while (_resolved.Count > Math.Max(0, maximum))
        {
            var oldest = _resolved.MinBy(value => value.Value.Ticks);
            _resolved.Remove(oldest.Key);
        }
    }

    private void Remove(NCActorId actorId, NCWorldOpportunityId opportunityId)
    {
        if (!_byActor.TryGetValue(actorId, out var entries) || !entries.Remove(opportunityId))
            return;
        Count--;
        if (entries.Count == 0)
            _byActor.Remove(actorId);
    }

    private static bool SameIdentity(NCWorldOpportunity left, NCWorldOpportunity right) =>
        left.OpportunityId == right.OpportunityId && left.ActorId == right.ActorId && left.Kind == right.Kind &&
        left.TargetKind == right.TargetKind && left.SourceKey == right.SourceKey &&
        left.CorrelationId == right.CorrelationId && left.AssignmentKey == right.AssignmentKey &&
        CompletionCapabilities(left).SequenceEqual(CompletionCapabilities(right));

    private static bool Equivalent(NCWorldOpportunity left, NCWorldOpportunity right) =>
        left.OpportunityId == right.OpportunityId && left.ActorId == right.ActorId && left.Kind == right.Kind &&
        left.TargetKind == right.TargetKind && left.TargetHandle == right.TargetHandle &&
        left.AffordanceKeys.SequenceEqual(right.AffordanceKeys) &&
        left.RequiredCapabilityIds.SequenceEqual(right.RequiredCapabilityIds) &&
        CompletionCapabilities(left).SequenceEqual(CompletionCapabilities(right)) &&
        left.Reward == right.Reward && left.Urgency == right.Urgency && left.Risk == right.Risk &&
        left.CreatedAt == right.CreatedAt && left.ExpiresAt == right.ExpiresAt &&
        left.SourceKey == right.SourceKey && left.CorrelationId == right.CorrelationId &&
        left.AssignmentKey == right.AssignmentKey;

    private static NCWorldOpportunity Merge(
        NCWorldOpportunity current,
        NCWorldOpportunity incoming,
        int maximumAffordances)
    {
        var affordances = current.AffordanceKeys.Concat(incoming.AffordanceKeys)
            .Distinct(StringComparer.Ordinal)
            .Take(maximumAffordances)
            .ToArray();
        var capabilities = current.RequiredCapabilityIds.Concat(incoming.RequiredCapabilityIds)
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumCapabilities)
            .ToArray();
        return current with
        {
            TargetHandle = incoming.TargetHandle,
            AffordanceKeys = affordances,
            RequiredCapabilityIds = capabilities,
            Reward = Math.Max(current.Reward, incoming.Reward),
            Urgency = Math.Max(current.Urgency, incoming.Urgency),
            Risk = Math.Max(current.Risk, incoming.Risk),
            CreatedAt = current.CreatedAt < incoming.CreatedAt ? current.CreatedAt : incoming.CreatedAt,
            ExpiresAt = current.ExpiresAt > incoming.ExpiresAt ? current.ExpiresAt : incoming.ExpiresAt,
        };
    }

    private static bool Valid(
        NCWorldOpportunity value,
        NCActiveWorldTime now,
        NCWorldOpportunityLimits limits) =>
        value.OpportunityId.IsValid && value.ActorId.IsValid && value.CorrelationId != Guid.Empty &&
        NCWorldOpportunityBusSystem.Stable(value.Kind) && NCWorldOpportunityBusSystem.Stable(value.TargetHandle) &&
        NCWorldOpportunityBusSystem.Stable(value.SourceKey) &&
        (value.AssignmentKey.Length == 0 || NCWorldOpportunityBusSystem.Stable(value.AssignmentKey)) &&
        value.TargetKind is NCWorldOpportunityTargetKind.Subject or NCWorldOpportunityTargetKind.Location &&
        value.AffordanceKeys.Count is > 0 && value.AffordanceKeys.Count <= limits.MaximumAffordanceKeys &&
        value.AffordanceKeys.Distinct(StringComparer.Ordinal).Count() == value.AffordanceKeys.Count &&
        value.AffordanceKeys.All(NCWorldOpportunityBusSystem.Stable) &&
        value.RequiredCapabilityIds.Count <= MaximumCapabilities &&
        value.RequiredCapabilityIds.Distinct(StringComparer.Ordinal).Count() == value.RequiredCapabilityIds.Count &&
        value.RequiredCapabilityIds.All(NCWorldOpportunityBusSystem.Stable) &&
        CompletionCapabilities(value).Count <= MaximumCapabilities &&
        CompletionCapabilities(value).Distinct(StringComparer.Ordinal).Count() == CompletionCapabilities(value).Count &&
        CompletionCapabilities(value).All(capability =>
            NCWorldOpportunityBusSystem.Stable(capability) &&
            value.RequiredCapabilityIds.Contains(capability, StringComparer.Ordinal)) &&
        value.Reward is >= 0 and <= 1000 && value.Urgency is >= 0 and <= 1000 &&
        value.Risk is >= 0 and <= 1000 && value.CreatedAt.IsValid && value.CreatedAt <= now &&
        value.ExpiresAt > now && value.ExpiresAt > value.CreatedAt;

    private static IReadOnlyList<string> CompletionCapabilities(NCWorldOpportunity opportunity) =>
        opportunity.CompletionCapabilityIds ?? opportunity.RequiredCapabilityIds;
}
