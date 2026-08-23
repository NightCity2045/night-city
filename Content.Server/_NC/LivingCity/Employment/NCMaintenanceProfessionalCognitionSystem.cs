// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Time;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Repairable;
using Content.Shared.Storage;
using Content.Shared.Tools.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Employment;

/// <summary>
/// Executes one real infrastructure-repair cycle for the common human planner. Target discovery is local and
/// bounded; movement, tool quality, fuel, DoAfter and damage mutation remain authoritative vanilla systems.
/// </summary>
public sealed partial class NCMaintenanceProfessionalCognitionSystem : EntitySystem
{
    private static readonly ProtoId<NCMaintenanceWorkPolicyPrototype> DefaultPolicy =
        "NCDefaultMaintenanceWorkPolicy";
    private const string SteeringOwner = "living-city-maintenance-work";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedToolSystem _tools = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private NCCityNpcSteeringLeaseSystem _steeringLease = default!;
    [Dependency] private NCCityPhysicalExecutionSystem _physical = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private HTNSystem _htn = default!;

    private readonly Dictionary<EntityUid, RepairWork> _work = [];
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
            return;
        args.Handled = true;
        if (!_clock.TryGetCurrentTime(out _, out var now) || !Exists(args.Worker))
        {
            args.Failure = "maintenance-worker-or-clock-unavailable";
            return;
        }

        if (_work.TryGetValue(args.Worker, out var active) && active.Started)
        {
            if (!Exists(active.Target) || !TryComp<DamageableComponent>(active.Target, out var damageable))
            {
                Stop(args.Worker);
                args.Completed = true;
                return;
            }
            var current = _damage.GetTotalDamage((active.Target, damageable));
            if (current < active.InitialDamage)
            {
                Stop(args.Worker);
                args.Completed = true;
                return;
            }
            if (now >= active.ExpiresAt)
            {
                Stop(args.Worker);
                args.Failure = "maintenance-repair-interrupted";
            }
            return;
        }

        if (!_work.TryGetValue(args.Worker, out active) || !IsDamagedRepairable(active.Target))
        {
            Stop(args.Worker);
            if (!TryFindTarget(args.Worker, policy, out var target, out var initialDamage))
            {
                args.Completed = true;
                return;
            }
            active = new RepairWork(target, initialDamage, false, default);
            _work[args.Worker] = active;
        }

        if (!_transform.InRange(Transform(args.Worker).Coordinates, Transform(active.Target).Coordinates,
                policy.InteractionRange))
        {
            if (TryComp<NPCSteeringComponent>(args.Worker, out var current) && current.Status == SteeringStatus.NoPath)
            {
                Stop(args.Worker);
                args.Failure = "maintenance-target-unreachable";
                return;
            }
            if (!BeginApproach(args.Worker, active.Target))
                args.Failure = "maintenance-locomotion-busy";
            return;
        }

        StopMovement(args.Worker);
        var repairable = Comp<RepairableComponent>(active.Target);
        if (!TryReadyTool(args.Worker, repairable.QualityNeeded.Id, out var tool))
        {
            Stop(args.Worker);
            args.Failure = "maintenance-required-tool-unavailable";
            return;
        }
        if (!_interaction.InteractUsing(args.Worker, tool, active.Target, Transform(active.Target).Coordinates))
        {
            Stop(args.Worker);
            args.Failure = "maintenance-repair-rejected";
            return;
        }
        _work[args.Worker] = active with { Started = true, ExpiresAt = now.Advance(policy.CompletionTimeout) };
    }

    private bool TryFindTarget(EntityUid worker, NCMaintenanceWorkPolicyPrototype policy, out EntityUid target,
        out FixedPoint2 damage)
    {
        target = EntityUid.Invalid;
        damage = FixedPoint2.Zero;
        var origin = _transform.GetMapCoordinates(worker);
        var nearest = float.MaxValue;
        var checkedCandidates = 0;
        foreach (var candidate in _lookup.GetEntitiesInRange<RepairableComponent>(origin, policy.SearchRadius))
        {
            if (++checkedCandidates > policy.MaximumCandidates)
                break;
            if (!TryComp<DamageableComponent>(candidate.Owner, out var damageable))
                continue;
            var total = _damage.GetTotalDamage((candidate.Owner, damageable));
            if (total <= 0f)
                continue;
            var coordinates = _transform.GetMapCoordinates(candidate.Owner);
            var distance = Vector2.DistanceSquared(origin.Position, coordinates.Position);
            if (distance >= nearest)
                continue;
            nearest = distance;
            target = candidate.Owner;
            damage = total;
        }
        return target.IsValid();
    }

    private bool IsDamagedRepairable(EntityUid target) => target.IsValid() && Exists(target) &&
        HasComp<RepairableComponent>(target) && TryComp<DamageableComponent>(target, out var damageable) &&
        _damage.GetTotalDamage((target, damageable)) > 0f;

    private bool TryReadyTool(EntityUid worker, string quality, out EntityUid tool)
    {
        foreach (var held in _hands.EnumerateHeld(worker))
        {
            if (!_tools.HasQuality(held, quality))
                continue;
            tool = held;
            return true;
        }
        if (_inventory.TryGetSlotEntity(worker, "belt", out var belt) &&
            TryComp<StorageComponent>(belt, out var storage))
        {
            foreach (var stored in storage.Container.ContainedEntities.ToArray())
            {
                if (_tools.HasQuality(stored, quality) && _hands.TryPickupAnyHand(worker, stored))
                {
                    tool = stored;
                    return true;
                }
            }
        }
        tool = EntityUid.Invalid;
        return false;
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

    private void StopMovement(EntityUid worker)
    {
        if (_steeringActors.Remove(worker) && Exists(worker))
        {
            _steering.Unregister(worker);
            _steeringLease.Release(worker, SteeringOwner);
        }
    }

    private void OnActorShutdown(Entity<NCCityActorBindingComponent> actor, ref ComponentShutdown args) =>
        Stop(actor.Owner, false);

    private void Stop(EntityUid worker, bool restoreHtn = true)
    {
        _work.Remove(worker);
        StopMovement(worker);
        if (!restoreHtn || !Exists(worker))
        {
            _suspendedHtn.Remove(worker);
            return;
        }
        _physical.SetInterrupt(worker, NCCityPhysicalInterruptFlags.Work, false);
        if (_suspendedHtn.Remove(worker) && TryComp<HTNComponent>(worker, out var htn))
            _htn.SetHTNEnabled((worker, htn), true, htn.PlanCooldown);
    }

    private readonly record struct RepairWork(
        EntityUid Target,
        FixedPoint2 InitialDamage,
        bool Started,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime ExpiresAt);
}
