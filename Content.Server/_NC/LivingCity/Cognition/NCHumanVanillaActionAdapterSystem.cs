// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Affordances;
using Content.Server._NC.LivingCity.Cognition.Components;
using Content.Server._NC.LivingCity.Time;
using Content.Server.Chat.Systems;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared.Chat;
using Content.Shared.CombatMode;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Affordances;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
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

    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityLocalPerceptionSystem _perception = default!;
    [Dependency] private NCCityNpcSteeringLeaseSystem _steeringLease = default!;
    [Dependency] private NCCityPhysicalExecutionSystem _physical = default!;
    [Dependency] private NCCityActorDematerializationSystem _dematerialization = default!;
    [Dependency] private NCCityAffordanceCatalogSystem _affordances = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private HTNSystem _htn = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private PullingSystem _pulling = default!;
    [Dependency] private ChatSystem _chat = default!;

    private readonly HashSet<EntityUid> _owned = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCHumanActionExecutionEvent>(OnExecute);
        SubscribeLocalEvent<NCHumanActionInterruptedEvent>(OnInterrupted);
        SubscribeLocalEvent<NCHumanVanillaActionComponent, EntitySpokeEvent>(OnSpoke);
        SubscribeLocalEvent<NCHumanVanillaActionComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => StopAll());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => StopAll());
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
        if (!AcquireMovement(actor, state, InterruptFor(args)))
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
        AcquireMovement(actor, state, InterruptFor(args));
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

        if (!AcquireMovement(actor, state, InterruptFor(args)) ||
            !TryResolveEntityCoordinates(state.TargetCoordinates, out var destination))
            return;
        RegisterCoordinateSteeringGoal(actor, state, destination, args.Action, args.Now);
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
        if (!AcquireMovement(actor, state, NCCityPhysicalInterruptFlags.Combat))
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
        if (!TryComp<NCCityLocalPerceptionComponent>(actor, out var perception))
        {
            Fail(actor, args, "vanilla-combat-perception-unavailable");
            return;
        }

        var combatRunning = perception.HostileCount > 0 ||
                            HasComp<NPCMeleeCombatComponent>(actor) ||
                            HasComp<NPCRangedCombatComponent>(actor) ||
                            TryComp<CombatModeComponent>(actor, out var combatMode) && combatMode.IsInCombatMode;
        if (!combatRunning)
        {
            Complete(actor, args);
            return;
        }

        if (state.Interrupt == NCCityPhysicalInterruptFlags.None)
        {
            _physical.SetInterrupt(actor, NCCityPhysicalInterruptFlags.Combat, true);
            state.Interrupt = NCCityPhysicalInterruptFlags.Combat;
        }
        if (!state.OwnsCombatBlocker &&
            _dematerialization.BeginBlocker(actor, NCCityDematerializationBlockerFlags.Combat))
        {
            state.OwnsCombatBlocker = true;
        }

        // The authored NCLivingHumanCompound selects vanilla ranged/melee/juke branches; this adapter never attacks.
        if (TryComp<HTNComponent>(actor, out var htn) && !htn.Enabled)
            _htn.SetHTNEnabled((actor, htn), true, htn.PlanCooldown);
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

    private bool AcquireMovement(
        EntityUid actor,
        NCHumanVanillaActionComponent state,
        NCCityPhysicalInterruptFlags interrupt)
    {
        if (state.Interrupt == NCCityPhysicalInterruptFlags.None)
        {
            _physical.SetInterrupt(actor, interrupt, true);
            state.Interrupt = interrupt;
        }
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

    private NCCityPhysicalInterruptFlags InterruptFor(NCHumanActionExecutionEvent args)
    {
        return args.Intention.CapabilityId switch
        {
            "NCMedicalRespondToCritical" or "NCCityMedicalRespondToCritical" =>
                NCCityPhysicalInterruptFlags.Medical,
            "NCCognitionConversation" or "NCCognitionApproachPerson" =>
                NCCityPhysicalInterruptFlags.Conversation,
            _ => NCCityPhysicalInterruptFlags.Scripted,
        };
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

    private void OnSpoke(Entity<NCHumanVanillaActionComponent> entity, ref EntitySpokeEvent args)
    {
        if (entity.Comp.Operation == NCHumanActionOperationKind.Speak &&
            args.Source == entity.Owner && args.Message == entity.Comp.ExpectedSpeech)
        {
            entity.Comp.SpeechConfirmed = true;
        }
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
        if (Exists(actor))
        {
            if (state.OwnsSteering)
            {
                StopSteeringGoal(actor, state);
                _steeringLease.Release(actor, SteeringOwner);
                state.OwnsSteering = false;
            }
            if (state.Interrupt != NCCityPhysicalInterruptFlags.None)
            {
                _physical.SetInterrupt(actor, state.Interrupt, false);
                state.Interrupt = NCCityPhysicalInterruptFlags.None;
            }
            if (state.OwnsCombatBlocker)
            {
                _dematerialization.EndBlocker(actor, NCCityDematerializationBlockerFlags.Combat);
                state.OwnsCombatBlocker = false;
            }
            if (state.SuspendedHtn && TryComp<HTNComponent>(actor, out var htn))
            {
                _htn.SetHTNEnabled((actor, htn), true, htn.PlanCooldown);
                state.SuspendedHtn = false;
            }
            if (remove && HasComp<NCHumanVanillaActionComponent>(actor))
                RemComp<NCHumanVanillaActionComponent>(actor);
        }
    }

    private void OnShutdown(Entity<NCHumanVanillaActionComponent> entity, ref ComponentShutdown args)
    {
        _owned.Remove(entity.Owner);
    }

    private void StopAll()
    {
        foreach (var actor in _owned.ToArray())
        {
            if (TryComp<NCHumanVanillaActionComponent>(actor, out var state))
                Cleanup(actor, state);
        }
        _owned.Clear();
    }
}
