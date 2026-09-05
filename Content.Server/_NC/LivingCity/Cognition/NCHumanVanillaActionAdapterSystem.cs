// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Affordances;
using Content.Server._NC.LivingCity.Cognition.Components;
using Content.Server._NC.LivingCity.Navigation;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Medical.Treatment;
using Content.Server.Chat.Systems;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.Chat;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Systems;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Affordances;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._NC.LivingCity.Cognition;

/// <summary>
/// Universal adapter from data-authored human actions to unchanged SS14 movement, HTN, interaction, hands,
/// inventory, chat and pulling authorities. It owns only bounded execution state and verifies real ECS results.
/// </summary>
public sealed partial class NCHumanVanillaActionAdapterSystem : EntitySystem
{
    private const string SteeringOwner = "human-vanilla-action";
    private const string DefensiveSteeringOwner = "human-defensive-reflex";

    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityLocalPerceptionSystem _perception = default!;
    [Dependency] private NCCityNpcSteeringLeaseSystem _steeringLease = default!;
    [Dependency] private NCCityAffordanceCatalogSystem _affordances = default!;
    [Dependency] private NCStationSemanticDestinationSystem _stationDestinations = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private HTNSystem _htn = default!;
    [Dependency] private NpcFactionSystem _factions = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedBuckleSystem _buckle = default!;
    [Dependency] private SharedStorageSystem _storage = default!;
    [Dependency] private IngestionSystem _ingestion = default!;
    [Dependency] private SharedGunSystem _gun = default!;
    [Dependency] private PullingSystem _pulling = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private NCTreatmentInteractionSystem _treatment = default!;

