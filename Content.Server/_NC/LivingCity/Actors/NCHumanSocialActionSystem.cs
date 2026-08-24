// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Time;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Robust.Shared.Map;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Executes universal human social movement selected by BDI. Neural output supplies only a perceived handle; this
/// system retains authority over entity resolution, locomotion ownership, collision-aware steering and completion.
/// </summary>
public sealed partial class NCHumanSocialActionSystem : EntitySystem
{
    private const string ApproachAction = "human-approach-person";
    private const string SteeringOwner = "human-social-action";

    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityLocalPerceptionSystem _perception = default!;
    [Dependency] private NCCityNpcSteeringLeaseSystem _steeringLease = default!;
    [Dependency] private NCCityPhysicalExecutionSystem _physicalExecution = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private HTNSystem _htn = default!;

    private readonly HashSet<EntityUid> _steeringActors = [];
    private readonly HashSet<EntityUid> _suspendedHtn = [];
    private readonly Dictionary<EntityUid, NCCityPhysicalInterruptFlags> _interrupts = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCHumanActionExecutionEvent>(OnExecuteStep);
        SubscribeLocalEvent<NCHumanActionInterruptedEvent>(OnInterrupted);
        SubscribeLocalEvent<NCHumanSocialActionComponent, ComponentShutdown>(OnActorShutdown);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => StopAll());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => StopAll());
    }

    private void OnExecuteStep(NCHumanActionExecutionEvent args)
    {
        if (args.Step.ActionId != ApproachAction)
            return;

        args.Handled = true;
        if (args.EntityUid is not { } actor || !Exists(actor) ||
            !TryComp<NCHumanSocialActionComponent>(actor, out var limits) ||
            !float.IsFinite(limits.ApproachRange) || limits.ApproachRange <= 0f ||
            !_clock.TryGetCurrentTime(out _, out var now) ||
            args.Intention.Target.Kind != Content.Shared._NC.LivingCity.Cognition.NCCognitionReferenceKind.StableKey ||
            !_perception.TryResolveSubjectHandle(args.ActorId, args.Intention.Target.StableId, now, out var target) ||
            target == actor)
        {
            if (args.EntityUid is { } invalidActor)
                Stop(invalidActor);
            args.Failure = "human-social-target-unavailable";
            return;
        }

        if (_transform.InRange(Transform(actor).Coordinates, Transform(target).Coordinates, limits.ApproachRange))
        {
            Stop(actor);
            args.Completed = true;
            return;
        }

        if (TryComp<NPCSteeringComponent>(actor, out var currentSteering) &&
            currentSteering.Status == SteeringStatus.NoPath)
        {
            Stop(actor);
            args.Failure = "human-social-target-unreachable";
            return;
        }
        if (_steeringLease.HasOtherOwner(actor, SteeringOwner))
        {
            Stop(actor);
            args.Failure = "human-social-locomotion-busy";
            return;
        }

        SuspendHtn(actor);
        var interrupt = args.Intention.CapabilityId == "NCMedicalRespondToCritical"
            ? NCCityPhysicalInterruptFlags.Medical
            : NCCityPhysicalInterruptFlags.Conversation;
        _physicalExecution.SetInterrupt(actor, interrupt, true);
        _interrupts[actor] = interrupt;
        if (!_steeringLease.Acquire(actor, SteeringOwner))
        {
            Stop(actor);
            args.Failure = "human-social-steering-unavailable";
            return;
        }
        _steeringActors.Add(actor);
        // Entity-relative steering follows a moving speaker while vanilla navigation owns path and collision checks.
        _steering.Register(actor, new EntityCoordinates(target, Vector2.Zero));
    }

    private void OnInterrupted(NCHumanActionInterruptedEvent args)
    {
        if (args.ActionId == ApproachAction && args.EntityUid is { } entity)
            Stop(entity);
    }

    private void OnActorShutdown(Entity<NCHumanSocialActionComponent> actor, ref ComponentShutdown args)
    {
        Stop(actor.Owner, false);
        _suspendedHtn.Remove(actor.Owner);
    }

    private void SuspendHtn(EntityUid actor)
    {
        if (_suspendedHtn.Contains(actor) || !TryComp<HTNComponent>(actor, out var htn) || !htn.Enabled)
            return;
        _htn.SetHTNEnabled((actor, htn), false);
        _suspendedHtn.Add(actor);
    }

    private void Stop(EntityUid actor, bool restoreHtn = true)
    {
        if (_steeringActors.Remove(actor) && Exists(actor))
        {
            _steering.Unregister(actor);
            _steeringLease.Release(actor, SteeringOwner);
        }
        var hadInterrupt = _interrupts.Remove(actor, out var interrupt);
        if (!restoreHtn || !Exists(actor))
            return;
        if (hadInterrupt)
            _physicalExecution.SetInterrupt(actor, interrupt, false);
        if (_suspendedHtn.Remove(actor) && TryComp<HTNComponent>(actor, out var htn))
            _htn.SetHTNEnabled((actor, htn), true, htn.PlanCooldown);
    }

    private void StopAll()
    {
        foreach (var actor in _steeringActors.ToArray())
            Stop(actor);
        _steeringActors.Clear();
        _suspendedHtn.Clear();
        _interrupts.Clear();
    }
}
