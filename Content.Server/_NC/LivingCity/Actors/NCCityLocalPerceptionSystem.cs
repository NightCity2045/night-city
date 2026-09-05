// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.Combat.Damage;
using Content.Server._NC.LivingCity.Simulation;
using Content.Server._NC.LivingCity.Time;
using Content.Server.Medical.Components;
using Content.Server.Construction.Components;
using Content.Server.Botany.Components;
using Content.Server.Chemistry.Components;
using Content.Server.Kitchen.Components;
using Content.Shared.Cuffs;
using Content.Shared.Atmos.Components;
using Content.Shared.Bed.Components;
using Content.Shared.Buckle.Components;
using Content.Shared.Interaction;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Doors.Components;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Content.Shared.Fluids.Components;
using Content.Shared.Storage;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.VendingMachines;
using Content.Shared._NC.LivingCity.Affordances;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Persistence.Time;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

public readonly record struct NCCityLocalPerceptionCounters(
    bool Active,
    int TrackedActors,
    int ScheduledUpdates,
    long Evaluations,
    long Observations,
    long RejectedConfigurations,
    string? ConfigurationError);

/// <summary>
/// Evaluates only configured materialized actors through EntityLookup spatial queries and a bounded due-time queue.
/// </summary>
public sealed partial class NCCityLocalPerceptionSystem : EntitySystem
{
    private const string DefaultPolicyId = "NCDefaultLocalPerceptionPolicy";
    private const string UnidentifiedSourceHandlePrefix = "unidentified-source:";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCAILoadGovernorSystem _governor = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private SharedCuffableSystem _cuffable = default!;
    [Dependency] private NpcFactionSystem _factions = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;

