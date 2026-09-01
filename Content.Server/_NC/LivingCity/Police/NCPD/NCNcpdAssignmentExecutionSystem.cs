// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Affordances;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Cognition.Components;
using Content.Server._NC.LivingCity.Time;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Affordances;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>
/// Physically executes already-authorized dispatch assignments. Leaders own one bounded route while partners follow
/// the leader, preventing every officer in a unit from requesting an independent long-distance route.
/// </summary>
public sealed partial class NCNcpdAssignmentExecutionSystem : EntitySystem
{
    private static readonly ProtoId<NCNcpdPatrolPolicyPrototype> DefaultPolicy = "NCDefaultNcpdPatrolPolicy";
    private const string SteeringOwner = "ncpd-assignment-execution";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityAffordanceCatalogSystem _affordances = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCCityNpcSteeringLeaseSystem _steeringLease = default!;
    [Dependency] private NCCityPhysicalExecutionSystem _physical = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private HTNSystem _htn = default!;
    [Dependency] private NCNcpdCommandSystem _commands = default!;

    private readonly PriorityQueue<ScheduledOfficer, SchedulePriority> _queue = new();
    private readonly Dictionary<EntityUid, NCActiveWorldTime> _scheduled = [];
    private readonly HashSet<EntityUid> _steeringActors = [];
    private readonly HashSet<EntityUid> _suspendedHtn = [];
    private readonly List<NCCityAffordanceInfo> _candidateBuffer = [];
    private long _sequence;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCNcpdAssignmentAppliedEvent>(OnAssignmentApplied);
        SubscribeLocalEvent<NCNcpdAssignmentComponent, ComponentShutdown>(OnAssignmentShutdown);
        SubscribeLocalEvent<NCCityActorBindingChangedEvent>(OnBindingChanged);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Clear());
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_clock.TryGetCurrentTime(out _, out var now) ||
            !_prototypes.TryIndex(DefaultPolicy, out var policy) || policy == null)
            return;

        var dispatched = 0;
        while (dispatched < policy.MaximumUpdatesPerTick && TryDequeue(now, out var officer))
        {
            Evaluate(officer, policy, now);
            dispatched++;
        }
    }

    private void OnAssignmentApplied(NCNcpdAssignmentAppliedEvent args)
    {
        if (_clock.TryGetCurrentTime(out _, out var now))
            Schedule(args.EntityUid, now);
    }

    /// <summary>Hands locomotion back before a higher-priority professional action acquires its own steering lease.</summary>
    public void YieldToProfessionalAction(EntityUid officer) => StopSteering(officer);

    private void Evaluate(EntityUid officer, NCNcpdPatrolPolicyPrototype policy, NCActiveWorldTime now)
    {
        if (!Exists(officer) || !TryComp<NCNcpdAssignmentComponent>(officer, out var assignment) ||
            !TryComp<NCNcpdUnitMemberComponent>(officer, out var member) ||
            !_commands.TryGetActive(member.UnitId, out var command) || command.CommandId != assignment.CommandId)
        {
            Stop(officer);
            return;
        }

        // An observed crime action temporarily supersedes the route. Its own adapter obtains steering and returns
        // control after a factual outcome, without destroying the dispatch assignment.
        if (TryComp<NCHumanActionRuntimeComponent>(officer, out var action) &&
            action.State is NCHumanActionRuntimeState.Pending or NCHumanActionRuntimeState.Running &&
            action.Executor == NCHumanActionExecutorKind.PoliceAdapter)
        {
            assignment.ExecutionNote = $"Suspended by {action.ActionId}.";
            Schedule(officer, now.Advance(policy.EvaluationInterval));
            return;
        }

        if (member.Role != NCNcpdUnitRole.Leader)
            EvaluatePartner(officer, assignment, member, policy, now);
        else
            EvaluateLeader(officer, assignment, command, policy, now);
    }

    private void EvaluatePartner(EntityUid officer, NCNcpdAssignmentComponent assignment,
        NCNcpdUnitMemberComponent member, NCNcpdPatrolPolicyPrototype policy, NCActiveWorldTime now)
    {
        if (!_runtime.TryGetEntity(member.LeaderActorId, out var leader) || !Exists(leader))
        {
            StopSteering(officer);
            assignment.ExecutionNote = "Waiting for the physical unit leader.";
            Schedule(officer, now.Advance(policy.ReplanBackoff));
            return;
        }

        if (_transform.InRange(Transform(officer).Coordinates, Transform(leader).Coordinates, policy.FollowSpacing))
        {
            StopSteering(officer);
            assignment.ExecutionNote = "In formation with the unit leader.";
        }
        else if (BeginApproach(officer, new EntityCoordinates(leader, Vector2.Zero)))
        {
            assignment.ExecutionNote = "Following the unit leader.";
        }
        else
        {
            assignment.ExecutionNote = "Waiting for locomotion authority.";
        }
        Schedule(officer, now.Advance(policy.EvaluationInterval));
    }

    private void EvaluateLeader(EntityUid officer, NCNcpdAssignmentComponent assignment,
        NCNcpdCommandRecord command, NCNcpdPatrolPolicyPrototype policy, NCActiveWorldTime now)
    {
        if (TryComp<NPCSteeringComponent>(officer, out var steering) && steering.Status == SteeringStatus.NoPath)
        {
            StopSteering(officer);
            assignment.HasExecutionTarget = false;
            assignment.ReachedAt = null;
            assignment.Replans++;
            assignment.ExecutionNote = $"Route unavailable; replan {assignment.Replans}/{policy.MaximumReplans}.";
            if (assignment.Replans > policy.MaximumReplans)
            {
                _commands.TryAdvanceExecution(command.CommandId, NCNcpdCommandStatus.Failed,
                    "Physical route remained unavailable after bounded replans.");
                return;
            }
            Schedule(officer, now.Advance(policy.ReplanBackoff));
            return;
        }

        if (!assignment.HasExecutionTarget)
        {
            if (!TrySelectTarget(assignment, command, out var target))
            {
                assignment.Replans++;
                assignment.ExecutionNote = $"No safe semantic destination; retry {assignment.Replans}/{policy.MaximumReplans}.";
                if (assignment.Replans > policy.MaximumReplans)
                {
                    _commands.TryAdvanceExecution(command.CommandId, NCNcpdCommandStatus.Failed,
                        "No safe destination was generated for the assigned city zone.");
                    return;
                }
                Schedule(officer, now.Advance(policy.ReplanBackoff));
                return;
            }
            assignment.ExecutionTarget = target;
            assignment.HasExecutionTarget = true;
            assignment.ReachedAt = null;
        }

        if (IsReached(officer, assignment.ExecutionTarget, policy.ArrivalRange))
        {
            StopSteering(officer);
            assignment.Replans = 0;
            assignment.ReachedAt ??= now;
            if (assignment.Status is NCNcpdCommandStatus.Accepted or NCNcpdCommandStatus.EnRoute)
            {
                _commands.TryAdvanceExecution(command.CommandId, NCNcpdCommandStatus.Executing,
                    "Physical unit reached the assigned city zone.");
            }

            if (command.Kind == NCNcpdCommandKind.ReturnToStation)
            {
                _commands.TryAdvanceExecution(command.CommandId, NCNcpdCommandStatus.Completed,
                    "Unit physically returned to the station report area.");
                return;
            }

            if (command.Kind == NCNcpdCommandKind.PatrolZone &&
                assignment.ReachedAt is { } reached && now >= reached.Advance(PatrolDwell(command, policy)))
            {
                assignment.RouteCycle++;
                assignment.HasExecutionTarget = false;
                assignment.ReachedAt = null;
                assignment.ExecutionNote = "Patrol point covered; selecting the next safe point.";
                Schedule(officer, now);
                return;
            }

            assignment.ExecutionNote = command.Kind == NCNcpdCommandKind.PatrolZone
                ? "Covering the current patrol point."
                : "On scene; awaiting a factual professional outcome.";
            Schedule(officer, now.Advance(policy.EvaluationInterval));
            return;
        }

        if (assignment.Status == NCNcpdCommandStatus.Accepted)
            _commands.TryAdvanceExecution(command.CommandId, NCNcpdCommandStatus.EnRoute,
                "Physical unit is moving to the assigned city zone.");
        assignment.ExecutionNote = BeginApproach(officer, assignment.ExecutionTarget)
            ? "Moving to the assigned semantic destination."
            : "Waiting for locomotion authority.";
        Schedule(officer, now.Advance(policy.EvaluationInterval));
    }

    private bool TrySelectTarget(NCNcpdAssignmentComponent assignment, NCNcpdCommandRecord command,
        out NCMapCoordinates target)
    {
        target = default;
        var preferred = command.Kind switch
        {
            NCNcpdCommandKind.ReturnToStation => NCCityAffordanceKind.ReportDesk,
            NCNcpdCommandKind.PatrolZone => NCCityAffordanceKind.PatrolArea,
            NCNcpdCommandKind.InvestigateZone => NCCityAffordanceKind.PublicEntrance,
            _ => NCCityAffordanceKind.PatrolArea,
        };
        _affordances.Get(preferred, assignment.TargetZone, _candidateBuffer);
        if (_candidateBuffer.Count == 0 && preferred != NCCityAffordanceKind.Workplace)
            _affordances.Get(NCCityAffordanceKind.Workplace, assignment.TargetZone, _candidateBuffer);
        if (_candidateBuffer.Count == 0 && preferred != NCCityAffordanceKind.PublicEntrance)
            _affordances.Get(NCCityAffordanceKind.PublicEntrance, assignment.TargetZone, _candidateBuffer);
        if (_candidateBuffer.Count == 0)
            return false;

        var seed = StableSeed(command.CommandId.Value, assignment.RouteCycle);
        target = _candidateBuffer[(int) (seed % (uint) _candidateBuffer.Count)].Target;
        return _coordinates.TryResolve(target, out _);
    }

    private bool BeginApproach(EntityUid officer, NCMapCoordinates target) =>
        TryResolveEntityCoordinates(target, out var destination) && BeginApproach(officer, destination);

    private bool BeginApproach(EntityUid officer, EntityCoordinates destination)
    {
        if (_steeringLease.HasOtherOwner(officer, SteeringOwner) ||
            !_steeringLease.Acquire(officer, SteeringOwner))
            return false;
        if (_suspendedHtn.Add(officer) && TryComp<HTNComponent>(officer, out var htn) && htn.Enabled)
            _htn.SetHTNEnabled((officer, htn), false);
        _physical.SetInterrupt(officer, NCCityPhysicalInterruptFlags.Police, true);
        _steeringActors.Add(officer);
        _steering.Register(officer, destination);
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

    private bool IsReached(EntityUid officer, NCMapCoordinates target, float range) =>
        _coordinates.TryGetCoordinates(officer, out var current) && current.NetworkId == target.NetworkId &&
        current.Z == target.Z && Vector2.DistanceSquared(current.Position, target.Position) <= range * range;

    private void Schedule(EntityUid officer, NCActiveWorldTime due)
    {
        if (!Exists(officer) || !HasComp<NCNcpdAssignmentComponent>(officer))
            return;
        _scheduled[officer] = due;
        _queue.Enqueue(new ScheduledOfficer(officer, due), new SchedulePriority(due.Ticks, _sequence++));
        if (TryComp<NCNcpdAssignmentComponent>(officer, out var assignment))
            assignment.NextEvaluationAt = due;
    }

    private bool TryDequeue(NCActiveWorldTime now, out EntityUid officer)
    {
        officer = EntityUid.Invalid;
        while (_queue.TryPeek(out var item, out var priority) && priority.DueTicks <= now.Ticks)
        {
            _queue.Dequeue();
            if (!_scheduled.TryGetValue(item.Officer, out var current) || current != item.Due)
                continue;
            _scheduled.Remove(item.Officer);
            officer = item.Officer;
            return true;
        }
        return false;
    }

    private void OnAssignmentShutdown(Entity<NCNcpdAssignmentComponent> entity, ref ComponentShutdown args) =>
        Stop(entity.Owner);

    private void OnBindingChanged(NCCityActorBindingChangedEvent args)
    {
        if (!args.Bound)
            Stop(args.EntityUid, false);
    }

    private void Stop(EntityUid officer, bool restoreHtn = true)
    {
        _scheduled.Remove(officer);
        StopSteering(officer, restoreHtn);
    }

    private void StopSteering(EntityUid officer, bool restoreHtn = true)
    {
        if (_steeringActors.Remove(officer) && Exists(officer))
        {
            _steering.Unregister(officer);
            _steeringLease.Release(officer, SteeringOwner);
        }
        if (!restoreHtn || !Exists(officer))
        {
            _suspendedHtn.Remove(officer);
            return;
        }
        _physical.SetInterrupt(officer, NCCityPhysicalInterruptFlags.Police, false);
        if (_suspendedHtn.Remove(officer) && TryComp<HTNComponent>(officer, out var htn))
            _htn.SetHTNEnabled((officer, htn), true, htn.PlanCooldown);
    }

    private void Clear()
    {
        foreach (var officer in _scheduled.Keys.Concat(_steeringActors).Distinct().ToArray())
            Stop(officer, false);
        _scheduled.Clear();
        _queue.Clear();
        _steeringActors.Clear();
        _suspendedHtn.Clear();
    }

    private static TimeSpan PatrolDwell(NCNcpdCommandRecord command, NCNcpdPatrolPolicyPrototype policy)
    {
        if (policy.MaximumStreetDwell <= policy.MinimumStreetDwell)
            return policy.MinimumStreetDwell;
        var span = policy.MaximumStreetDwell - policy.MinimumStreetDwell;
        var fraction = StableSeed(command.CommandId.Value, 0) / (double) uint.MaxValue;
        return policy.MinimumStreetDwell + TimeSpan.FromTicks((long) (span.Ticks * fraction));
    }

    private static uint StableSeed(Guid id, uint cycle)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        var hash = 2166136261u;
        foreach (var value in bytes)
            hash = (hash ^ value) * 16777619u;
        return (hash ^ cycle) * 16777619u;
    }

    private readonly record struct ScheduledOfficer(EntityUid Officer, NCActiveWorldTime Due);

    private readonly record struct SchedulePriority(long DueTicks, long Sequence) : IComparable<SchedulePriority>
    {
        public int CompareTo(SchedulePriority other)
        {
            var due = DueTicks.CompareTo(other.DueTicks);
            return due != 0 ? due : Sequence.CompareTo(other.Sequence);
        }
    }
}