    private readonly HashSet<EntityUid> _owned = [];
    private readonly Dictionary<NCActorId, DefensiveReflexState> _defensiveReflexes = [];
    private readonly PriorityQueue<DefensiveReflexExpiry, long> _defensiveReflexExpiry = new();
    private readonly Dictionary<NCActionResolutionId, EntityUid> _treatmentOwners = [];
    private long _defensiveReflexSequence;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCHumanActionExecutionEvent>(OnExecute);
        SubscribeLocalEvent<NCHumanActionInterruptedEvent>(OnInterrupted);
        SubscribeLocalEvent<NCHumanVanillaActionComponent, EntitySpokeEvent>(OnSpoke);
        SubscribeLocalEvent<NCHumanVanillaActionComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCMedicalBodyComponent, NCTreatmentChangedEvent>(OnTreatmentChanged);
        SubscribeLocalEvent<NCCityActorBindingChangedEvent>(OnBindingChanged);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => StopAll());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => StopAll());
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_clock.TryGetCurrentTime(out _, out var now))
            return;

        // The heap keeps reflex expiry O(log n); stale refresh entries are discarded by sequence.
        while (_defensiveReflexExpiry.TryPeek(out _, out var due) && due <= now.Ticks)
        {
            var entry = _defensiveReflexExpiry.Dequeue();
            if (!_defensiveReflexes.TryGetValue(entry.ActorId, out var state) ||
                state.Sequence != entry.Sequence || state.ExpiresAt > now)
                continue;
            TryEndDefensiveReflex(entry.ActorId);
        }
    }

    /// <summary>
    /// Applies an immediate, time-bounded step away from a direct threat while GPT chooses the actual response. It
    /// exits stale work but deliberately grants no attack, pursuit, destination, or continuing flight choice.
    /// </summary>
    public bool TryStartDefensiveReflex(
        NCActorId actorId,
        EntityUid actor,
        EntityUid threat,
        NCActiveWorldTime now,
        TimeSpan duration,
        float retreatDistance)
    {
        if (!actorId.IsValid || duration <= TimeSpan.Zero || !Exists(actor) || !Exists(threat) || actor == threat ||
            !float.IsFinite(retreatDistance) || retreatDistance <= 0f ||
            !HasComp<MobStateComponent>(actor) || !_mobState.IsAlive(actor) ||
            !HasComp<MobStateComponent>(threat) || _mobState.IsDead(threat))
            return false;

        var actorMap = _transform.GetMapCoordinates(actor);
        var threatMap = _transform.GetMapCoordinates(threat);
        if (actorMap.MapId != threatMap.MapId ||
            !TryGetDefensiveOffset(actorMap.Position, threatMap.Position, retreatDistance,
                StableSeed(actorId.Value, 0), out var offset))
        {
            return false;
        }

        TryEndDefensiveReflex(actorId);
        if (_steeringLease.HasOtherOwner(actor, DefensiveSteeringOwner) ||
            !_steeringLease.Acquire(actor, DefensiveSteeringOwner))
        {
            return false;
        }

        var suspendedHtn = false;
        if (TryComp<HTNComponent>(actor, out var htn) && htn.Enabled)
        {
            _htn.SetHTNEnabled((actor, htn), false);
            suspendedHtn = true;
        }

        // Vanilla steering and pathfinding remain the sole movement authority. The reflex owns one short coordinate
        // goal and releases it either when GPT accepts a plan or when the bounded lease expires.
        var destination = _transform.ToCoordinates(
            Transform(actor).Coordinates.EntityId,
            actorMap.Offset(offset));
        var steering = _steering.Register(actor, destination);
        steering.ArriveOnLineOfSight = false;
        var sequence = ++_defensiveReflexSequence;
        var expiresAt = now.Advance(duration);
        _defensiveReflexes[actorId] = new DefensiveReflexState(
            actor,
            expiresAt,
            sequence,
            suspendedHtn);
        _defensiveReflexExpiry.Enqueue(new DefensiveReflexExpiry(actorId, sequence), expiresAt.Ticks);
        return true;
    }

    /// <summary>Ends only state owned by the temporary reflex and restores neural-primary idle.</summary>
    public bool TryEndDefensiveReflex(NCActorId actorId)
    {
        if (!_defensiveReflexes.Remove(actorId, out var state))
            return false;
        if (!Exists(state.Actor))
            return true;

        if (!_steeringLease.HasOtherOwner(state.Actor, DefensiveSteeringOwner) &&
            HasComp<NPCSteeringComponent>(state.Actor))
        {
            _steering.Unregister(state.Actor);
        }
        _steeringLease.Release(state.Actor, DefensiveSteeringOwner);
        if (state.SuspendedHtn && TryComp<HTNComponent>(state.Actor, out var htn) && !htn.Enabled)
            _htn.SetHTNEnabled((state.Actor, htn), true, htn.PlanCooldown);
        return true;
    }

    internal static bool TryGetDefensiveOffset(
        Vector2 actorPosition,
        Vector2 threatPosition,
        float retreatDistance,
        uint fallbackSeed,
        out Vector2 offset)
    {
        offset = default;
        if (!float.IsFinite(retreatDistance) || retreatDistance <= 0f)
            return false;

        var away = actorPosition - threatPosition;
        if (away.LengthSquared() <= 0.0001f)
        {
            var angle = fallbackSeed / (double) uint.MaxValue * Math.Tau;
            away = new Vector2((float) Math.Cos(angle), (float) Math.Sin(angle));
        }
        else
        {
            away = Vector2.Normalize(away);
        }

        offset = away * retreatDistance;
        return float.IsFinite(offset.X) && float.IsFinite(offset.Y);
    }

    private void OnExecute(NCHumanActionExecutionEvent args)
    {
        if (args.Action.Operation == NCHumanActionOperationKind.None)
            return;

        args.Handled = true;
        if (args.EntityUid is not { } actor || !Exists(actor))
        {
            args.Failure = "vanilla-action-entity-unavailable";
            return;
        }

        var state = GetState(actor, args);
        switch (args.Action.Operation)
        {
            case NCHumanActionOperationKind.ApproachTarget:
            case NCHumanActionOperationKind.FollowTarget:
                ExecuteFollow(actor, state, args);
                return;
            case NCHumanActionOperationKind.HoldPosition:
                ExecuteHold(actor, state, args);
                return;
            case NCHumanActionOperationKind.PatrolZone:
                ExecutePatrolZone(actor, state, args);
                return;
            case NCHumanActionOperationKind.MoveToStationDestination:
                ExecuteStationDestination(actor, state, args);
                return;
            case NCHumanActionOperationKind.InspectStationWorkplace:
                ExecuteInspectStationWorkplace(actor, state, args);
                return;
            case NCHumanActionOperationKind.FleeFromTarget:
                ExecuteFlee(actor, state, args);
                return;
            case NCHumanActionOperationKind.CombatAuthority:
                ExecuteCombatAuthority(actor, state, args);
                return;
            case NCHumanActionOperationKind.ActivateTarget:
            case NCHumanActionOperationKind.AltInteractTarget:
                ExecuteInteraction(actor, state, args);
                return;
            case NCHumanActionOperationKind.PickupTarget:
            case NCHumanActionOperationKind.DropTarget:
            case NCHumanActionOperationKind.EquipTarget:
            case NCHumanActionOperationKind.UnequipTarget:
            case NCHumanActionOperationKind.UseHeldTarget:
                ExecuteItemAction(actor, state, args);
                return;
            case NCHumanActionOperationKind.UseHeldOnTarget:
                ExecuteUseHeldOnTarget(actor, state, args);
                return;
            case NCHumanActionOperationKind.UseHeldOnInjuredPerson:
                ExecuteUseHeldOnInjuredPerson(actor, state, args);
                return;
            case NCHumanActionOperationKind.BuckleSelfToTarget:
                ExecuteBuckle(actor, state, args);
                return;
            case NCHumanActionOperationKind.UnbuckleSelf:
                ExecuteUnbuckle(actor, state, args);
                return;
            case NCHumanActionOperationKind.StoreHeldInTarget:
                ExecuteStoreHeld(actor, state, args);
                return;
            case NCHumanActionOperationKind.RetrieveTargetFromStorage:
                ExecuteRetrieveStored(actor, state, args);
                return;
            case NCHumanActionOperationKind.ConsumeHeldTarget:
                ExecuteConsumeHeld(actor, state, args);
                return;
            case NCHumanActionOperationKind.FireHeldGunAtTarget:
                ExecuteFireHeldGun(actor, state, args);
                return;
            case NCHumanActionOperationKind.ReloadHeldGunWithTarget:
                ExecuteReloadHeldGun(actor, state, args);
                return;
            case NCHumanActionOperationKind.Speak:
                ExecuteSpeech(actor, state, args);
                return;
            case NCHumanActionOperationKind.StartPullingTarget:
                ExecuteStartPulling(actor, state, args);
                return;
            case NCHumanActionOperationKind.StopPulling:
                ExecuteStopPulling(actor, state, args);
                return;
            default:
                Fail(actor, args, $"vanilla-action-operation-unhandled:{args.Action.Operation}");
                return;
        }
    }

    private NCHumanVanillaActionComponent GetState(EntityUid actor, NCHumanActionExecutionEvent args)
    {
        if (TryComp<NCHumanVanillaActionComponent>(actor, out var existing) &&
            (existing.InstanceId != args.InstanceId || existing.Operation != args.Action.Operation))
        {
            Cleanup(actor, existing);
        }

        var state = EnsureComp<NCHumanVanillaActionComponent>(actor);
        state.InstanceId = args.InstanceId;
        state.Operation = args.Action.Operation;
        _owned.Add(actor);
        return state;
    }

    private void ExecuteFollow(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        // A person resolved through a bounded perception handle remains valid for this one action instance. This
        // lets a real follow action continue after the original speech/perception snapshot expires.
        var target = state.Target.IsValid() && Exists(state.Target)
            ? state.Target
            : EntityUid.Invalid;
        if ((!target.IsValid() && !TryResolveVisibleTarget(args, out target)) || target == actor)
        {
            Fail(actor, args, "vanilla-movement-target-unavailable");
            return;
        }
        state.Target = target;

        if (args.Action.MaximumRange > 0f &&
            !_transform.InRange(Transform(actor).Coordinates, Transform(target).Coordinates, args.Action.MaximumRange))
        {
            Fail(actor, args, "vanilla-movement-target-out-of-range");
            return;
        }
        var inRange = _transform.InRange(Transform(actor).Coordinates, Transform(target).Coordinates,
            args.Action.CompletionRange);
        if (inRange && args.Action.Operation == NCHumanActionOperationKind.ApproachTarget)
        {
            Complete(actor, args);
            return;
        }
        if (state.OwnsSteering && TryComp<NPCSteeringComponent>(actor, out var steering) &&
            steering.Status == SteeringStatus.NoPath)
        {
            Fail(actor, args, "vanilla-movement-no-path");
            return;
        }
        if (!AcquireMovement(actor, state))
            return;

        if (inRange)
        {
            // Keep the lease and HTN suspension, but do not path into the followed person's collision body.
            StopSteeringGoal(actor, state);
            return;
        }

        // Entity-relative steering keeps following a moving human and preserves vanilla collision/path checks.
        RegisterEntitySteeringGoal(actor, state, target, args.Action.CompletionRange);
    }

    private void ExecuteHold(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        // Holding is intentionally non-terminal. The directive owner completes or interrupts it when the authorized
        // order expires; the vanilla adapter only owns steering/HTN suspension while it remains active.
        AcquireMovement(actor, state);
    }

    private void ExecutePatrolZone(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (args.Target.Kind != NCCognitionReferenceKind.Zone ||
            !Guid.TryParse(args.Target.StableId, out var rawZone))
        {
            Fail(actor, args, "vanilla-patrol-zone-invalid");
            return;
        }

        if (state.HasTargetCoordinates && IsReached(actor, state.TargetCoordinates, args.Action.CompletionRange))
        {
            // Keep the same bounded steering authority while selecting another catalogued point in this zone.
            StopSteeringGoal(actor, state);
            state.HasTargetCoordinates = false;
            state.PatrolCycle++;
        }
        if (state.OwnsSteering && TryComp<NPCSteeringComponent>(actor, out var steering) &&
            steering.Status == SteeringStatus.NoPath)
        {
            StopSteeringGoal(actor, state);
            state.HasTargetCoordinates = false;
            state.PatrolCycle++;
        }
        if (!state.HasTargetCoordinates)
        {
            var zone = new NCZoneId(rawZone);
            if (!_affordances.TrySelect(NCCityAffordanceKind.PatrolArea, zone,
                    StableSeed(args.ActorId.Value, state.PatrolCycle), out var affordance) ||
                !TryResolveEntityCoordinates(affordance.Target, out _))
            {
                Fail(actor, args, "vanilla-patrol-zone-has-no-reachable-points");
                return;
            }
            state.TargetCoordinates = affordance.Target;
            state.HasTargetCoordinates = true;
        }

        if (!AcquireMovement(actor, state) ||
            !TryResolveEntityCoordinates(state.TargetCoordinates, out var destination))
            return;
        RegisterCoordinateSteeringGoal(actor, state, destination, args.Action, args.Now);
    }

    private void ExecuteStationDestination(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (args.Target.Kind != NCCognitionReferenceKind.StableKey ||
            !_stationDestinations.TryResolve(actor, args.Target.StableId, out var target))
        {
            Fail(actor, args, "vanilla-station-destination-unavailable");
            return;
        }

        var current = _transform.GetMapCoordinates(actor);
        if (current.MapId != target.MapId)
        {
            Fail(actor, args, "vanilla-station-destination-wrong-map");
            return;
        }

        if (Vector2.DistanceSquared(current.Position, target.Position) <=
            args.Action.CompletionRange * args.Action.CompletionRange)
        {
            Complete(actor, args);
            return;
        }

        if (state.OwnsSteering && TryComp<NPCSteeringComponent>(actor, out var steering) &&
            steering.Status == SteeringStatus.NoPath)
        {
            Fail(actor, args, "vanilla-station-destination-no-path");
            return;
        }

        if (!AcquireMovement(actor, state) ||
            !_maps.TryGetMap(target.MapId, out var mapUid) || mapUid is not { } map ||
            !_maps.TryFindGridAt(map, target.Position, out var gridUid, out _))
        {
            Fail(actor, args, "vanilla-station-destination-unreachable");
            return;
        }

        var destination = _transform.ToCoordinates((gridUid, null), target);
        RegisterCoordinateSteeringGoal(actor, state, destination, args.Action, args.Now);
    }

    private void ExecuteInspectStationWorkplace(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (args.Target.Kind != NCCognitionReferenceKind.StableKey ||
            !_stationDestinations.TryResolve(actor, args.Target.StableId, out var anchor))
        {
            Fail(actor, args, "vanilla-station-workplace-unavailable");
            return;
        }

        var current = _transform.GetMapCoordinates(actor);
        if (current.MapId != anchor.MapId)
        {
            Fail(actor, args, "vanilla-station-workplace-wrong-map");
            return;
        }

        if (state.OperationAttempted && state.HasRegisteredSteeringDestination &&
            state.RegisteredSteeringDestination.MapId == current.MapId &&
            Vector2.DistanceSquared(current.Position, state.RegisteredSteeringDestination.Position) <=
            args.Action.CompletionRange * args.Action.CompletionRange)
        {
            Complete(actor, args);
            return;
        }

        if (state.OwnsSteering && TryComp<NPCSteeringComponent>(actor, out var steering) &&
            steering.Status == SteeringStatus.NoPath)
        {
            Fail(actor, args, "vanilla-station-workplace-no-path");
            return;
        }

        if (state.OperationAttempted)
            return;

        var destination = anchor;
        var radius = Math.Max(args.Action.CompletionRange * 2f, args.Action.MaximumRange);
        if (Vector2.DistanceSquared(current.Position, anchor.Position) <= radius * radius)
        {
            var angle = StableSeed(args.ActorId.Value, state.PatrolCycle) / (double) uint.MaxValue * Math.Tau;
            destination = new MapCoordinates(
                anchor.Position + new Vector2((float) Math.Cos(angle), (float) Math.Sin(angle)) * radius,
                anchor.MapId);
        }

        if (!AcquireMovement(actor, state) || !_maps.TryGetMap(destination.MapId, out var mapUid) ||
            mapUid is not { } map || !_maps.TryFindGridAt(map, destination.Position, out var gridUid, out _))
        {
            Fail(actor, args, "vanilla-station-workplace-unreachable");
            return;
        }

        state.OperationAttempted = true;
        state.PatrolCycle++;
        var coordinates = _transform.ToCoordinates((gridUid, null), destination);
        RegisterCoordinateSteeringGoal(actor, state, coordinates, args.Action, args.Now);
    }

    private void ExecuteFlee(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (!TryResolveVisibleTarget(args, out var target) || target == actor)
        {
            Fail(actor, args, "vanilla-flee-threat-unavailable");
            return;
        }
        state.Target = target;
        var actorMap = _transform.GetMapCoordinates(actor);
        var targetMap = _transform.GetMapCoordinates(target);
        if (actorMap.MapId != targetMap.MapId)
        {
            Complete(actor, args);
            return;
        }

        var delta = actorMap.Position - targetMap.Position;
        var distance = delta.Length();
        if (distance >= args.Action.CompletionRange)
        {
            Complete(actor, args);
            return;
        }
        if (state.OwnsSteering && TryComp<NPCSteeringComponent>(actor, out var steering) &&
            steering.Status == SteeringStatus.NoPath)
        {
            Fail(actor, args, "vanilla-flee-no-path");
            return;
        }
        if (!AcquireMovement(actor, state))
            return;

        var direction = distance > 0.001f ? delta / distance : Vector2.UnitX;
        var destination = Transform(actor).Coordinates.Offset(direction * args.Action.CompletionRange);
        RegisterCoordinateSteeringGoal(actor, state, destination, args.Action, args.Now);
    }

    private void ExecuteCombatAuthority(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        var target = state.Target.IsValid() && Exists(state.Target)
            ? state.Target
            : EntityUid.Invalid;
        if ((!target.IsValid() && !TryResolveVisibleTarget(args, out target)) || target == actor ||
            !TryComp<MobStateComponent>(target, out var targetMob))
        {
            Fail(actor, args, "vanilla-combat-target-unavailable");
            return;
        }
        state.Target = target;
        if (targetMob.CurrentState != MobState.Alive)
        {
            _factions.DeAggroEntity(actor, target);
            Complete(actor, args);
            return;
        }
        // GPT chooses fight rather than flight; vanilla faction targeting and HTN remain combat authority.
        _factions.AggroEntity(actor, target);

        // The authored NCLivingHumanCompound selects vanilla ranged/melee/juke branches; this adapter never attacks.
        if (TryComp<HTNComponent>(actor, out var htn) && !htn.Enabled)
        {
            _htn.SetHTNEnabled((actor, htn), true, htn.PlanCooldown);
            state.EnabledHtn = true;
        }
    }

    private void ExecuteInteraction(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (!TryResolveVisibleTarget(args, out var target) || !CheckInteractionRange(actor, target, args))
        {
            Fail(actor, args, "vanilla-interaction-target-unavailable");
            return;
        }
        state.Target = target;
        var succeeded = args.Action.Operation == NCHumanActionOperationKind.ActivateTarget
            ? _interaction.InteractionActivate(actor, target)
            : _interaction.AltInteract(actor, target);
        if (!succeeded)
        {
            Fail(actor, args, "vanilla-interaction-rejected");
            return;
        }
        Complete(actor, args);
    }

    private void ExecuteItemAction(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (!TryResolveItemTarget(actor, state, args, out var item))
        {
            Fail(actor, args, "vanilla-item-target-unavailable");
            return;
        }
        state.Target = item;

        switch (args.Action.Operation)
        {
            case NCHumanActionOperationKind.PickupTarget:
                if (_hands.IsHolding(actor, item) ||
                    CheckInteractionRange(actor, item, args) && _hands.TryPickupAnyHand(actor, item) &&
                    _hands.IsHolding(actor, item))
                    Complete(actor, args);
                else
                    Fail(actor, args, "vanilla-item-pickup-rejected");
                return;
            case NCHumanActionOperationKind.DropTarget:
                if (!_hands.IsHolding(actor, item) ||
                    _hands.TryDrop(actor, item) && !_hands.IsHolding(actor, item))
                    Complete(actor, args);
                else
                    Fail(actor, args, "vanilla-item-drop-rejected");
                return;
            case NCHumanActionOperationKind.EquipTarget:
                ExecuteEquip(actor, item, state, args);
                return;
            case NCHumanActionOperationKind.UnequipTarget:
                ExecuteUnequip(actor, item, state, args);
                return;
            case NCHumanActionOperationKind.UseHeldTarget:
                if (!_hands.IsHolding(actor, item) || !_hands.TrySelect(actor, item) ||
                    !_interaction.UseInHandInteraction(actor, item))
                    Fail(actor, args, "vanilla-held-item-use-rejected");
                else
                    Complete(actor, args);
                return;
        }
    }

    private void ExecuteUseHeldOnTarget(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (state.OperationAttempted)
        {
            var waiting = false;
            foreach (var id in state.PendingDoAfterIds)
            {
                switch (ClassifyInteractionReceipt(_doAfter.GetStatus(actor, id)))
                {
                    case NCHumanInteractionReceiptState.Pending:
                        waiting = true;
                        break;
                    case NCHumanInteractionReceiptState.Failed:
                        Fail(actor, args, "vanilla-held-on-target-do-after-failed");
                        return;
                }
            }

            if (!waiting)
                Complete(actor, args);
            return;
        }

        if (!TryResolveVisibleTarget(args, out var target) || !CheckInteractionRange(actor, target, args))
        {
            Fail(actor, args, "vanilla-held-on-target-unavailable");
            return;
        }
        if (!_hands.TryGetActiveItem(actor, out var activeItem) || activeItem is not { } held)
        {
            Fail(actor, args, "vanilla-active-item-unavailable");
            return;
        }

        state.Target = target;
        var existingDoAfters = TryComp<DoAfterComponent>(actor, out var before)
            ? before.DoAfters.Keys.ToHashSet()
            : [];
        if (!_interaction.InteractUsing(actor, held, target, Transform(target).Coordinates))
        {
            Fail(actor, args, "vanilla-held-on-target-rejected");
            return;
        }

        // InteractUsing is authoritative only for synchronous handlers. If the vanilla authority started one or
        // more DoAfters, retain the normal action until every resulting receipt reports Finished.
        if (TryComp<DoAfterComponent>(actor, out var after))
        {
            foreach (var id in after.DoAfters.Keys)
            {
                if (!existingDoAfters.Contains(id))
                    state.PendingDoAfterIds.Add(id);
            }
        }
        if (state.PendingDoAfterIds.Count > 0)
        {
            state.OperationAttempted = true;
            return;
        }

        Complete(actor, args);
    }

    internal static NCHumanInteractionReceiptState ClassifyInteractionReceipt(DoAfterStatus status) => status switch
    {
        DoAfterStatus.Running => NCHumanInteractionReceiptState.Pending,
        DoAfterStatus.Finished => NCHumanInteractionReceiptState.Completed,
        _ => NCHumanInteractionReceiptState.Failed,
    };

    /// <summary>
    /// Starts the normal player treatment interaction, then remains running until that authority reports a real
    /// medical mutation. Merely accepting InteractUsing or starting its DoAfter is never completion.
    /// </summary>
    private void ExecuteUseHeldOnInjuredPerson(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        var target = state.Target.IsValid() && Exists(state.Target)
            ? state.Target
            : EntityUid.Invalid;
        if ((!target.IsValid() && !TryResolveVisibleTarget(args, out target)) || target == actor ||
            !CheckInteractionRange(actor, target, args) ||
            !TryComp<DamageableComponent>(target, out var damageable))
        {
            Fail(actor, args, "vanilla-treatment-target-unavailable");
            return;
        }

        state.Target = target;
        var currentDamage = _damageable.GetTotalDamage((target, damageable));
        if (state.OperationAttempted)
        {
            // NC treatment is correlated by its authoritative resolution event. The damage delta fallback is
            // reserved for vanilla healing items that do not expose that receipt; unrelated healing must not
            // complete a pending NC treatment command.
            var completed = state.TreatmentResolutionId.IsValid
                ? state.TreatmentConfirmed
                : HasAuthoritativeDamageReduction(state.HasInitialTargetDamage, state.InitialTargetDamage,
                    currentDamage);
            if (completed)
            {
                Complete(actor, args);
            }
            return;
        }

        if (!_hands.TryGetActiveItem(actor, out var activeItem) || activeItem is not { } held)
        {
            Fail(actor, args, "vanilla-treatment-active-item-unavailable");
            return;
        }

        state.HasInitialTargetDamage = true;
        state.InitialTargetDamage = currentDamage;
        if (TryComp<NCMedicalBodyComponent>(target, out var medicalBody))
        {
            state.InitialMedicalRevision = medicalBody.MedicalRevision;
            if (!_treatment.TryStartItemTreatment(
                    actor,
                    held,
                    target,
                    out var resolutionId,
                    out _,
                    out _,
                    out _))
            {
                Fail(actor, args, "vanilla-treatment-start-rejected");
                return;
            }

            state.TreatmentResolutionId = resolutionId;
            state.OperationAttempted = true;
            _treatmentOwners[resolutionId] = actor;
            return;
        }

        if (currentDamage <= FixedPoint2.Zero ||
            !_interaction.InteractUsing(actor, held, target, Transform(target).Coordinates))
        {
            Fail(actor, args, "vanilla-treatment-start-rejected");
            return;
        }
        state.OperationAttempted = true;
        if (TryComp<DamageableComponent>(target, out damageable) &&
            HasAuthoritativeDamageReduction(true, state.InitialTargetDamage,
                _damageable.GetTotalDamage((target, damageable))))
        {
            Complete(actor, args);
        }
    }

    internal static bool HasAuthoritativeDamageReduction(
        bool hasInitialDamage,
        FixedPoint2 initialDamage,
        FixedPoint2 currentDamage) =>
        hasInitialDamage && currentDamage < initialDamage;

    private void ExecuteEquip(
        EntityUid actor,
        EntityUid item,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (TryFindEquippedSlot(actor, item, out _))
        {
            Complete(actor, args);
            return;
        }
        if (!_hands.IsHolding(actor, item) &&
            (!CheckInteractionRange(actor, item, args) || !_hands.TryPickupAnyHand(actor, item)))
        {
            Fail(actor, args, "vanilla-item-equip-pickup-rejected");
            return;
        }

        if (state.InventorySlot.Length == 0)
        {
            if (!_inventory.TryGetSlots(actor, out var slots) || slots == null)
            {
                Fail(actor, args, "vanilla-item-inventory-unavailable");
                return;
            }
            foreach (var slot in slots.OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                if (!_inventory.CanEquip(actor, item, slot.Name, out _))
                    continue;
                state.InventorySlot = slot.Name;
                break;
            }
        }
        if (state.InventorySlot.Length == 0)
        {
            Fail(actor, args, "vanilla-item-no-compatible-slot");
            return;
        }

        _inventory.TryEquip(actor, item, state.InventorySlot, silent: true, checkDoafter: true,
            triggerHandContact: true);
        if (TryFindEquippedSlot(actor, item, out _))
            Complete(actor, args);
    }

    private void ExecuteUnequip(
        EntityUid actor,
        EntityUid item,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (!TryFindEquippedSlot(actor, item, out var slot))
        {
            Complete(actor, args);
            return;
        }
        state.InventorySlot = slot;
        _inventory.TryUnequip(actor, slot, out _, silent: true, checkDoafter: true,
            triggerHandContact: true);
        if (!TryFindEquippedSlot(actor, item, out _))
            Complete(actor, args);
    }

    private void ExecuteBuckle(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (!TryResolveVisibleTarget(args, out var target) ||
            !CheckInteractionRange(actor, target, args) ||
            !TryComp<BuckleComponent>(actor, out var buckle) ||
            !HasComp<StrapComponent>(target))
        {
            Fail(actor, args, "vanilla-buckle-target-unavailable");
            return;
        }

        state.Target = target;
        if (buckle.BuckledTo == target ||
            buckle.BuckledTo == null && _buckle.TryBuckle(actor, actor, target, buckle, popup: false) &&
            buckle.BuckledTo == target)
        {
            Complete(actor, args);
            return;
        }

        Fail(actor, args, "vanilla-buckle-rejected");
    }

    private void ExecuteUnbuckle(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (!TryComp<BuckleComponent>(actor, out var buckle) || !buckle.Buckled)
        {
            Complete(actor, args);
            return;
        }

        state.Target = buckle.BuckledTo ?? EntityUid.Invalid;
        if (_buckle.TryUnbuckle((actor, buckle), actor, popup: false) && !buckle.Buckled)
        {
            Complete(actor, args);
            return;
        }

        Fail(actor, args, "vanilla-unbuckle-rejected");
    }

    private void ExecuteStoreHeld(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (!TryResolveVisibleTarget(args, out var target) ||
            !CheckInteractionRange(actor, target, args) ||
            !TryComp<StorageComponent>(target, out var storage) ||
            !TryComp<HandsComponent>(actor, out var hands) ||
            !_hands.TryGetActiveItem((actor, hands), out var activeItem) ||
            activeItem is not { } held ||
            !_hands.IsHolding(actor, held))
        {
            Fail(actor, args, "vanilla-storage-target-unavailable");
            return;
        }

        state.Target = target;
        if (_storage.PlayerInsertHeldEntity((target, storage), (actor, hands)) &&
            !_hands.IsHolding(actor, held) && storage.Container.Contains(held))
        {
            Complete(actor, args);
            return;
        }

        Fail(actor, args, "vanilla-storage-insert-rejected");
    }

    private void ExecuteRetrieveStored(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (!TryResolveVisibleTarget(args, out var item) ||
            !TryComp<Content.Shared.Item.ItemComponent>(item, out var itemComponent) ||
            !_storage.TryGetStorageLocation((item, itemComponent), out var container, out _, out _) ||
            !CheckInteractionRange(actor, container.Owner, args))
        {
            Fail(actor, args, "vanilla-storage-item-unavailable");
            return;
        }

        state.Target = item;
        if (_hands.TryPickupAnyHand(actor, item) && _hands.IsHolding(actor, item) && !container.Contains(item))
        {
            Complete(actor, args);
            return;
        }

        Fail(actor, args, "vanilla-storage-retrieve-rejected");
    }

    private void ExecuteConsumeHeld(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (state.OperationAttempted)
        {
            // IngestedEvent is already owned by vanilla IngestionSystem for EdibleComponent. Poll the same
            // authoritative solution instead of registering an illegal duplicate component-event subscription.
            if (!state.Target.IsValid() || !Exists(state.Target) ||
                TryComp<EdibleComponent>(state.Target, out var currentEdible) &&
                _ingestion.EdibleVolume((state.Target, currentEdible)).Float() < state.InitialConsumableVolume)
            {
                Complete(actor, args);
            }
            return;
        }
        if (!TryResolveItemTarget(actor, state, args, out var item) || !_hands.IsHolding(actor, item) ||
            !TryComp<EdibleComponent>(item, out var edible) || !_ingestion.CanIngest(actor, item))
        {
            Fail(actor, args, "vanilla-consume-item-unavailable");
            return;
        }

        state.Target = item;
        state.InitialConsumableVolume = _ingestion.EdibleVolume((item, edible)).Float();
        // The action stays running until the vanilla solution volume changes. Starting a DoAfter is not success.
        state.OperationAttempted = true;
        if (!_ingestion.TryIngest(actor, item))
        {
            Fail(actor, args, "vanilla-consume-rejected");
        }
    }

    private void ExecuteFireHeldGun(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (!TryResolveVisibleTarget(args, out var target) || target == actor ||
            !CheckInteractionRange(actor, target, args) || !_gun.TryGetGun(actor, out var gun) ||
            !_hands.IsHolding(actor, gun.Owner))
        {
            Fail(actor, args, "vanilla-fire-target-unavailable");
            return;
        }

        state.Target = target;
        // AttemptShoot owns action blockers, ammo, firing cadence, projectile/hitscan creation and recoil.
        if (_gun.AttemptShoot(actor, gun, Transform(target).Coordinates, target))
        {
            Complete(actor, args);
            return;
        }

        Fail(actor, args, "vanilla-fire-rejected");
    }

    private void ExecuteReloadHeldGun(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (!TryResolveItemTarget(actor, state, args, out var ammunition) ||
            !_hands.IsHolding(actor, ammunition) || !TryFindSingleHeldGun(actor, ammunition, out var gun))
        {
            Fail(actor, args, "vanilla-reload-items-unavailable");
            return;
        }

        state.Target = ammunition;
        var before = GetAmmoCount(gun);
        if (!_hands.TrySelect(actor, ammunition) ||
            !_interaction.InteractUsing(actor, ammunition, gun, Transform(gun).Coordinates))
        {
            Fail(actor, args, "vanilla-reload-rejected");
            return;
        }

        var after = GetAmmoCount(gun);
        if (IsReloadConfirmed(before, after, Exists(ammunition) && _hands.IsHolding(actor, ammunition)))
        {
            Complete(actor, args);
            return;
        }

        Fail(actor, args, "vanilla-reload-unconfirmed");
    }

    private void ExecuteSpeech(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (args.Action.SpeechLocId.Length == 0)
        {
            Fail(actor, args, "vanilla-speech-line-missing");
            return;
        }
        if (state.SpeechConfirmed)
        {
            Complete(actor, args);
            return;
        }

        state.ExpectedSpeech = Loc.GetString(args.Action.SpeechLocId);
        _chat.TrySendInGameICMessage(actor, state.ExpectedSpeech, InGameICChatType.Speak,
            hideChat: false, hideLog: false);
        if (state.SpeechConfirmed)
            Complete(actor, args);
        else
            Fail(actor, args, "vanilla-speech-rejected");
    }

    private void ExecuteStartPulling(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (!TryResolveVisibleTarget(args, out var target) || !CheckInteractionRange(actor, target, args))
        {
            Fail(actor, args, "vanilla-pulling-target-unavailable");
            return;
        }
        state.Target = target;
        if (TryComp<PullerComponent>(actor, out var puller) && puller.Pulling == target ||
            _pulling.TryStartPull(actor, target) && TryComp(actor, out puller) && puller.Pulling == target)
        {
            Complete(actor, args);
            return;
        }
        Fail(actor, args, "vanilla-pulling-rejected");
    }

    private void ExecuteStopPulling(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args)
    {
        if (!TryComp<PullerComponent>(actor, out var puller) || puller.Pulling is not { } target)
        {
            Complete(actor, args);
            return;
        }
        state.Target = target;
        if (!TryComp<PullableComponent>(target, out var pullable) ||
            _pulling.TryStopPull(target, pullable, actor) && pullable.Puller != actor)
        {
            Complete(actor, args);
            return;
        }
        Fail(actor, args, "vanilla-stop-pulling-rejected");
    }

    private bool AcquireMovement(EntityUid actor, NCHumanVanillaActionComponent state)
    {
        if (_steeringLease.HasOtherOwner(actor, SteeringOwner))
            return false;
        if (!state.OwnsSteering)
        {
            if (!_steeringLease.Acquire(actor, SteeringOwner))
                return false;
            state.OwnsSteering = true;
        }
        if (!state.SuspendedHtn && TryComp<HTNComponent>(actor, out var htn) && htn.Enabled)
        {
            _htn.SetHTNEnabled((actor, htn), false);
            state.SuspendedHtn = true;
        }
        return true;
    }

    /// <summary>
    /// Entity-relative steering already follows a moving target. Registering the same goal again would submit an
    /// identical vanilla path request, so it is retained until the target changes or steering reports failure.
    /// </summary>
    private void RegisterEntitySteeringGoal(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        EntityUid target,
        float completionRange)
    {
        if (state.RegisteredSteeringTarget == target && HasUsableSteering(actor, state))
            return;

        var steering = _steering.Register(actor, new EntityCoordinates(target, Vector2.Zero));
        steering.Range = completionRange;
        steering.ArriveOnLineOfSight = false;
        state.RegisteredSteeringTarget = target;
        state.HasRegisteredSteeringDestination = false;
    }

    /// <summary>
    /// Static patrol goals are registered once. Dynamic flee goals may move, but only after both their authored
    /// refresh cadence and displacement threshold have been crossed.
    /// </summary>
    private void RegisterCoordinateSteeringGoal(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        EntityCoordinates destination,
        NCHumanActionPrototype action,
        NCActiveWorldTime now)
    {
        var mapDestination = _transform.ToMapCoordinates(destination);
        if (state.HasRegisteredSteeringDestination && HasUsableSteering(actor, state) &&
            state.RegisteredSteeringDestination.MapId == mapDestination.MapId)
        {
            var displacement = Vector2.DistanceSquared(
                state.RegisteredSteeringDestination.Position,
                mapDestination.Position);
            var threshold = action.SteeringRetargetDistance * action.SteeringRetargetDistance;
            var refreshDue = action.SteeringRefreshInterval <= TimeSpan.Zero ||
                             !now.TryElapsedSince(state.SteeringRegisteredAt, out var elapsed) ||
                             elapsed >= action.SteeringRefreshInterval;
            if (displacement <= threshold || !refreshDue)
                return;
        }

        var steering = _steering.Register(actor, destination);
        // Flee completion means separation from the threat, not arrival range around the projected destination.
        // Preserve the vanilla steering range there so the NPC does not consider a newly projected escape point reached.
        if (action.Operation != NCHumanActionOperationKind.FleeFromTarget)
            steering.Range = action.CompletionRange;
        steering.ArriveOnLineOfSight = false;
        state.RegisteredSteeringTarget = null;
        state.RegisteredSteeringDestination = mapDestination;
        state.HasRegisteredSteeringDestination = true;
        state.SteeringRegisteredAt = now;
    }

    private bool HasUsableSteering(EntityUid actor, NCHumanVanillaActionComponent state) =>
        state.OwnsSteering && TryComp<NPCSteeringComponent>(actor, out var steering) &&
        steering.Status != SteeringStatus.NoPath;

    private void StopSteeringGoal(EntityUid actor, NCHumanVanillaActionComponent state)
    {
        if (state.OwnsSteering && Exists(actor) && HasComp<NPCSteeringComponent>(actor))
            _steering.Unregister(actor);
        state.RegisteredSteeringTarget = null;
        state.RegisteredSteeringDestination = default;
        state.HasRegisteredSteeringDestination = false;
        state.SteeringRegisteredAt = default;
    }

    private bool TryResolveVisibleTarget(NCHumanActionExecutionEvent args, out EntityUid target)
    {
        target = EntityUid.Invalid;
        if (args.Target.Kind == NCCognitionReferenceKind.StableKey &&
            _perception.TryResolveSubjectHandle(args.ActorId, args.Target.StableId, args.Now, out target))
            return true;
        if (args.Target.Kind != NCCognitionReferenceKind.Actor ||
            !Guid.TryParse(args.Target.StableId, out var actorGuid) ||
            !_runtime.TryGetEntity(new NCActorId(actorGuid), out var candidate) ||
            !_perception.TryGetSnapshot(args.ActorId, args.Now, out var observations, out _ ) ||
            observations.All(value => value.EntityUid != candidate))
        {
            return false;
        }
        target = candidate;
        return true;
    }

    private bool TryResolveEntityCoordinates(NCMapCoordinates target, out EntityCoordinates coordinates)
    {
        coordinates = default;
        if (!_coordinates.TryResolve(target, out var mapCoordinates) ||
            !_maps.TryGetMap(mapCoordinates.MapId, out var mapUid) || mapUid is not { } map ||
            !_maps.TryFindGridAt(map, mapCoordinates.Position, out var gridUid, out _))
            return false;
        coordinates = _transform.ToCoordinates((gridUid, null), mapCoordinates);
        return true;
    }

    private bool IsReached(EntityUid actor, NCMapCoordinates target, float range) =>
        _coordinates.TryGetCoordinates(actor, out var current) && current.NetworkId == target.NetworkId &&
        current.Z == target.Z && Vector2.DistanceSquared(current.Position, target.Position) <= range * range;

    private static uint StableSeed(Guid actorId, uint cycle)
    {
        Span<byte> bytes = stackalloc byte[16];
        actorId.TryWriteBytes(bytes);
        var hash = 2166136261u;
        foreach (var value in bytes)
            hash = (hash ^ value) * 16777619u;
        return (hash ^ cycle) * 16777619u;
    }

    private bool TryResolveItemTarget(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCHumanActionExecutionEvent args,
        out EntityUid target)
    {
        // A target resolved from perception remains a bounded capability handle for this one action instance. This
        // is required for delayed unequip/equip completion after the item leaves the original sensory container.
        if (state.Target.IsValid() && Exists(state.Target))
        {
            target = state.Target;
            return true;
        }
        if (TryResolveVisibleTarget(args, out target))
            return true;
        target = EntityUid.Invalid;
        if (!Guid.TryParse(args.Target.StableId, out var itemGuid))
            return false;
        var itemId = new NCItemId(itemGuid);
        if (TryComp<HandsComponent>(actor, out var hands))
        {
            foreach (var held in _hands.EnumerateHeld((actor, hands)))
            {
                if (TryComp<NCCityPersistentItemAdapterComponent>(held, out var adapter) &&
                    adapter.ItemId == itemId)
                {
                    target = held;
                    return true;
                }
            }
        }
        var slots = _inventory.GetSlotEnumerator(actor);
        while (slots.NextItem(out var equipped, out _))
        {
            if (!TryComp<NCCityPersistentItemAdapterComponent>(equipped, out var adapter) ||
                adapter.ItemId != itemId)
                continue;
            target = equipped;
            return true;
        }
        return false;
    }

    private bool CheckInteractionRange(EntityUid actor, EntityUid target, NCHumanActionExecutionEvent args)
    {
        return args.Action.MaximumRange <= 0f ||
               _interaction.InRangeUnobstructed(actor, target, args.Action.MaximumRange);
    }

    private bool TryFindEquippedSlot(EntityUid actor, EntityUid item, out string slot)
    {
        var enumerator = _inventory.GetSlotEnumerator(actor);
        while (enumerator.NextItem(out var equipped, out var definition))
        {
            if (equipped != item)
                continue;
            slot = definition.Name;
            return true;
        }
        slot = string.Empty;
        return false;
    }

    private bool TryFindSingleHeldGun(EntityUid actor, EntityUid excluded, out EntityUid gun)
    {
        gun = EntityUid.Invalid;
        if (!TryComp<HandsComponent>(actor, out var hands))
            return false;

        foreach (var held in _hands.EnumerateHeld((actor, hands)))
        {
            if (held == excluded || !HasComp<GunComponent>(held))
                continue;
            // Ambiguous weapon selection must be resolved by another typed command, never guessed here.
            if (gun.IsValid())
                return false;
            gun = held;
        }
        return gun.IsValid();
    }

    private int GetAmmoCount(EntityUid gun)
    {
        var count = new GetAmmoCountEvent();
        RaiseLocalEvent(gun, ref count);
        return count.Count;
    }

    internal static bool IsReloadConfirmed(int ammunitionBefore, int ammunitionAfter, bool sourceStillHeld) =>
        ammunitionAfter > ammunitionBefore || !sourceStillHeld;

    private void OnSpoke(Entity<NCHumanVanillaActionComponent> entity, ref EntitySpokeEvent args)
    {
        if (entity.Comp.Operation == NCHumanActionOperationKind.Speak &&
            args.Source == entity.Owner && args.Message == entity.Comp.ExpectedSpeech)
        {
            entity.Comp.SpeechConfirmed = true;
        }
    }

    private void OnTreatmentChanged(Entity<NCMedicalBodyComponent> patient, ref NCTreatmentChangedEvent args)
    {
        if (!_treatmentOwners.Remove(args.ResolutionId, out var actor) ||
            !TryComp<NCHumanVanillaActionComponent>(actor, out var state) ||
            state.Operation != NCHumanActionOperationKind.UseHeldOnInjuredPerson ||
            state.Target != patient.Owner || state.TreatmentResolutionId != args.ResolutionId ||
            patient.Comp.MedicalRevision <= state.InitialMedicalRevision)
        {
            return;
        }

        // The NC medical resolver raises this only after it has accepted and applied the correlated mutation.
        state.TreatmentConfirmed = true;
    }

    private void OnInterrupted(NCHumanActionInterruptedEvent args)
    {
        if (args.EntityUid is not { } actor ||
            !TryComp<NCHumanVanillaActionComponent>(actor, out var state) ||
            state.InstanceId != args.InstanceId)
            return;
        Cleanup(actor, state);
    }

    private void Complete(EntityUid actor, NCHumanActionExecutionEvent args)
    {
        if (TryComp<NCHumanVanillaActionComponent>(actor, out var state))
            Cleanup(actor, state);
        args.Completed = true;
    }

    private void Fail(EntityUid actor, NCHumanActionExecutionEvent args, string failure)
    {
        if (TryComp<NCHumanVanillaActionComponent>(actor, out var state))
            Cleanup(actor, state);
        args.Failure = failure;
    }

    private void Cleanup(EntityUid actor, NCHumanVanillaActionComponent state, bool remove = true)
    {
        _owned.Remove(actor);
        if (state.TreatmentResolutionId.IsValid)
            _treatmentOwners.Remove(state.TreatmentResolutionId);
        if (Exists(actor))
        {
            foreach (var id in state.PendingDoAfterIds)
            {
                if (_doAfter.IsRunning(actor, id))
                    _doAfter.Cancel(actor, id);
            }
            state.PendingDoAfterIds.Clear();
            if (state.OwnsSteering)
            {
                StopSteeringGoal(actor, state);
                _steeringLease.Release(actor, SteeringOwner);
                state.OwnsSteering = false;
            }
            if (state.SuspendedHtn && TryComp<HTNComponent>(actor, out var htn))
            {
                _htn.SetHTNEnabled((actor, htn), true, htn.PlanCooldown);
                state.SuspendedHtn = false;
            }
            if (state.EnabledHtn && TryComp<HTNComponent>(actor, out var enabledHtn) && enabledHtn.Enabled)
            {
                _htn.SetHTNEnabled((actor, enabledHtn), false);
                state.EnabledHtn = false;
            }
            if (state.Operation == NCHumanActionOperationKind.CombatAuthority && state.Target.IsValid())
                _factions.DeAggroEntity(actor, state.Target);
            if (remove && HasComp<NCHumanVanillaActionComponent>(actor))
                RemComp<NCHumanVanillaActionComponent>(actor);
    }
}

    private void OnShutdown(Entity<NCHumanVanillaActionComponent> entity, ref ComponentShutdown args)
    {
        _owned.Remove(entity.Owner);
        if (entity.Comp.TreatmentResolutionId.IsValid)
            _treatmentOwners.Remove(entity.Comp.TreatmentResolutionId);
    }

    private void OnBindingChanged(NCCityActorBindingChangedEvent args)
    {
        if (!args.Bound)
            TryEndDefensiveReflex(args.ActorId);
    }

    private void StopAll()
    {
        foreach (var actor in _owned.ToArray())
        {
            if (TryComp<NCHumanVanillaActionComponent>(actor, out var state))
                Cleanup(actor, state);
        }
        _owned.Clear();
        _treatmentOwners.Clear();
        foreach (var actorId in _defensiveReflexes.Keys.ToArray())
            TryEndDefensiveReflex(actorId);
        _defensiveReflexes.Clear();
        _defensiveReflexExpiry.Clear();
    }

    private readonly record struct DefensiveReflexState(
        EntityUid Actor,
        NCActiveWorldTime ExpiresAt,
        long Sequence,
        bool SuspendedHtn);

    private readonly record struct DefensiveReflexExpiry(NCActorId ActorId, long Sequence);
}

internal enum NCHumanInteractionReceiptState : byte
{
    Pending,
    Completed,
    Failed,
}