    private readonly PriorityQueue<ScheduledEvaluation, EvaluationPriority> _queue = new();
    private readonly Dictionary<EntityUid, NCActiveWorldTime> _scheduled = [];
    private readonly HashSet<EntityUid> _tracked = [];
    private readonly HashSet<EntityUid> _observedBuffer = [];
    private readonly List<NCCityLocalObservation> _retainedObservations = [];
    private readonly List<NCCityLocalObservation> _previousVisibleObservations = [];
    private readonly List<NCCityLocalObservation> _currentVisibleObservations = [];
    private readonly List<NCCityLocalHeldItemObservation> _previousVisibleHeldItems = [];
    private readonly List<NCCityLocalPerceptionDelta> _perceptionDeltaBuffer = [];
    private readonly Dictionary<EntityUid, string> _previousHandles = [];
    private readonly Dictionary<(EntityUid Actor, EntityUid Target), IncidentCorrelation> _incidentCorrelations = [];
    private readonly PriorityQueue<IncidentCorrelationEntry, long> _incidentCorrelationExpiry = new();
    private NCCityLocalPerceptionPolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private long _sequence;
    private long _evaluations;
    private long _observations;
    private long _rejectedConfigurations;
    private bool _active;
    private float _maximumObservationRadius;
    private TimeSpan _maximumWitnessedEventLifetime;
    private string? _configurationError;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
        SubscribeLocalEvent<NCCityLocalPerceptionComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCPhysicalActorSpawnedEvent>(OnPhysicalActorSpawned);
        SubscribeLocalEvent<NCCityActorBindingChangedEvent>(OnBindingChanged);
        SubscribeLocalEvent<DamageableComponent, DamageDealtEvent>(OnDamageDealt);
        SubscribeLocalEvent<NCMedicalBodyComponent, NCCombatImpactConfirmedEvent>(OnCombatImpactConfirmed);
        SubscribeLocalEvent<GunComponent, GunShotEvent>(OnGunShot);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            return;
        }

        using var work = _governor.MeasureWork(NCAILoadWorkKind.PhysicalPerception);
        var limit = _governor.TakeDispatchLimit(
            NCAILoadDispatchKind.PhysicalPerception,
            _policy.MaximumUpdatesPerTick);
        var dispatched = 0;
        while (dispatched < limit && TryDequeueDue(now, out var entityUid))
        {
            Evaluate(entityUid, now);
            dispatched++;
        }
        // Every tracked actor always owns one future schedule entry, so scheduled/tracked is naturally 100% and is
        // not queue pressure. Only work still due after the bounded dispatch pass represents saturation.
        var dueBacklog = HasDue(now);
        _governor.ReportQueue(NCAILoadQueueKind.PhysicalPerception, dueBacklog ? 1 : 0, 1);
        _governor.ReportBacklog(NCAILoadBacklogKind.PhysicalPerception, dueBacklog);
    }

    public bool Configure(EntityUid entityUid, ProtoId<NCCityLocalPerceptionProfilePrototype> profileId)
    {
        if (!_active || _policy == null || !Exists(entityUid) ||
            !_prototypes.TryIndex(profileId, out var profile) || profile == null || !Validate(profile))
        {
            _rejectedConfigurations++;
            return false;
        }
        var component = EnsureComp<NCCityLocalPerceptionComponent>(entityUid);
        component.Profile = profileId;
        _tracked.Add(entityUid);
        if (_clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch)
            Schedule(entityUid, now);
        return true;
    }

    public NCCityLocalPerceptionCounters GetCounters() => new(
        _active,
        _tracked.Count,
        _scheduled.Count,
        _evaluations,
        _observations,
        _rejectedConfigurations,
        _configurationError);

    public void Unconfigure(EntityUid entityUid) => Cleanup(entityUid, true);

    private void OnPhysicalActorSpawned(NCPhysicalActorSpawnedEvent args) =>
        TryConfigureBoundActor(args.ActorId, args.EntityUid);

    private void OnBindingChanged(NCCityActorBindingChangedEvent args)
    {
        if (!args.Bound)
        {
            Unconfigure(args.EntityUid);
            return;
        }

        // Spawned actors are configured again by NCPhysicalActorSpawnedEvent after their body is fully restored.
        // This early path also covers valid round-start/map bindings whose actor record is already cached.
        TryConfigureBoundActor(args.ActorId, args.EntityUid);
    }

    private bool TryConfigureBoundActor(NCActorId actorId, EntityUid entityUid)
    {
        if (!_active || !Exists(entityUid) || !_actors.TryGetCached(actorId, out var actor) ||
            !_prototypes.TryIndex(
                new ProtoId<NCCityActorArchetypePrototype>(actor.Snapshot.Identity.ArchetypeId),
                out var archetype) || archetype == null)
        {
            return false;
        }

        return Configure(entityUid, archetype.LocalPerceptionProfile);
    }

    /// <summary>
    /// Converts real damage into a witnessed actor→victim event for nearby configured humans. The spatial query is
    /// bounded by the largest authored perception radius and never iterates the city population.
    /// </summary>
    private void OnDamageDealt(Entity<DamageableComponent> victim, ref DamageDealtEvent args)
    {
        if (!_active || !args.Damage.AnyPositive() ||
            args.Origin is not { } origin || !Exists(origin) ||
            !_clock.TryGetCurrentTime(out _, out var now))
        {
            return;
        }

        ObserveAssault(victim.Owner, origin, now);
    }

    /// <summary>
    /// Consumes the confirmed result of the NC medical damage path, which intentionally bypasses legacy
    /// Damageable HP and therefore cannot produce DamageDealtEvent.
    /// </summary>
    private void OnCombatImpactConfirmed(
        Entity<NCMedicalBodyComponent> victim,
        ref NCCombatImpactConfirmedEvent args)
    {
        if (!_active || args.Target != victim.Owner || !args.ResolutionId.IsValid ||
            !Exists(args.Instigator) || !_clock.TryGetCurrentTime(out _, out var now))
        {
            return;
        }

        ObserveAssault(victim.Owner, args.Instigator, now, args.ResolutionId.Value);
    }

    private void ObserveAssault(
        EntityUid victim,
        EntityUid origin,
        NCActiveWorldTime now,
        Guid? confirmedEventId = null)
    {
        if (!TryResolveResponsibleActor(origin, out var attacker) || attacker == victim)
            return;

        using var work = _governor.MeasureWork(NCAILoadWorkKind.DamageEvents);
        var worldEventId = confirmedEventId is { } eventId && eventId != Guid.Empty
            ? eventId
            : CorrelateIncident(attacker, victim, now);

        // Being hit is authoritative proprioception. It must not depend on witness range, line of sight, or the
        // background perception budget; those checks apply only to bystanders.
        if (TryComp<NCCityLocalPerceptionComponent>(victim, out var victimPerception) &&
            _prototypes.TryIndex(victimPerception.Profile, out var victimProfile) && victimProfile != null)
        {
            if (_interaction.InRangeUnobstructed(victim, attacker, victimProfile.Radius))
            {
                ObserveIncident(victim, NCCityLocalPerceivedEventKind.Assault, attacker, victim, now,
                    victimProfile.WitnessedEventLifetime, worldEventId: worldEventId);
            }
            else
            {
                // The body knows it was hit, but damage metadata is not eyesight. Preserve the real attacker only
                // for the defensive reflex and authoritative incident consumers; cognition receives no targetable
                // identity until normal perception actually identifies the source.
                ObserveUnidentifiedIncident(victim, victimPerception, victimProfile,
                    NCCityLocalPerceivedEventKind.Assault, attacker, victim, now,
                    victimProfile.WitnessedEventLifetime, worldEventId: worldEventId);
            }
        }

        if (_maximumObservationRadius <= 0f)
            return;
        var coordinates = _transform.GetMapCoordinates(victim);
        foreach (var observer in _lookup.GetEntitiesInRange<NCCityLocalPerceptionComponent>(
                     coordinates,
                     _maximumObservationRadius))
        {
            if (observer.Owner == attacker || observer.Owner == victim ||
                !_prototypes.TryIndex(observer.Comp.Profile, out var profile) || profile == null ||
                !_transform.InRange(Transform(observer.Owner).Coordinates, Transform(victim).Coordinates,
                    profile.Radius) ||
                !_interaction.InRangeUnobstructed(observer.Owner, victim, profile.Radius))
            {
                continue;
            }
            ObserveIncident(observer.Owner, NCCityLocalPerceivedEventKind.Assault, attacker, victim, now,
                profile.WitnessedEventLifetime, worldEventId: worldEventId);
        }
    }

    /// <summary>Records an actually fired gun as an audible event; hearing does not require line of sight.</summary>
    private void OnGunShot(Entity<GunComponent> gun, ref GunShotEvent args)
    {
        if (!_active || _maximumObservationRadius <= 0f || !Exists(args.User) ||
            !_clock.TryGetCurrentTime(out _, out var now))
            return;

        var coordinates = _transform.GetMapCoordinates(args.User);
        foreach (var observer in _lookup.GetEntitiesInRange<NCCityLocalPerceptionComponent>(
                     coordinates,
                     _maximumObservationRadius))
        {
            if (observer.Owner == args.User ||
                !_prototypes.TryIndex(observer.Comp.Profile, out var profile) || profile == null ||
                !_transform.InRange(Transform(observer.Owner).Coordinates, Transform(args.User).Coordinates,
                    profile.Radius))
                continue;
            if (_interaction.InRangeUnobstructed(observer.Owner, args.User, profile.Radius))
            {
                ObserveIncident(observer.Owner, NCCityLocalPerceivedEventKind.Gunshot, args.User, args.User, now,
                    profile.WitnessedEventLifetime);
            }
            else
            {
                // Hearing proves a shot happened, not who fired it. Keep the source non-targetable until a visual
                // observation independently supplies a normal subject handle.
                ObserveUnidentifiedIncident(observer.Owner, observer.Comp, profile,
                    NCCityLocalPerceivedEventKind.Gunshot, args.User, args.User, now,
                    profile.WitnessedEventLifetime);
            }
        }
    }

    /// <summary>Resolves only an actor's current bounded sensory handle; this is not a global entity lookup.</summary>
    public bool TryResolveSubjectHandle(
        NCActorId observer,
        string handle,
        NCActiveWorldTime now,
        out EntityUid target)
    {
        target = EntityUid.Invalid;
        if (!_runtime.TryGetEntity(observer, out var entityUid) ||
            !TryComp<NCCityLocalPerceptionComponent>(entityUid, out var perception) ||
            !_prototypes.TryIndex(perception.Profile, out var profile) || profile == null)
            return false;
        foreach (var observation in perception.Observations)
        {
            if (observation.SubjectHandle != handle || observation.ExpiresAt <= now ||
                !CanResolveCurrentObservation(entityUid, perception, observation, profile))
                continue;
            target = observation.EntityUid;
            return true;
        }
        return false;
    }

    /// <summary>Returns a copy of the actor's current bounded sensory state for a single decision request.</summary>
    public bool TryGetSnapshot(
        NCActorId observer,
        NCActiveWorldTime now,
        out NCCityLocalObservation[] observations,
        out NCCityLocalPerceivedEvent[] events)
    {
        return TryGetSnapshot(observer, now, out observations, out events, out _);
    }

    /// <summary>
    /// Returns the current targetable sensorium plus bounded history of ordinary changes. A departed handle may
    /// remain in a delta, but never re-enters the current observation target catalog.
    /// </summary>
    public bool TryGetSnapshot(
        NCActorId observer,
        NCActiveWorldTime now,
        out NCCityLocalObservation[] observations,
        out NCCityLocalPerceivedEvent[] events,
        out NCCityLocalPerceptionDelta[] deltas)
    {
        observations = [];
        events = [];
        deltas = [];
        if (!_runtime.TryGetEntity(observer, out var entityUid) ||
            !TryComp<NCCityLocalPerceptionComponent>(entityUid, out var perception) ||
            !_prototypes.TryIndex(perception.Profile, out var profile) || profile == null)
        {
            return false;
        }

        observations = perception.Observations
            .Where(value => value.ExpiresAt > now &&
                CanResolveCurrentObservation(entityUid, perception, value, profile))
            .ToArray();
        events = perception.RecentEvents
            .Where(value => value.ExpiresAt > now && Exists(value.ActorEntity) && Exists(value.TargetEntity))
            // MobState is read only for the already bounded event set. This avoids claiming the vanilla component's
            // exclusive MobStateChangedEvent subscription while still presenting a lethal witnessed assault as a kill.
            .Select(value => value.Kind == NCCityLocalPerceivedEventKind.Assault &&
                             TryComp<MobStateComponent>(value.TargetEntity, out var mobState) &&
                             mobState.CurrentState == MobState.Dead
                ? value with { Kind = NCCityLocalPerceivedEventKind.Killing }
                : value)
            .ToArray();
        deltas = perception.PerceptionDeltas
            .Where(value => value.ExpiresAt > now)
            .Take(profile.MaximumPerceptionDeltas)
            .ToArray();
        return true;
    }

    private bool CanResolveCurrentObservation(
        EntityUid observer,
        NCCityLocalPerceptionComponent perception,
        NCCityLocalObservation observation,
        NCCityLocalPerceptionProfilePrototype profile)
    {
        if (!Exists(observation.EntityUid) || !perception.CurrentTargets.Contains(observation.EntityUid))
            return false;
        var selfContained = (observation.Kind is NCCityLocalObservationKind.HeldItem or
                                NCCityLocalObservationKind.EquippedItem) &&
                            TryResolveResponsibleActor(observation.EntityUid, out var owner) && owner == observer;
        return selfContained || _interaction.InRangeUnobstructed(observer, observation.EntityUid, profile.Radius);
    }

    /// <summary>Refreshes one actor on demand; dialogue never waits for the staggered background cadence.</summary>
    public bool RefreshNow(NCActorId observer, NCActiveWorldTime now)
    {
        if (!_runtime.TryGetEntity(observer, out var entityUid) ||
            !TryComp<NCCityLocalPerceptionComponent>(entityUid, out _))
        {
            return false;
        }
        Evaluate(entityUid, now);
        return true;
    }

    /// <summary>Adds one authoritative, short-lived subject to the observer's bounded sensory cache.</summary>
    public bool ObserveSubject(
        EntityUid observer,
        EntityUid subject,
        NCCityLocalObservationKind kind,
        NCActiveWorldTime now,
        TimeSpan lifetime,
        float confidence = 1f)
    {
        if (!_active || lifetime <= TimeSpan.Zero || !Exists(observer) || !Exists(subject) || observer == subject ||
            !TryComp<NCCityLocalPerceptionComponent>(observer, out var perception) ||
            !_prototypes.TryIndex(perception.Profile, out var profile) || profile == null)
        {
            return false;
        }

        var observerCoordinates = _transform.GetMapCoordinates(observer);
        var subjectCoordinates = _transform.GetMapCoordinates(subject);
        if (observerCoordinates.MapId != subjectCoordinates.MapId)
            return false;
        var previous = perception.Observations.FirstOrDefault(value => value.EntityUid == subject);
        perception.Observations.RemoveAll(value => value.EntityUid == subject);
        perception.CurrentTargets.Remove(subject);
        if (perception.Observations.Count >= profile.MaximumObservedEntities)
            perception.Observations.RemoveAt(perception.Observations.Count - 1);
        var distanceSquared = System.Numerics.Vector2.DistanceSquared(
            observerCoordinates.Position,
            subjectCoordinates.Position);
        var effectiveKind = kind == NCCityLocalObservationKind.Neutral &&
                            !string.IsNullOrWhiteSpace(previous.SubjectHandle) &&
                            previous.Kind != NCCityLocalObservationKind.Neutral
            ? previous.Kind
            : kind;
        perception.Observations.Insert(0, new NCCityLocalObservation(
            subject,
            string.IsNullOrWhiteSpace(previous.SubjectHandle)
                ? NewSubjectHandle()
                : previous.SubjectHandle,
            effectiveKind,
            distanceSquared,
            Math.Clamp(confidence, 0f, 1f),
            now,
            now.Advance(lifetime)));
        // Event-driven observations may run between scheduled scans. Only make the handle actionable when the
        // subject is still inside this observer's current, unobstructed sensorium.
        if (_interaction.InRangeUnobstructed(observer, subject, profile.Radius))
            perception.CurrentTargets.Add(subject);
        perception.Revision++;
        _observations++;
        return true;
    }

    /// <summary>Records who acted upon whom, preserving the relationship missing from ordinary proximity scans.</summary>
    public bool ObserveIncident(
        EntityUid observer,
        NCCityLocalPerceivedEventKind kind,
        EntityUid actor,
        EntityUid target,
        NCActiveWorldTime now,
        TimeSpan lifetime,
        float confidence = 1f,
        Guid? worldEventId = null)
    {
        var actorKind = kind switch
        {
            NCCityLocalPerceivedEventKind.Assault or NCCityLocalPerceivedEventKind.Killing or
                NCCityLocalPerceivedEventKind.VerbalThreat when observer == target =>
                NCCityLocalObservationKind.Hostile,
            NCCityLocalPerceivedEventKind.Assault or NCCityLocalPerceivedEventKind.Killing or
                NCCityLocalPerceivedEventKind.Gunshot or NCCityLocalPerceivedEventKind.VerbalThreat =>
                NCCityLocalObservationKind.Suspect,
            _ => NCCityLocalObservationKind.Neutral,
        };
        if (!ObserveSubject(observer, actor, actorKind, now, lifetime, confidence) ||
            actor != target && observer != target &&
            !ObserveSubject(observer, target, ObservationKind(target), now, lifetime, confidence) ||
            !TryComp<NCCityLocalPerceptionComponent>(observer, out var perception) ||
            !_prototypes.TryIndex(perception.Profile, out var profile) || profile == null)
        {
            return false;
        }

        return RecordObservedEvent(observer, perception, profile, kind, actor, target, now, lifetime, confidence,
            worldEventId);
    }

    /// <summary>Adds the authoritative fact that this actor was the listener selected for local speech.</summary>
    public bool ObserveAddressedSpeech(EntityUid observer, EntityUid speaker, NCActiveWorldTime now)
    {
        if (!TryComp<NCCityLocalPerceptionComponent>(observer, out var perception) ||
            !_prototypes.TryIndex(perception.Profile, out var profile) || profile == null)
            return false;
        return ObserveIncident(observer, NCCityLocalPerceivedEventKind.AddressedSpeech, speaker, observer, now,
            profile.WitnessedEventLifetime);
    }

    private bool RecordObservedEvent(
        EntityUid observer,
        NCCityLocalPerceptionComponent perception,
        NCCityLocalPerceptionProfilePrototype profile,
        NCCityLocalPerceivedEventKind kind,
        EntityUid actor,
        EntityUid target,
        NCActiveWorldTime now,
        TimeSpan lifetime,
        float confidence,
        Guid? worldEventId = null)
    {
        var actorObservation = perception.Observations.FirstOrDefault(value => value.EntityUid == actor);
        if (string.IsNullOrWhiteSpace(actorObservation.SubjectHandle))
            return false;
        var targetHandle = actor == target
            ? actorObservation.SubjectHandle
            : observer == target
            ? "self"
            : perception.Observations.FirstOrDefault(value => value.EntityUid == target).SubjectHandle;
        if (string.IsNullOrWhiteSpace(targetHandle))
            return false;
        var existing = perception.RecentEvents.FindIndex(value => value.Kind == kind && value.ActorEntity == actor &&
            value.TargetEntity == target);
        if (existing >= 0)
        {
            var current = perception.RecentEvents[existing];
            var raiseFreshWake = (kind is NCCityLocalPerceivedEventKind.Assault or
                                     NCCityLocalPerceivedEventKind.Killing) &&
                                 ShouldRaiseRepeatedIncident(current.ObservedAt, now,
                                     profile.RepeatedIncidentWakeInterval);
            perception.RecentEvents.RemoveAt(existing);
            perception.RecentEvents.Insert(0, current with
            {
                Confidence = Math.Max(current.Confidence, Math.Clamp(confidence, 0f, 1f)),
                ObservedAt = raiseFreshWake ? now : current.ObservedAt,
                ExpiresAt = now.Advance(lifetime),
            });
            perception.Revision++;
            if (raiseFreshWake)
                RaiseLocalEvent(new NCCityLocalIncidentObservedEvent(observer, current.EventId, kind, actor, target,
                    now));
            return true;
        }
        if (perception.RecentEvents.Count >= profile.MaximumObservedEntities)
            perception.RecentEvents.RemoveAt(perception.RecentEvents.Count - 1);
        var eventId = worldEventId is { } correlated && correlated != Guid.Empty ? correlated : Guid.NewGuid();
        perception.RecentEvents.Insert(0, new NCCityLocalPerceivedEvent(
            eventId,
            kind,
            actor,
            actorObservation.SubjectHandle,
            target,
            targetHandle,
            Math.Clamp(confidence, 0f, 1f),
            now,
            now.Advance(lifetime)));
        perception.Revision++;
        RefreshImmediateHostileSummary(perception, now);

        // Profession systems interpret this witnessed fact without subscribing to DamageDealtEvent themselves.
        RaiseLocalEvent(new NCCityLocalIncidentObservedEvent(observer, eventId, kind, actor, target, now));
        return true;
    }

    private bool ObserveUnidentifiedIncident(
        EntityUid observer,
        NCCityLocalPerceptionComponent perception,
        NCCityLocalPerceptionProfilePrototype profile,
        NCCityLocalPerceivedEventKind kind,
        EntityUid actor,
        EntityUid target,
        NCActiveWorldTime now,
        TimeSpan lifetime,
        float confidence = 1f,
        Guid? worldEventId = null)
    {
        var existing = perception.RecentEvents.FindIndex(value =>
            value.Kind == kind && value.ActorEntity == actor && value.TargetEntity == target);
        if (existing >= 0)
        {
            var current = perception.RecentEvents[existing];
            var raiseFreshWake = (kind is NCCityLocalPerceivedEventKind.Assault or
                                     NCCityLocalPerceivedEventKind.Killing) &&
                                 ShouldRaiseRepeatedIncident(current.ObservedAt, now,
                                     profile.RepeatedIncidentWakeInterval);
            perception.RecentEvents.RemoveAt(existing);
            perception.RecentEvents.Insert(0, current with
            {
                Confidence = Math.Max(current.Confidence, Math.Clamp(confidence, 0f, 1f)),
                ObservedAt = raiseFreshWake ? now : current.ObservedAt,
                ExpiresAt = now.Advance(lifetime),
            });
            perception.Revision++;
            if (raiseFreshWake)
                RaiseLocalEvent(new NCCityLocalIncidentObservedEvent(observer, current.EventId, kind, actor, target,
                    now));
            return true;
        }

        if (perception.RecentEvents.Count >= profile.MaximumObservedEntities)
            perception.RecentEvents.RemoveAt(perception.RecentEvents.Count - 1);
        var eventId = worldEventId is { } correlated && correlated != Guid.Empty ? correlated : Guid.NewGuid();
        var sourceHandle = $"{UnidentifiedSourceHandlePrefix}{eventId:N}";
        perception.RecentEvents.Insert(0, new NCCityLocalPerceivedEvent(
            eventId,
            kind,
            actor,
            sourceHandle,
            target,
            observer == target ? "self" : sourceHandle,
            Math.Clamp(confidence, 0f, 1f),
            now,
            now.Advance(lifetime)));
        perception.Revision++;
        RaiseLocalEvent(new NCCityLocalIncidentObservedEvent(observer, eventId, kind, actor, target, now));
        return true;
    }

    public static bool IsUnidentifiedSourceHandle(string handle) =>
        handle.StartsWith(UnidentifiedSourceHandlePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Adds a short-lived police observation produced by an authoritative gameplay event. The handle remains scoped
    /// to this observer and is therefore safe to expose to cognition and the neural service.
    /// </summary>
    public bool ObserveSuspect(
        EntityUid observer,
        EntityUid suspect,
        NCActiveWorldTime now,
        TimeSpan lifetime,
        float confidence = 1f,
        bool hostile = false)
    {
        var kind = hostile && !IsNeutralized(suspect)
            ? NCCityLocalObservationKind.Hostile
            : NCCityLocalObservationKind.Suspect;
        if (!ObserveSubject(observer, suspect, kind, now, lifetime, confidence) ||
            !TryComp<NCCityLocalPerceptionComponent>(observer, out var perception))
            return false;

        RefreshImmediateHostileSummary(perception, now);
        return true;
    }

    private void Evaluate(EntityUid entityUid, NCActiveWorldTime now)
    {
        if (!Exists(entityUid) || !TryComp<NCCityLocalPerceptionComponent>(entityUid, out var component) ||
            !_prototypes.TryIndex(component.Profile, out var profile) || profile == null || !Validate(profile))
        {
            Cleanup(entityUid, true);
            return;
        }

        // Handles survive consecutive observations but disappear with the bounded sensory cache. This gives neural
        // decisions a stable short-lived target without leaking EntityUid or accumulating a global target table.
        _retainedObservations.Clear();
        _previousVisibleObservations.Clear();
        _previousVisibleHeldItems.Clear();
        _previousHandles.Clear();
        foreach (var observation in component.Observations)
        {
            _previousHandles.TryAdd(observation.EntityUid, observation.SubjectHandle);
            if (component.CurrentTargets.Contains(observation.EntityUid) && observation.ExpiresAt > now)
                _previousVisibleObservations.Add(observation);
            if (_retainedObservations.Count >= profile.MaximumObservedEntities ||
                observation.Kind is not (NCCityLocalObservationKind.Suspect or
                    NCCityLocalObservationKind.Hostile) ||
                observation.ExpiresAt <= now || !Exists(observation.EntityUid))
            {
                continue;
            }
            _retainedObservations.Add(observation);
        }
        _previousVisibleHeldItems.AddRange(component.VisibleHeldItems);
        component.PerceptionDeltas.RemoveAll(value => value.ExpiresAt <= now);
        PromoteLethalEvents(entityUid, component, now);
        component.RecentEvents.RemoveAll(value => value.ExpiresAt <= now || !Exists(value.ActorEntity) ||
            !Exists(value.TargetEntity));
        component.Observations.Clear();
        component.VisibleHeldItems.Clear();
        component.CurrentTargets.Clear();
        component.HostileCount = 0;
        component.CriticalCount = 0;
        component.NearestHostile = EntityUid.Invalid;
        component.NearestCritical = EntityUid.Invalid;
        var nearestHostileDistance = float.MaxValue;
        var nearestCriticalDistance = float.MaxValue;
        var lineOfSightChecks = 0;
        var origin = _transform.GetMapCoordinates(entityUid);
        _observedBuffer.Clear();
        foreach (var observed in _retainedObservations)
        {
            var retained = RefreshRetainedObservation(entityUid, observed, origin, profile, now,
                ref lineOfSightChecks, out var retainedVisible);
            component.Observations.Add(retained);
            _observedBuffer.Add(retained.EntityUid);
            if (retainedVisible)
                component.CurrentTargets.Add(retained.EntityUid);
            if (retained.Kind != NCCityLocalObservationKind.Hostile)
                continue;
            component.HostileCount++;
            if (retained.DistanceSquared < nearestHostileDistance)
            {
                nearestHostileDistance = retained.DistanceSquared;
                component.NearestHostile = retained.EntityUid;
            }
        }
        if (TryComp<NpcFactionMemberComponent>(entityUid, out var ownFaction))
        {
            TryComp<FactionExceptionComponent>(entityUid, out var exception);
            foreach (var hostile in _factions.GetNearbyHostiles((entityUid, ownFaction, exception), profile.Radius))
            {
                if (component.Observations.Count >= profile.MaximumObservedEntities)
                    break;
                if (!_observedBuffer.Add(hostile))
                {
                    continue;
                }
                if (IsNeutralized(hostile))
                {
                    _factions.DeAggroEntity((entityUid, exception), hostile);
                    continue;
                }
                if (lineOfSightChecks >= profile.MaximumLineOfSightChecks)
                    break;
                lineOfSightChecks++;
                if (!_interaction.InRangeUnobstructed(entityUid, hostile, profile.Radius))
                    continue;

                var target = _transform.GetMapCoordinates(hostile);
                var distanceSquared = System.Numerics.Vector2.DistanceSquared(origin.Position, target.Position);
                component.Observations.Add(new NCCityLocalObservation(
                    hostile,
                    SubjectHandle(_previousHandles, hostile),
                    NCCityLocalObservationKind.Hostile,
                    distanceSquared,
                    Confidence(distanceSquared, profile.Radius),
                    now,
                    now.Advance(profile.EvaluationInterval + profile.EvaluationInterval)));
                component.CurrentTargets.Add(hostile);
                component.HostileCount++;
                if (distanceSquared < nearestHostileDistance)
                {
                    nearestHostileDistance = distanceSquared;
                    component.NearestHostile = hostile;
                }
            }
        }

        foreach (var candidate in _lookup.GetEntitiesInRange<MobStateComponent>(origin, profile.Radius))
        {
            if (component.Observations.Count >= profile.MaximumObservedEntities)
                break;
            if (candidate.Owner == entityUid || candidate.Comp.CurrentState is not (MobState.Critical or MobState.Dead) ||
                !_observedBuffer.Add(candidate.Owner))
            {
                continue;
            }
            if (lineOfSightChecks >= profile.MaximumLineOfSightChecks)
                break;
            lineOfSightChecks++;
            if (!_interaction.InRangeUnobstructed(entityUid, candidate.Owner, profile.Radius))
                continue;
            var target = _transform.GetMapCoordinates(candidate.Owner);
            var distanceSquared = System.Numerics.Vector2.DistanceSquared(origin.Position, target.Position);
            component.Observations.Add(new NCCityLocalObservation(
                candidate.Owner,
                SubjectHandle(_previousHandles, candidate.Owner),
                candidate.Comp.CurrentState == MobState.Dead
                    ? NCCityLocalObservationKind.Dead
                    : NCCityLocalObservationKind.Critical,
                distanceSquared,
                Confidence(distanceSquared, profile.Radius),
                now,
                now.Advance(profile.EvaluationInterval + profile.EvaluationInterval)));
            component.CurrentTargets.Add(candidate.Owner);
            RecordObservedEvent(entityUid, component, profile,
                candidate.Comp.CurrentState == MobState.Dead
                    ? NCCityLocalPerceivedEventKind.BodyDiscovered
                    : NCCityLocalPerceivedEventKind.SevereInjury,
                candidate.Owner, candidate.Owner, now, profile.WitnessedEventLifetime,
                Confidence(distanceSquared, profile.Radius));
            if (candidate.Comp.CurrentState == MobState.Critical)
                component.CriticalCount++;
            if (distanceSquared < nearestCriticalDistance)
            {
                nearestCriticalDistance = distanceSquared;
                component.NearestCritical = candidate.Owner;
            }
        }

        ObserveVisibleHazards(entityUid, component, profile, origin, now, ref lineOfSightChecks);
        ObserveAvailableActionTargets(entityUid, component, profile, origin, now, ref lineOfSightChecks);

        _currentVisibleObservations.Clear();
        foreach (var observation in component.Observations)
        {
            if (component.CurrentTargets.Contains(observation.EntityUid))
                _currentVisibleObservations.Add(observation);
        }
        if (component.HasPerceptionBaseline)
        {
            _perceptionDeltaBuffer.Clear();
            BuildPerceptionDeltas(
                _previousVisibleObservations,
                _currentVisibleObservations,
                _previousVisibleHeldItems,
                component.VisibleHeldItems,
                now,
                profile.PerceptionDeltaLifetime,
                _perceptionDeltaBuffer);
            foreach (var delta in _perceptionDeltaBuffer)
                RecordPerceptionDelta(component, profile, delta);
        }
        component.HasPerceptionBaseline = true;

        component.LastEvaluatedAt = now;
        component.Revision++;
        TryRaisePerceptionDeltaWake(entityUid, component, profile, now);
        _evaluations++;
        _observations += component.Observations.Count;
        var interval = _governor.ScaleEvaluationInterval(profile.EvaluationInterval);
        component.NextEvaluationAt = now.Advance(interval);
        Schedule(entityUid, component.NextEvaluationAt);
    }

    /// <summary>Checks only nearby indexed component sets and records bounded visible hazards.</summary>
    private void ObserveVisibleHazards(
        EntityUid observer,
        NCCityLocalPerceptionComponent perception,
        NCCityLocalPerceptionProfilePrototype profile,
        MapCoordinates origin,
        NCActiveWorldTime now,
        ref int lineOfSightChecks)
    {
        foreach (var burning in _lookup.GetEntitiesInRange<FlammableComponent>(origin, profile.Radius))
        {
            if (!burning.Comp.OnFire || burning.Owner == observer || !HasComp<MobStateComponent>(burning.Owner) ||
                lineOfSightChecks >= profile.MaximumLineOfSightChecks)
                continue;
            lineOfSightChecks++;
            if (!_interaction.InRangeUnobstructed(observer, burning.Owner, profile.Radius))
                continue;
            ObserveIncident(observer, NCCityLocalPerceivedEventKind.Fire, burning.Owner, burning.Owner, now,
                profile.WitnessedEventLifetime);
        }

        // Query people once, perform one LoS check per holder, then inspect the already-bounded hand contents.
        // This exposes ordinary held items to dialogue without scanning every item entity in the city.
        foreach (var holder in _lookup.GetEntitiesInRange<HandsComponent>(origin, profile.Radius))
        {
            if (holder.Owner == observer || !HasComp<MobStateComponent>(holder.Owner) ||
                lineOfSightChecks >= profile.MaximumLineOfSightChecks)
                continue;
            lineOfSightChecks++;
            if (!_interaction.InRangeUnobstructed(observer, holder.Owner, profile.Radius))
                continue;

            foreach (var held in _hands.EnumerateHeld(holder.AsNullable()))
            {
                if (!Exists(held))
                    continue;
                var kind = HasComp<GunComponent>(held) || HasComp<MeleeWeaponComponent>(held)
                    ? NCCityLocalPerceivedEventKind.WeaponDisplayed
                    : NCCityLocalPerceivedEventKind.HeldItemDisplayed;
                if (ObserveIncident(observer, kind, holder.Owner, held, now, profile.WitnessedEventLifetime))
                    RecordVisibleHeldItem(observer, perception, profile, holder.Owner, held);
            }
        }
    }

    /// <summary>
    /// Adds only nearby visible people and loose items, plus the actor's own inventory. This makes typed GPT actions
    /// targetable without exposing a global entity list or anything hidden inside another entity's containers.
    /// </summary>
    private void ObserveAvailableActionTargets(
        EntityUid observer,
        NCCityLocalPerceptionComponent perception,
        NCCityLocalPerceptionProfilePrototype profile,
        MapCoordinates origin,
        NCActiveWorldTime now,
        ref int lineOfSightChecks)
    {
        foreach (var observation in perception.Observations)
            _observedBuffer.Add(observation.EntityUid);

        if (TryComp<HandsComponent>(observer, out var hands))
        {
            foreach (var held in _hands.EnumerateHeld((observer, hands)))
            {
                AddKnownActionTarget(perception, profile, held, NCCityLocalObservationKind.HeldItem, now);
                RecordVisibleHeldItem(observer, perception, profile, observer, held);
            }
        }
        var slots = _inventory.GetSlotEnumerator(observer);
        while (slots.NextItem(out var equipped, out _))
            AddKnownActionTarget(perception, profile, equipped, NCCityLocalObservationKind.EquippedItem, now);

        foreach (var candidate in _lookup.GetEntitiesInRange<MobStateComponent>(origin, profile.Radius))
        {
            if (perception.Observations.Count >= profile.MaximumObservedEntities ||
                lineOfSightChecks >= profile.MaximumLineOfSightChecks)
                break;
            if (candidate.Owner == observer || candidate.Comp.CurrentState != MobState.Alive ||
                !_observedBuffer.Add(candidate.Owner))
                continue;
            lineOfSightChecks++;
            if (!_interaction.InRangeUnobstructed(observer, candidate.Owner, profile.Radius))
                continue;
            AddVisibleActionTarget(perception, profile, origin, candidate.Owner,
                NCCityLocalObservationKind.Neutral, now);
        }

        // Real affordance providers remain targetable on ordinary SS14 maps where no CityGraph has been authored.
        // Each category uses the same shared LoS budget and the entity buffer deduplicates multi-role machines.
        foreach (var candidate in _lookup.GetEntitiesInRange<NCCityAffordanceProviderComponent>(origin,
                     profile.Radius))
        {
            if (!TryAddVisibleActionTarget(observer, perception, profile, origin, candidate.Owner,
                    NCCityLocalObservationKind.Affordance, now, ref lineOfSightChecks))
                break;
        }
        // The affordance catalog also exposes common vanilla providers without requiring mapper-authored NC markers.
        // Query each indexed component family locally and keep using the same deduplication and LoS budgets.
        foreach (var candidate in _lookup.GetEntitiesInRange<DoorComponent>(origin, profile.Radius))
        {
            if (!TryAddVisibleActionTarget(observer, perception, profile, origin, candidate.Owner,
                    NCCityLocalObservationKind.Affordance, now, ref lineOfSightChecks))
                break;
        }
        foreach (var candidate in _lookup.GetEntitiesInRange<StorageComponent>(origin, profile.Radius))
        {
            if (!TryAddVisibleActionTarget(observer, perception, profile, origin, candidate.Owner,
                    NCCityLocalObservationKind.Affordance, now, ref lineOfSightChecks))
                break;
        }
        foreach (var candidate in _lookup.GetEntitiesInRange<ComputerComponent>(origin, profile.Radius))
        {
            if (!TryAddVisibleActionTarget(observer, perception, profile, origin, candidate.Owner,
                    NCCityLocalObservationKind.Affordance, now, ref lineOfSightChecks))
                break;
        }
        foreach (var candidate in _lookup.GetEntitiesInRange<ReagentDispenserComponent>(origin, profile.Radius))
        {
            if (!TryAddVisibleActionTarget(observer, perception, profile, origin, candidate.Owner,
                    NCCityLocalObservationKind.Affordance, now, ref lineOfSightChecks))
                break;
        }
        foreach (var candidate in _lookup.GetEntitiesInRange<MicrowaveComponent>(origin, profile.Radius))
        {
            if (!TryAddVisibleActionTarget(observer, perception, profile, origin, candidate.Owner,
                    NCCityLocalObservationKind.Affordance, now, ref lineOfSightChecks))
                break;
        }
        foreach (var candidate in _lookup.GetEntitiesInRange<PlantHolderComponent>(origin, profile.Radius))
        {
            if (!TryAddVisibleActionTarget(observer, perception, profile, origin, candidate.Owner,
                    NCCityLocalObservationKind.Affordance, now, ref lineOfSightChecks))
                break;
        }
        foreach (var candidate in _lookup.GetEntitiesInRange<PuddleComponent>(origin, profile.Radius))
        {
            if (!TryAddVisibleActionTarget(observer, perception, profile, origin, candidate.Owner,
                    NCCityLocalObservationKind.Affordance, now, ref lineOfSightChecks))
                break;
        }
        foreach (var candidate in _lookup.GetEntitiesInRange<DamageableComponent>(origin, profile.Radius))
        {
            if (_damageable.GetTotalDamage((candidate.Owner, candidate.Comp)) <= 0)
                continue;
            if (!TryAddVisibleActionTarget(observer, perception, profile, origin, candidate.Owner,
                    NCCityLocalObservationKind.Affordance, now, ref lineOfSightChecks))
                break;
        }
        foreach (var candidate in _lookup.GetEntitiesInRange<StrapComponent>(origin, profile.Radius))
        {
            if (!TryAddVisibleActionTarget(observer, perception, profile, origin, candidate.Owner,
                    NCCityLocalObservationKind.Seat, now, ref lineOfSightChecks))
                break;
        }
        foreach (var candidate in _lookup.GetEntitiesInRange<VendingMachineComponent>(origin, profile.Radius))
        {
            if (!TryAddVisibleActionTarget(observer, perception, profile, origin, candidate.Owner,
                    NCCityLocalObservationKind.VendingMachine, now, ref lineOfSightChecks))
                break;
        }
        foreach (var candidate in _lookup.GetEntitiesInRange<StasisBedComponent>(origin, profile.Radius))
        {
            if (!TryAddVisibleActionTarget(observer, perception, profile, origin, candidate.Owner,
                    NCCityLocalObservationKind.MedicalDevice, now, ref lineOfSightChecks))
                break;
        }
        foreach (var candidate in _lookup.GetEntitiesInRange<MedicalScannerComponent>(origin, profile.Radius))
        {
            if (!TryAddVisibleActionTarget(observer, perception, profile, origin, candidate.Owner,
                    NCCityLocalObservationKind.MedicalDevice, now, ref lineOfSightChecks))
                break;
        }

        foreach (var candidate in _lookup.GetEntitiesInRange<ItemComponent>(origin, profile.Radius))
        {
            if (perception.Observations.Count >= profile.MaximumObservedEntities ||
                lineOfSightChecks >= profile.MaximumLineOfSightChecks)
                break;
            if (!_observedBuffer.Add(candidate.Owner))
                continue;
            var parent = Transform(candidate.Owner).ParentUid;
            if (!HasComp<MapGridComponent>(parent) && !HasComp<MapComponent>(parent))
                continue;
            lineOfSightChecks++;
            if (!_interaction.InRangeUnobstructed(observer, candidate.Owner, profile.Radius))
                continue;
            AddVisibleActionTarget(perception, profile, origin, candidate.Owner,
                NCCityLocalObservationKind.Item, now);
        }
    }

    /// <summary>Consumes at most one shared LoS check and reports false only when this snapshot is out of budget.</summary>
    private bool TryAddVisibleActionTarget(
        EntityUid observer,
        NCCityLocalPerceptionComponent perception,
        NCCityLocalPerceptionProfilePrototype profile,
        MapCoordinates origin,
        EntityUid target,
        NCCityLocalObservationKind kind,
        NCActiveWorldTime now,
        ref int lineOfSightChecks)
    {
        if (perception.Observations.Count >= profile.MaximumObservedEntities ||
            lineOfSightChecks >= profile.MaximumLineOfSightChecks)
            return false;
        if (!_observedBuffer.Add(target))
            return true;
        lineOfSightChecks++;
        if (_interaction.InRangeUnobstructed(observer, target, profile.Radius))
            AddVisibleActionTarget(perception, profile, origin, target, kind, now);
        return true;
    }

    private static void RecordVisibleHeldItem(
        EntityUid observer,
        NCCityLocalPerceptionComponent perception,
        NCCityLocalPerceptionProfilePrototype profile,
        EntityUid holder,
        EntityUid item)
    {
        if (perception.VisibleHeldItems.Count >= profile.MaximumObservedEntities ||
            perception.VisibleHeldItems.Any(value => value.Holder == holder && value.Item == item))
            return;
        var itemHandle = perception.Observations
            .FirstOrDefault(value => value.EntityUid == item).SubjectHandle;
        var holderHandle = holder == observer
            ? "self"
            : perception.Observations.FirstOrDefault(value => value.EntityUid == holder).SubjectHandle;
        if (string.IsNullOrWhiteSpace(holderHandle) || string.IsNullOrWhiteSpace(itemHandle))
            return;
        perception.VisibleHeldItems.Add(new NCCityLocalHeldItemObservation(
            holder,
            holderHandle,
            item,
            itemHandle));
    }

    private void AddKnownActionTarget(
        NCCityLocalPerceptionComponent perception,
        NCCityLocalPerceptionProfilePrototype profile,
        EntityUid target,
        NCCityLocalObservationKind kind,
        NCActiveWorldTime now)
    {
        if (!Exists(target) || perception.Observations.Count >= profile.MaximumObservedEntities ||
            !_observedBuffer.Add(target))
            return;
        perception.Observations.Add(new NCCityLocalObservation(
            target,
            SubjectHandle(_previousHandles, target),
            kind,
            0f,
            1f,
            now,
            now.Advance(profile.EvaluationInterval + profile.EvaluationInterval)));
        perception.CurrentTargets.Add(target);
    }

    private void AddVisibleActionTarget(
        NCCityLocalPerceptionComponent perception,
        NCCityLocalPerceptionProfilePrototype profile,
        MapCoordinates origin,
        EntityUid target,
        NCCityLocalObservationKind kind,
        NCActiveWorldTime now)
    {
        var targetCoordinates = _transform.GetMapCoordinates(target);
        var distanceSquared = System.Numerics.Vector2.DistanceSquared(origin.Position, targetCoordinates.Position);
        perception.Observations.Add(new NCCityLocalObservation(
            target,
            SubjectHandle(_previousHandles, target),
            kind,
            distanceSquared,
            Confidence(distanceSquared, profile.Radius),
            now,
            now.Advance(profile.EvaluationInterval + profile.EvaluationInterval)));
        perception.CurrentTargets.Add(target);
    }

    /// <summary>
    /// Walks a bounded containment chain. Melee/projectiles already provide the user, while hitscan provides the
    /// held gun; nested storage and inventory slots therefore need more than the old three-parent guess.
    /// </summary>
    private bool TryResolveResponsibleActor(EntityUid origin, out EntityUid actor)
    {
        var current = origin;
        for (var depth = 0; depth < 16 && Exists(current); depth++)
        {
            if (HasComp<NCCityActorBindingComponent>(current) || HasComp<MobStateComponent>(current))
            {
                actor = current;
                return true;
            }
            if (!TryComp(current, out TransformComponent? transform))
                break;
            var parent = transform.ParentUid;
            if (!parent.IsValid() || parent == current)
                break;
            current = parent;
        }
        actor = EntityUid.Invalid;
        return false;
    }

    /// <summary>
    /// A suspect is evidence for police cognition, while a hostile is an immediate visible combat fact. Hostility is
    /// downgraded once sight is lost or vanilla state says that the subject can no longer fight.
    /// </summary>
    private NCCityLocalObservation RefreshRetainedObservation(
        EntityUid observer,
        NCCityLocalObservation observed,
        MapCoordinates origin,
        NCCityLocalPerceptionProfilePrototype profile,
        NCActiveWorldTime now,
        ref int lineOfSightChecks,
        out bool visible)
    {
        visible = false;
        var target = _transform.GetMapCoordinates(observed.EntityUid);
        if (target.MapId != origin.MapId)
            return observed with { Kind = NCCityLocalObservationKind.Suspect };
        var distanceSquared = System.Numerics.Vector2.DistanceSquared(origin.Position, target.Position);
        visible = distanceSquared <= profile.Radius * profile.Radius &&
                  lineOfSightChecks < profile.MaximumLineOfSightChecks;
        if (visible)
        {
            lineOfSightChecks++;
            visible = _interaction.InRangeUnobstructed(observer, observed.EntityUid, profile.Radius);
        }
        var neutralized = IsNeutralized(observed.EntityUid);
        if (neutralized)
            _factions.DeAggroEntity(observer, observed.EntityUid);
        return observed with
        {
            Kind = observed.Kind == NCCityLocalObservationKind.Hostile && visible && !neutralized
                ? NCCityLocalObservationKind.Hostile
                : NCCityLocalObservationKind.Suspect,
            DistanceSquared = distanceSquared,
            ObservedAt = visible ? now : observed.ObservedAt,
        };
    }

    private bool IsNeutralized(EntityUid entityUid) =>
        _cuffable.IsCuffed(entityUid, requireFullyCuffed: false) ||
        TryComp<MobStateComponent>(entityUid, out var mobState) && mobState.CurrentState != MobState.Alive;

    /// <summary>Updates the synchronous combat fact after an authoritative incident without waiting for the queue.</summary>
    private void RefreshImmediateHostileSummary(NCCityLocalPerceptionComponent perception, NCActiveWorldTime now)
    {
        perception.HostileCount = 0;
        perception.NearestHostile = EntityUid.Invalid;
        var nearestDistance = float.MaxValue;
        foreach (var observation in perception.Observations)
        {
            if (observation.Kind != NCCityLocalObservationKind.Hostile || observation.ExpiresAt <= now ||
                !Exists(observation.EntityUid) || IsNeutralized(observation.EntityUid))
                continue;
            perception.HostileCount++;
            if (observation.DistanceSquared >= nearestDistance)
                continue;
            nearestDistance = observation.DistanceSquared;
            perception.NearestHostile = observation.EntityUid;
        }
    }

    private void PromoteLethalEvents(
        EntityUid observer,
        NCCityLocalPerceptionComponent perception,
        NCActiveWorldTime now)
    {
        for (var index = 0; index < perception.RecentEvents.Count; index++)
        {
            var value = perception.RecentEvents[index];
            if (value.Kind != NCCityLocalPerceivedEventKind.Assault ||
                !TryComp<MobStateComponent>(value.TargetEntity, out var mobState) ||
                mobState.CurrentState != MobState.Dead)
                continue;
            var promoted = value with { Kind = NCCityLocalPerceivedEventKind.Killing };
            perception.RecentEvents[index] = promoted;
            RaiseLocalEvent(new NCCityLocalIncidentObservedEvent(observer, promoted.EventId,
                promoted.Kind, promoted.ActorEntity, promoted.TargetEntity, now));
        }
    }

    private void Schedule(EntityUid entityUid, NCActiveWorldTime dueAt)
    {
        if (_scheduled.TryGetValue(entityUid, out var existing) && existing <= dueAt)
            return;
        _scheduled[entityUid] = dueAt;
        var item = new ScheduledEvaluation(entityUid, dueAt, ++_sequence);
        _queue.Enqueue(item, new EvaluationPriority(dueAt.Ticks, item.Sequence));
    }

    private bool TryDequeueDue(NCActiveWorldTime now, out EntityUid entityUid)
    {
        while (_queue.TryPeek(out var item, out var priority) && priority.DueTicks <= now.Ticks)
        {
            _queue.Dequeue();
            if (!_scheduled.TryGetValue(item.EntityUid, out var dueAt) || dueAt != item.DueAt)
                continue;
            _scheduled.Remove(item.EntityUid);
            entityUid = item.EntityUid;
            return true;
        }
        entityUid = EntityUid.Invalid;
        return false;
    }

    private bool HasDue(NCActiveWorldTime now) =>
        _queue.TryPeek(out _, out var priority) && priority.DueTicks <= now.Ticks;

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (!_prototypes.TryIndex(
                new ProtoId<NCCityLocalPerceptionPolicyPrototype>(DefaultPolicyId),
                out var policy) || policy == null || policy.MaximumUpdatesPerTick <= 0 ||
            policy.MaximumIncidentCorrelations <= 0)
        {
            _configurationError = "default-local-perception-policy-invalid";
            return;
        }
        _policy = policy;
        _maximumObservationRadius = _prototypes.EnumeratePrototypes<NCCityLocalPerceptionProfilePrototype>()
            .Select(value => value.Radius)
            .Where(value => float.IsFinite(value) && value > 0f)
            .DefaultIfEmpty(0f)
            .Max();
        _maximumWitnessedEventLifetime = _prototypes.EnumeratePrototypes<NCCityLocalPerceptionProfilePrototype>()
            .Select(value => value.WitnessedEventLifetime)
            .Where(value => value > TimeSpan.Zero)
            .DefaultIfEmpty(TimeSpan.FromSeconds(30))
            .Max();
        _epoch = args.Epoch;
        _active = true;
    }

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args) => Reset();

    private void OnClockFailed(NCActiveWorldClockFailedEvent args) => Reset();

    private void OnShutdown(Entity<NCCityLocalPerceptionComponent> entity, ref ComponentShutdown args) =>
        Cleanup(entity.Owner, false);

    private void Cleanup(EntityUid entityUid, bool removeComponent)
    {
        _tracked.Remove(entityUid);
        _scheduled.Remove(entityUid);
        if (removeComponent && Exists(entityUid) && HasComp<NCCityLocalPerceptionComponent>(entityUid))
            RemCompDeferred<NCCityLocalPerceptionComponent>(entityUid);
    }

    private void Reset()
    {
        _active = false;
        var tracked = new EntityUid[_tracked.Count];
        _tracked.CopyTo(tracked);
        foreach (var entityUid in tracked)
            Cleanup(entityUid, true);
        _tracked.Clear();
        _scheduled.Clear();
        _queue.Clear();
        _observedBuffer.Clear();
        _retainedObservations.Clear();
        _previousVisibleObservations.Clear();
        _currentVisibleObservations.Clear();
        _previousVisibleHeldItems.Clear();
        _perceptionDeltaBuffer.Clear();
        _previousHandles.Clear();
        _incidentCorrelations.Clear();
        _incidentCorrelationExpiry.Clear();
        _policy = null;
        _epoch = default;
        _sequence = 0;
        _evaluations = 0;
        _observations = 0;
        _rejectedConfigurations = 0;
        _configurationError = null;
        _maximumObservationRadius = 0f;
        _maximumWitnessedEventLifetime = TimeSpan.Zero;
        _governor.ReportQueue(NCAILoadQueueKind.PhysicalPerception, 0, 1);
        _governor.ReportBacklog(NCAILoadBacklogKind.PhysicalPerception, false);
    }

    internal static bool Validate(NCCityLocalPerceptionProfilePrototype profile) =>
        float.IsFinite(profile.Radius) && profile.Radius > 0f &&
        profile.MaximumObservedEntities > 0 && profile.MaximumLineOfSightChecks > 0 &&
        profile.MaximumLineOfSightChecks <= profile.MaximumObservedEntities &&
        profile.EvaluationInterval > TimeSpan.Zero && profile.WitnessedEventLifetime > TimeSpan.Zero &&
        profile.RepeatedIncidentWakeInterval > TimeSpan.Zero &&
        profile.RepeatedIncidentWakeInterval <= profile.WitnessedEventLifetime &&
        profile.MaximumPerceptionDeltas is > 0 and <= 128 &&
        profile.PerceptionDeltaLifetime > TimeSpan.Zero &&
        profile.PerceptionDeltaWakeCooldown > TimeSpan.Zero &&
        profile.PerceptionDeltaWakeCooldown <= profile.PerceptionDeltaLifetime;

    internal static bool ShouldRaiseRepeatedIncident(
        NCActiveWorldTime previous,
        NCActiveWorldTime now,
        TimeSpan interval) =>
        interval > TimeSpan.Zero &&
        (!now.TryElapsedSince(previous, out var elapsed) || elapsed >= interval);

    internal static void BuildPerceptionDeltas(
        IReadOnlyList<NCCityLocalObservation> previous,
        IReadOnlyList<NCCityLocalObservation> current,
        IReadOnlyList<NCCityLocalHeldItemObservation> previousHeldItems,
        IReadOnlyList<NCCityLocalHeldItemObservation> currentHeldItems,
        NCActiveWorldTime now,
        TimeSpan lifetime,
        ICollection<NCCityLocalPerceptionDelta> output)
    {
        if (lifetime <= TimeSpan.Zero)
            return;
        var expiresAt = now.Advance(lifetime);
        var previousByEntity = previous
            .GroupBy(value => value.EntityUid)
            .ToDictionary(group => group.Key, group => group.First());
        var currentByEntity = current
            .GroupBy(value => value.EntityUid)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var observation in currentByEntity.Values)
        {
            if (!previousByEntity.TryGetValue(observation.EntityUid, out var old))
            {
                output.Add(new NCCityLocalPerceptionDelta(
                    IsObjectObservationKind(observation.Kind)
                        ? NCCityLocalPerceptionDeltaKind.ItemAppeared
                        : NCCityLocalPerceptionDeltaKind.PersonEntered,
                    observation.SubjectHandle,
                    string.Empty,
                    "absent",
                    ObservationKindKey(observation.Kind),
                    now,
                    expiresAt));
                continue;
            }
            if (!IsObjectObservationKind(observation.Kind) && !IsObjectObservationKind(old.Kind) &&
                observation.Kind != old.Kind)
            {
                output.Add(new NCCityLocalPerceptionDelta(
                    NCCityLocalPerceptionDeltaKind.SubjectStateChanged,
                    observation.SubjectHandle,
                    string.Empty,
                    ObservationKindKey(old.Kind),
                    ObservationKindKey(observation.Kind),
                    now,
                    expiresAt));
            }
        }

        foreach (var observation in previousByEntity.Values)
        {
            if (currentByEntity.ContainsKey(observation.EntityUid))
                continue;
            output.Add(new NCCityLocalPerceptionDelta(
                IsObjectObservationKind(observation.Kind)
                    ? NCCityLocalPerceptionDeltaKind.ItemDisappeared
                    : NCCityLocalPerceptionDeltaKind.PersonLeft,
                observation.SubjectHandle,
                string.Empty,
                ObservationKindKey(observation.Kind),
                "absent",
                now,
                expiresAt));
        }

        var previousHeld = previousHeldItems
            .GroupBy(value => (value.Holder, value.Item))
            .ToDictionary(group => group.Key, group => group.First());
        var currentHeld = currentHeldItems
            .GroupBy(value => (value.Holder, value.Item))
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var held in currentHeld)
        {
            if (previousHeld.ContainsKey(held.Key))
                continue;
            output.Add(new NCCityLocalPerceptionDelta(
                NCCityLocalPerceptionDeltaKind.HeldItemAdded,
                held.Value.HolderHandle,
                held.Value.ItemHandle,
                "not-held",
                "held",
                now,
                expiresAt));
        }
        foreach (var held in previousHeld)
        {
            if (currentHeld.ContainsKey(held.Key))
                continue;
            output.Add(new NCCityLocalPerceptionDelta(
                NCCityLocalPerceptionDeltaKind.HeldItemRemoved,
                held.Value.HolderHandle,
                held.Value.ItemHandle,
                "held",
                "not-held",
                now,
                expiresAt));
        }
    }

    private static void RecordPerceptionDelta(
        NCCityLocalPerceptionComponent perception,
        NCCityLocalPerceptionProfilePrototype profile,
        NCCityLocalPerceptionDelta delta)
    {
        var existing = perception.PerceptionDeltas.FindIndex(value =>
            value.Kind == delta.Kind && value.SubjectHandle == delta.SubjectHandle &&
            value.RelatedHandle == delta.RelatedHandle && value.PreviousKind == delta.PreviousKind &&
            value.CurrentKind == delta.CurrentKind);
        if (existing >= 0)
            perception.PerceptionDeltas.RemoveAt(existing);
        perception.PerceptionDeltas.Insert(0, delta);
        if (perception.PerceptionDeltas.Count > profile.MaximumPerceptionDeltas)
            perception.PerceptionDeltas.RemoveRange(
                profile.MaximumPerceptionDeltas,
                perception.PerceptionDeltas.Count - profile.MaximumPerceptionDeltas);
        perception.PerceptionDeltaWakePending = true;
    }

    private void TryRaisePerceptionDeltaWake(
        EntityUid observer,
        NCCityLocalPerceptionComponent perception,
        NCCityLocalPerceptionProfilePrototype profile,
        NCActiveWorldTime now)
    {
        if (!ShouldRaisePerceptionDeltaWake(
                perception.PerceptionDeltaWakePending,
                perception.PerceptionDeltas.Count,
                perception.NextPerceptionDeltaWakeAt,
                now))
            return;
        perception.PerceptionDeltaWakePending = false;
        perception.NextPerceptionDeltaWakeAt = now.Advance(profile.PerceptionDeltaWakeCooldown);
        RaiseLocalEvent(new NCCityLocalPerceptionDeltaObservedEvent(
            observer,
            perception.Revision,
            perception.PerceptionDeltas.Count,
            now));
    }

    internal static bool ShouldRaisePerceptionDeltaWake(
        bool pending,
        int deltaCount,
        NCActiveWorldTime nextWakeAt,
        NCActiveWorldTime now) =>
        pending && deltaCount > 0 && nextWakeAt <= now;

    internal static string PerceptionDeltaKindKey(NCCityLocalPerceptionDeltaKind kind) => kind switch
    {
        NCCityLocalPerceptionDeltaKind.PersonEntered => "person-entered",
        NCCityLocalPerceptionDeltaKind.PersonLeft => "person-left",
        NCCityLocalPerceptionDeltaKind.ItemAppeared => "item-appeared",
        NCCityLocalPerceptionDeltaKind.ItemDisappeared => "item-disappeared",
        NCCityLocalPerceptionDeltaKind.SubjectStateChanged => "subject-state-changed",
        NCCityLocalPerceptionDeltaKind.HeldItemAdded => "held-item-added",
        NCCityLocalPerceptionDeltaKind.HeldItemRemoved => "held-item-removed",
        _ => "unknown",
    };

    private static string ObservationKindKey(NCCityLocalObservationKind kind) =>
        kind.ToString().ToLowerInvariant();

    private static bool IsObjectObservationKind(NCCityLocalObservationKind kind) => kind is
        NCCityLocalObservationKind.Item or
        NCCityLocalObservationKind.HeldItem or
        NCCityLocalObservationKind.EquippedItem or
        NCCityLocalObservationKind.Affordance or
        NCCityLocalObservationKind.Seat or
        NCCityLocalObservationKind.VendingMachine or
        NCCityLocalObservationKind.MedicalDevice;

    private NCCityLocalObservationKind ObservationKind(EntityUid entityUid) =>
        HasComp<ItemComponent>(entityUid)
            ? NCCityLocalObservationKind.Item
            : NCCityLocalObservationKind.Neutral;

    private static string SubjectHandle(
        IReadOnlyDictionary<EntityUid, string> previous,
        EntityUid target) => previous.TryGetValue(target, out var handle)
        ? handle
        : NewSubjectHandle();

    private static string NewSubjectHandle() => $"perceived-{Guid.NewGuid():N}";

    private static float Confidence(float distanceSquared, float radius)
    {
        var normalized = MathF.Sqrt(Math.Max(0f, distanceSquared)) / radius;
        return Math.Clamp(1f - normalized * 0.5f, 0.5f, 1f);
    }

    private Guid CorrelateIncident(EntityUid actor, EntityUid target, NCActiveWorldTime now)
    {
        var key = (actor, target);
        TrimIncidentCorrelations(now);
        if (_incidentCorrelations.TryGetValue(key, out var existing) && existing.ExpiresAt > now)
        {
            var refreshed = existing with { ExpiresAt = now.Advance(_maximumWitnessedEventLifetime) };
            _incidentCorrelations[key] = refreshed;
            _incidentCorrelationExpiry.Enqueue(new IncidentCorrelationEntry(key, refreshed.ExpiresAt),
                refreshed.ExpiresAt.Ticks);
            return refreshed.EventId;
        }

        if (_policy != null && _incidentCorrelations.Count >= _policy.MaximumIncidentCorrelations)
            EvictOldestIncidentCorrelation();
        var created = new IncidentCorrelation(Guid.NewGuid(), now.Advance(_maximumWitnessedEventLifetime));
        _incidentCorrelations[key] = created;
        _incidentCorrelationExpiry.Enqueue(new IncidentCorrelationEntry(key, created.ExpiresAt),
            created.ExpiresAt.Ticks);
        return created.EventId;
    }

    private void TrimIncidentCorrelations(NCActiveWorldTime now)
    {
        while (_incidentCorrelationExpiry.TryPeek(out var entry, out var ticks) && ticks <= now.Ticks)
        {
            _incidentCorrelationExpiry.Dequeue();
            if (_incidentCorrelations.TryGetValue(entry.Key, out var current) && current.ExpiresAt == entry.ExpiresAt)
                _incidentCorrelations.Remove(entry.Key);
        }
    }

    private void EvictOldestIncidentCorrelation()
    {
        while (_incidentCorrelationExpiry.TryDequeue(out var entry, out _))
        {
            if (!_incidentCorrelations.TryGetValue(entry.Key, out var current) || current.ExpiresAt != entry.ExpiresAt)
                continue;
            _incidentCorrelations.Remove(entry.Key);
            return;
        }
    }

    private readonly record struct IncidentCorrelation(Guid EventId, NCActiveWorldTime ExpiresAt);
    private readonly record struct IncidentCorrelationEntry(
        (EntityUid Actor, EntityUid Target) Key,
        NCActiveWorldTime ExpiresAt);

    private readonly record struct ScheduledEvaluation(EntityUid EntityUid, NCActiveWorldTime DueAt, long Sequence);


    private readonly record struct EvaluationPriority(long DueTicks, long Sequence) : IComparable<EvaluationPriority>
    {
        public int CompareTo(EvaluationPriority other)
        {
            var due = DueTicks.CompareTo(other.DueTicks);
            return due != 0 ? due : Sequence.CompareTo(other.Sequence);
        }
    }
}
