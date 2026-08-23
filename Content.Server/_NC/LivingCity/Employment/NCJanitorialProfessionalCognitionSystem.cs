// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared.Fluids;
using Content.Shared.Fluids.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Employment;

/// <summary>
/// Makes a Zhirafa janitor perform real, visible work without creating a second NPC brain. The common cognition
/// pipeline asks for professional work; this adapter only finds a bounded nearby puddle, walks to it and uses the
/// same held mop and interaction event as a player.
/// </summary>
public sealed partial class NCJanitorialProfessionalCognitionSystem : EntitySystem
{
    private static readonly ProtoId<NCJanitorialWorkPolicyPrototype> DefaultPolicy =
        "NCDefaultJanitorialWorkPolicy";
    private const string SteeringOwner = "living-city-janitorial-work";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private NCCityNpcSteeringLeaseSystem _steeringLease = default!;
    [Dependency] private NCCityPhysicalExecutionSystem _physical = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private HTNSystem _htn = default!;

    private readonly Dictionary<EntityUid, EntityUid> _targets = [];
    private readonly HashSet<EntityUid> _steeringActors = [];
    private readonly HashSet<EntityUid> _suspendedHtn = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCProfessionalWorkRequestedEvent>(OnProfessionalWork);
        SubscribeLocalEvent<NCCityActorBindingComponent, ComponentShutdown>(OnActorShutdown);
    }

    private void OnProfessionalWork(NCProfessionalWorkRequestedEvent args)
    {
        if (!_prototypes.TryIndex(DefaultPolicy, out var policy) ||
            !args.Profile.CapabilityOverlays.Contains(policy.Capability))
        {
            return;
        }

        args.Handled = true;
        var worker = args.Worker;
        if (!Exists(worker))
        {
            args.Failure = "janitorial-worker-unavailable";
            return;
        }

        if (!_targets.TryGetValue(worker, out var target) || !Exists(target) || !HasComp<PuddleComponent>(target))
        {
            Stop(worker);
            if (!TryFindPuddle(worker, policy, out target))
            {
                // Inspecting a bounded assigned area and finding it clean is a legitimate completed work cycle.
                args.Completed = true;
                return;
            }
            _targets[worker] = target;
        }

        if (!_transform.InRange(Transform(worker).Coordinates, Transform(target).Coordinates,
                policy.InteractionRange))
        {
            if (TryComp<NPCSteeringComponent>(worker, out var current) && current.Status == SteeringStatus.NoPath)
            {
                Stop(worker);
                args.Failure = "janitorial-target-unreachable";
                return;
            }
            if (!BeginApproach(worker, target))
                args.Failure = "janitorial-locomotion-busy";
            return;
        }

        Stop(worker);
        if (!TryGetHeldMop(worker, out var mop))
        {
            args.Failure = "janitorial-mop-unavailable";
            return;
        }

        // InteractUsing reaches AbsorbentSystem through the same event sequence and authority checks as player input.
        var handled = _interaction.InteractUsing(
            worker,
            mop,
            target,
            Transform(target).Coordinates,
            checkCanInteract: true);
        _targets.Remove(worker);
        if (!handled)
        {
            args.Failure = "janitorial-cleaning-rejected";
            return;
        }
        args.Completed = true;
    }

    private bool TryFindPuddle(EntityUid worker, NCJanitorialWorkPolicyPrototype policy, out EntityUid target)
    {
        target = EntityUid.Invalid;
        var origin = _transform.GetMapCoordinates(worker);
        var nearestDistance = float.MaxValue;
        var checkedCandidates = 0;
        foreach (var candidate in _lookup.GetEntitiesInRange<PuddleComponent>(origin, policy.SearchRadius))
        {
            if (++checkedCandidates > policy.MaximumCandidates || !Exists(candidate.Owner))
                break;
            var coordinates = _transform.GetMapCoordinates(candidate.Owner);
            var distance = Vector2.DistanceSquared(origin.Position, coordinates.Position);
            if (distance >= nearestDistance)
                continue;
            nearestDistance = distance;
            target = candidate.Owner;
        }
        return target.IsValid();
    }

    private bool BeginApproach(EntityUid worker, EntityUid target)
    {
        if (_steeringLease.HasOtherOwner(worker, SteeringOwner) || !_steeringLease.Acquire(worker, SteeringOwner))
            return false;
        if (_suspendedHtn.Add(worker) && TryComp<HTNComponent>(worker, out var htn) && htn.Enabled)
            _htn.SetHTNEnabled((worker, htn), false);
        _physical.SetInterrupt(worker, NCCityPhysicalInterruptFlags.Work, true);
        _steeringActors.Add(worker);
        _steering.Register(worker, new EntityCoordinates(target, Vector2.Zero));
        return true;
    }

    private bool TryGetHeldMop(EntityUid worker, out EntityUid mop)
    {
        foreach (var held in _hands.EnumerateHeld(worker))
        {
            if (!HasComp<AbsorbentComponent>(held))
                continue;
            mop = held;
            return true;
        }
        mop = EntityUid.Invalid;
        return false;
    }

    private void OnActorShutdown(Entity<NCCityActorBindingComponent> actor, ref ComponentShutdown args) =>
        Stop(actor.Owner, false);

    private void Stop(EntityUid worker, bool restoreHtn = true)
    {
        _targets.Remove(worker);
        if (_steeringActors.Remove(worker) && Exists(worker))
        {
            _steering.Unregister(worker);
            _steeringLease.Release(worker, SteeringOwner);
        }
        if (!restoreHtn || !Exists(worker))
        {
            _suspendedHtn.Remove(worker);
            return;
        }
        _physical.SetInterrupt(worker, NCCityPhysicalInterruptFlags.Work, false);
        if (_suspendedHtn.Remove(worker) && TryComp<HTNComponent>(worker, out var htn))
            _htn.SetHTNEnabled((worker, htn), true, htn.PlanCooldown);
    }
}
