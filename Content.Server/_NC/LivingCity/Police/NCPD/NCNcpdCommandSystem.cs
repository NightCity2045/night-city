// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>
/// Owns round-local operational orders. The console is only an input surface; authority, rank and zone checks happen
/// here before an assignment is projected onto physical officers.
/// </summary>
public sealed partial class NCNcpdCommandSystem : EntitySystem
{
    private static readonly ProtoId<DepartmentPrototype> NcpdDepartment = "NCPD";
    private static readonly ProtoId<NCNcpdDutyPolicyPrototype> DefaultDutyPolicy = "NCDefaultNcpdDutyPolicy";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCNcpdUnitSystem _units = default!;
    [Dependency] private NCNcpdDutySystem _duty = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;

    private readonly Dictionary<NCNcpdCommandId, NCNcpdCommandRecord> _commands = [];
    private readonly Dictionary<NCNcpdUnitId, NCNcpdCommandId> _activeByUnit = [];
    private readonly List<NCNcpdCommandId> _history = [];

    public int ActiveCommandCount => _activeByUnit.Count;
    public int RetainedCommandCount => _commands.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityActorMaterializationChangedEvent>(OnMaterializationChanged);
        SubscribeLocalEvent<NCNcpdUnitMembershipAppliedEvent>(OnUnitMembershipApplied);
        SubscribeLocalEvent<NCNcpdUnitRemovedEvent>(OnUnitRemoved);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => ClearRound());
    }

    public bool TryGetActive(NCNcpdUnitId unitId, out NCNcpdCommandRecord command)
    {
        if (_activeByUnit.TryGetValue(unitId, out var commandId) && _commands.TryGetValue(commandId, out command!))
            return true;
        command = default!;
        return false;
    }

    public void GetCommands(List<NCNcpdCommandRecord> result)
    {
        result.Clear();
        foreach (var commandId in _history.AsEnumerable().Reverse())
        {
            if (_commands.TryGetValue(commandId, out var command))
                result.Add(command);
        }
    }

    public bool TryIssue(
        NCNcpdIssuerContext issuer,
        NCNcpdCommandAuthorityPrototype authority,
        NCNcpdCommandPolicyPrototype policy,
        NCNcpdUnitId unitId,
        NCNcpdCommandKind kind,
        NCZoneId requestedZone,
        int priority,
        string reason,
        out string error)
    {
        error = string.Empty;
        if (!_clock.TryGetCurrentTime(out _, out var now))
            return Reject("The active round is not ready.", out error);
        if (!authority.AllowedCommands.Contains(kind))
            return Reject("Your current position cannot issue this order.", out error);
        if (priority is < 0 || priority > authority.MaximumPriority)
            return Reject($"Priority must be between 0 and {authority.MaximumPriority}.", out error);
        reason = reason.Trim();
        if (reason.Length is < 1 || reason.Length > policy.MaximumReasonLength)
            return Reject($"Give an operational reason (maximum {policy.MaximumReasonLength} characters).", out error);
        if (!_units.TryGetSnapshot(unitId, out var unit))
            return Reject("That unit is no longer available.", out error);
        if (_activeByUnit.ContainsKey(unitId) && kind != NCNcpdCommandKind.StandDown)
            return Reject("The unit already has an active order. Cancel or stand it down first.", out error);
        if (!CanCommandUnit(issuer, authority, unitId))
            return Reject("Your position cannot command this unit's current leader.", out error);

        if (kind == NCNcpdCommandKind.StandDown)
        {
            if (!_activeByUnit.TryGetValue(unitId, out var active))
                return Reject("The unit has no active order.", out error);
            return TryCancel(issuer, authority, policy, active, reason, out error);
        }

        var targetZone = requestedZone;
        if (kind == NCNcpdCommandKind.ReturnToStation)
        {
            if (!_prototypes.TryIndex(DefaultDutyPolicy, out NCNcpdDutyPolicyPrototype? duty) || duty == null)
                return Reject("NCPD station policy is unavailable.", out error);
            targetZone = duty.StationZone;
        }

        if (!targetZone.IsValid || !_zones.TryGetZone(targetZone, out var zone) ||
            !authority.AllowedZoneSlots.Contains(zone.ContextSlot))
            return Reject("Select a city zone permitted for this order.", out error);
        if (_activeByUnit.Count >= policy.MaximumActiveCommands)
            return Reject("The dispatch board has reached its active-order limit.", out error);

        var commandId = new NCNcpdCommandId(Guid.NewGuid());
        var command = new NCNcpdCommandRecord(
            commandId,
            unitId,
            kind,
            targetZone,
            priority,
            issuer.CharacterId,
            issuer.Name,
            issuer.Job,
            reason,
            NCNcpdCommandStatus.Accepted,
            now,
            "Accepted by dispatch; awaiting the physical patrol executor.");
        _commands.Add(commandId, command);
        _activeByUnit.Add(unitId, commandId);
        _history.Add(commandId);
        TrimHistory(policy.RetainedHistory);
        _duty.SetCommandAssignment(unit.Members, true);
        ApplyToUnit(unit, command);
        RaiseLocalEvent(new NCNcpdCommandChangedEvent(commandId, unitId, command.Status));
        return true;
    }

    public bool TryCancel(
        NCNcpdIssuerContext issuer,
        NCNcpdCommandAuthorityPrototype authority,
        NCNcpdCommandPolicyPrototype policy,
        NCNcpdCommandId commandId,
        string reason,
        out string error)
    {
        error = string.Empty;
        reason = reason.Trim();
        if (!_commands.TryGetValue(commandId, out var command) || !_activeByUnit.ContainsKey(command.UnitId))
            return Reject("That order is no longer active.", out error);
        if (reason.Length is < 1 || reason.Length > policy.MaximumReasonLength)
            return Reject($"Give a cancellation reason (maximum {policy.MaximumReasonLength} characters).", out error);
        if (command.IssuerCharacterId != issuer.CharacterId && !authority.CanCancelOthers)
            return Reject("Your position cannot cancel another commander's order.", out error);
        if (!CanCommandUnit(issuer, authority, command.UnitId))
            return Reject("Your position cannot stand down this unit.", out error);

        var cancelled = command with
        {
            Status = NCNcpdCommandStatus.Cancelled,
            Note = $"Cancelled by {issuer.Name}: {reason}",
        };
        _commands[commandId] = cancelled;
        _activeByUnit.Remove(command.UnitId);
        if (_units.TryGetSnapshot(command.UnitId, out var unit))
            _duty.SetCommandAssignment(unit.Members, false);
        ClearUnitAssignment(command.UnitId);
        TrimHistory(policy.RetainedHistory);
        RaiseLocalEvent(new NCNcpdCommandChangedEvent(commandId, command.UnitId, cancelled.Status));
        return true;
    }

    public bool CanCommandUnit(
        NCNcpdIssuerContext issuer,
        NCNcpdCommandAuthorityPrototype authority,
        NCNcpdUnitId unitId)
    {
        if (authority.CanCommandAllUnits)
            return true;
        if (!_units.TryGetLeaderJob(unitId, out var leaderJob) ||
            !_prototypes.TryIndex(NcpdDepartment, out DepartmentPrototype? department) || department == null ||
            !department.NCPositionRanks.TryGetValue(leaderJob, out var leaderRank))
            return false;
        return leaderRank < issuer.Rank || authority.CanCommandEqualRank && leaderRank == issuer.Rank;
    }

    private void ApplyToUnit(NCNcpdUnitSnapshot unit, NCNcpdCommandRecord command)
    {
        foreach (var actorId in unit.Members)
        {
            if (_runtime.TryGetEntity(actorId, out var entityUid))
                ApplyPhysical(entityUid, command);
        }
    }

    private void ApplyPhysical(EntityUid entityUid, NCNcpdCommandRecord command)
    {
        if (!Exists(entityUid))
            return;
        var assignment = EnsureComp<NCNcpdAssignmentComponent>(entityUid);
        assignment.CommandId = command.CommandId;
        assignment.Kind = command.Kind;
        assignment.TargetZone = command.TargetZone;
        assignment.Priority = command.Priority;
        assignment.Status = command.Status;
        var claim = EnsureComp<NCNcpdPoliceClaimComponent>(entityUid);
        claim.AssignmentId = command.CommandId.Value;
    }

    private void OnMaterializationChanged(NCCityActorMaterializationChangedEvent args)
    {
        if (!args.Materialized || !TryComp<NCNcpdUnitMemberComponent>(args.EntityUid, out var member) ||
            !TryGetActive(member.UnitId, out var command))
            return;
        ApplyPhysical(args.EntityUid, command);
    }

    private void OnUnitMembershipApplied(NCNcpdUnitMembershipAppliedEvent args)
    {
        if (TryGetActive(args.UnitId, out var command))
            ApplyPhysical(args.EntityUid, command);
    }

    private void OnUnitRemoved(NCNcpdUnitRemovedEvent args)
    {
        if (!_activeByUnit.Remove(args.UnitId, out var commandId) || !_commands.TryGetValue(commandId, out var command))
            return;
        _duty.SetCommandAssignment(args.Members, false);
        var failed = command with
        {
            Status = NCNcpdCommandStatus.Failed,
            Note = "Unit dissolved because its duty composition changed.",
        };
        _commands[commandId] = failed;
        RaiseLocalEvent(new NCNcpdCommandChangedEvent(commandId, args.UnitId, failed.Status));
    }

    private void ClearUnitAssignment(NCNcpdUnitId unitId)
    {
        if (!_units.TryGetSnapshot(unitId, out var unit))
            return;
        foreach (var actorId in unit.Members)
        {
            if (_runtime.TryGetEntity(actorId, out var entityUid))
            {
                RemComp<NCNcpdAssignmentComponent>(entityUid);
                if (TryComp<NCNcpdPoliceClaimComponent>(entityUid, out var claim))
                    claim.AssignmentId = Guid.Empty;
            }
        }
    }

    private void TrimHistory(int retained)
    {
        retained = Math.Max(0, retained);
        while (_history.Count > retained)
        {
            // Preserve every active order, but allow an older completed order to be evicted
            // even when another unit's long-running order is at the head of the history.
            var removableIndex = _history.FindIndex(commandId =>
                !_commands.TryGetValue(commandId, out var command) ||
                !_activeByUnit.TryGetValue(command.UnitId, out var activeId) ||
                activeId != commandId);
            if (removableIndex < 0)
                break;
            var removable = _history[removableIndex];
            _history.RemoveAt(removableIndex);
            _commands.Remove(removable);
        }
    }

    private void ClearRound()
    {
        foreach (var unitId in _activeByUnit.Keys.ToArray())
        {
            if (_units.TryGetSnapshot(unitId, out var unit))
                _duty.SetCommandAssignment(unit.Members, false);
            ClearUnitAssignment(unitId);
        }
        _activeByUnit.Clear();
        _commands.Clear();
        _history.Clear();
    }

    private static bool Reject(string message, out string error)
    {
        error = message;
        return false;
    }
}
