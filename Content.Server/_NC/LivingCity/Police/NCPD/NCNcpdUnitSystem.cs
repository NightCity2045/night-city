// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>
/// Forms deterministic patrol units from available officers and keeps valid groups intact across evaluations.
/// </summary>
public sealed partial class NCNcpdUnitSystem : EntitySystem
{
    private static readonly ProtoId<NCNcpdDutyPolicyPrototype> DefaultPolicy = "NCDefaultNcpdDutyPolicy";
    private static readonly ProtoId<DepartmentPrototype> NcpdDepartment = "NCPD";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCNcpdWorkforceProjectionSystem _officers = default!;
    [Dependency] private NCNcpdDutySystem _duty = default!;

    private readonly Dictionary<NCNcpdUnitId, UnitState> _units = [];
    private readonly Dictionary<NCActorId, NCNcpdUnitId> _unitByActor = [];
    private readonly List<NCNcpdDutySnapshot> _dutyBuffer = [];
    private bool _reconciling;

    public int UnitCount => _units.Count;
    public int AssignedOfficerCount => _unitByActor.Count;
    public int CadetsWithoutSupervisor { get; private set; }
    public string? ConfigurationError { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCNcpdDutyChangedEvent>(OnDutyChanged);
        SubscribeLocalEvent<NCNcpdOfficerProjectedEvent>(OnOfficerProjected);
        SubscribeLocalEvent<NCCityActorBindingChangedEvent>(OnBindingChanged);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(_ => Reconcile());
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => ClearUnits());
        Reconcile();
    }

    public void GetSnapshots(List<NCNcpdUnitSnapshot> result)
    {
        result.Clear();
        foreach (var unit in _units.Values.OrderBy(static value => value.UnitId.Value))
            result.Add(new NCNcpdUnitSnapshot(unit.UnitId, unit.Leader, unit.Members.ToArray()));
    }

    public bool TryGetSnapshot(NCNcpdUnitId unitId, out NCNcpdUnitSnapshot snapshot)
    {
        if (_units.TryGetValue(unitId, out var unit))
        {
            snapshot = new NCNcpdUnitSnapshot(unit.UnitId, unit.Leader, unit.Members.ToArray());
            return true;
        }

        snapshot = default!;
        return false;
    }

    public bool TryGetLeaderJob(NCNcpdUnitId unitId, out ProtoId<JobPrototype> job)
    {
        if (_units.TryGetValue(unitId, out var unit) &&
            _officers.TryGetRoleProfile(unit.Leader, out _, out var profile))
        {
            job = profile.Job;
            return true;
        }

        job = default;
        return false;
    }

    private void OnDutyChanged(NCNcpdDutyChangedEvent args) => Reconcile();
    private void OnOfficerProjected(NCNcpdOfficerProjectedEvent args) => Reconcile();

    private void OnBindingChanged(NCCityActorBindingChangedEvent args)
    {
        if (args.Bound)
            ApplyPhysical(args.ActorId, args.EntityUid);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<NCNcpdDutyPolicyPrototype>() || args.WasModified<NCNcpdRoleProfilePrototype>())
            Reconcile();
    }

    private void Reconcile()
    {
        if (_reconciling)
            return;
        _reconciling = true;
        try
        {
            if (!_prototypes.TryIndex(DefaultPolicy, out NCNcpdDutyPolicyPrototype? policy) || policy == null ||
                policy.PreferredPatrolSize < 2 || policy.MaximumPatrolSize < policy.PreferredPatrolSize ||
                policy.ReserveActors < 0)
            {
                ConfigurationError = $"NCPD duty policy {DefaultPolicy} is missing or invalid.";
                ClearUnits();
                return;
            }

            ConfigurationError = null;
            _duty.GetSnapshots(_dutyBuffer);
            var eligible = new Dictionary<NCActorId, OfficerCandidate>();
            foreach (var state in _dutyBuffer)
            {
                if (state.Status is not (NCNcpdDutyStatus.Available or NCNcpdDutyStatus.Assigned) ||
                    !_officers.TryGetRoleProfile(state.ActorId, out _, out var role) ||
                    !role.AllowedAssignments.Contains(NCNcpdAssignmentKind.Patrol) &&
                    !(role.MayWorkAlone && role.AllowedAssignments.Any(kind => kind is
                        NCNcpdAssignmentKind.Response or NCNcpdAssignmentKind.Investigation)))
                {
                    continue;
                }

                eligible[state.ActorId] = new OfficerCandidate(state.ActorId, role);
            }

            PreserveValidUnits(eligible, policy);
            ReleasePartnersForWaitingCadets(eligible);
            var available = eligible.Values
                .Where(candidate => !_unitByActor.ContainsKey(candidate.ActorId))
                .OrderByDescending(candidate => GetRank(candidate.Profile.Job))
                .ThenBy(static candidate => candidate.ActorId.Value)
                .ToList();

            // Specialist roles such as Inspector are valid one-person units and must not be excluded merely because
            // they do not perform ordinary street patrols.
            foreach (var specialist in available.Where(candidate => candidate.Profile.MayWorkAlone &&
                         !candidate.Profile.AllowedAssignments.Contains(NCNcpdAssignmentKind.Patrol)).ToArray())
            {
                CreateSoloUnit(policy.StationZone, specialist);
                available.Remove(specialist);
            }

            var usable = Math.Max(0, available.Count - policy.ReserveActors);
            while (usable >= 2)
            {
                var leaderIndex = available.FindIndex(static candidate =>
                    candidate.Profile.CanLead && candidate.Profile.OfficerRole != NCNcpdOfficerRole.Cadet);
                if (leaderIndex < 0)
                    break;

                var leader = available[leaderIndex];
                available.RemoveAt(leaderIndex);
                usable--;

                var partnerIndex = available.FindIndex(static candidate =>
                    candidate.Profile.OfficerRole == NCNcpdOfficerRole.Cadet);
                if (partnerIndex < 0)
                    partnerIndex = 0;
                var partner = available[partnerIndex];
                available.RemoveAt(partnerIndex);
                usable--;

                CreateUnit(policy.StationZone, leader, partner);
            }

            CadetsWithoutSupervisor = available.Count(static candidate =>
                candidate.Profile.OfficerRole == NCNcpdOfficerRole.Cadet);
            ApplyAllPhysical();
        }
        finally
        {
            _reconciling = false;
        }
    }

    private void PreserveValidUnits(
        IReadOnlyDictionary<NCActorId, OfficerCandidate> eligible,
        NCNcpdDutyPolicyPrototype policy)
    {
        foreach (var unit in _units.Values.ToArray())
        {
            var valid = unit.Members.Length >= 1 && unit.Members.Length <= policy.MaximumPatrolSize &&
                        eligible.TryGetValue(unit.Leader, out var leader) && leader.Profile.CanLead &&
                        (unit.Members.Length > 1 || leader.Profile.MayWorkAlone) &&
                        unit.Members.All(eligible.ContainsKey) &&
                        unit.Members.Where(actor => eligible[actor].Profile.OfficerRole == NCNcpdOfficerRole.Cadet)
                            .All(actor => actor != unit.Leader);
            if (valid)
                continue;

            RemoveUnit(unit.UnitId);
        }
    }

    private void CreateUnit(NCZoneId stationZone, OfficerCandidate leader, OfficerCandidate partner)
    {
        var members = new[] { leader.ActorId, partner.ActorId }
            .OrderBy(static actor => actor.Value)
            .ToArray();
        var unitId = NCPersistentIdentity.NcpdUnit(stationZone, members);
        var state = new UnitState(unitId, leader.ActorId, members);
        _units[unitId] = state;
        foreach (var actorId in members)
            _unitByActor[actorId] = unitId;
    }

    private void CreateSoloUnit(NCZoneId stationZone, OfficerCandidate specialist)
    {
        var members = new[] { specialist.ActorId };
        var unitId = NCPersistentIdentity.NcpdUnit(stationZone, members);
        _units[unitId] = new UnitState(unitId, specialist.ActorId, members);
        _unitByActor[specialist.ActorId] = unitId;
    }

    private void ReleasePartnersForWaitingCadets(IReadOnlyDictionary<NCActorId, OfficerCandidate> eligible)
    {
        var waitingCadets = eligible.Values.Count(candidate =>
            candidate.Profile.OfficerRole == NCNcpdOfficerRole.Cadet && !_unitByActor.ContainsKey(candidate.ActorId));
        if (waitingCadets == 0)
            return;

        // A late-arriving cadet is an operational reason to replace one ordinary partner; otherwise incremental
        // check-ins could permanently leave every cadet unsupervised while valid officer/officer pairs are preserved.
        foreach (var unit in _units.Values.ToArray())
        {
            if (waitingCadets == 0)
                break;
            if (unit.Members.Length < 2)
                continue;
            if (unit.Members.Any(actor => eligible[actor].Profile.OfficerRole == NCNcpdOfficerRole.Cadet))
                continue;

            RemoveUnit(unit.UnitId);
            waitingCadets--;
        }
    }

    private void RemoveUnit(NCNcpdUnitId unitId)
    {
        if (!_units.Remove(unitId, out var unit))
            return;
        foreach (var actorId in unit.Members)
        {
            _unitByActor.Remove(actorId);
            if (_runtime.TryGetEntity(actorId, out var entityUid))
            {
                RemComp<NCNcpdAssignmentComponent>(entityUid);
                RemComp<NCNcpdUnitMemberComponent>(entityUid);
            }
        }
        RaiseLocalEvent(new NCNcpdUnitRemovedEvent(unitId, unit.Members.ToArray()));
    }

    private void ClearUnits()
    {
        foreach (var unitId in _units.Keys.ToArray())
            RemoveUnit(unitId);
        CadetsWithoutSupervisor = 0;
    }

    private void ApplyAllPhysical()
    {
        foreach (var (actorId, _) in _unitByActor)
        {
            if (_runtime.TryGetEntity(actorId, out var entityUid))
                ApplyPhysical(actorId, entityUid);
        }
    }

    private void ApplyPhysical(NCActorId actorId, EntityUid entityUid)
    {
        if (!Exists(entityUid) || !_unitByActor.TryGetValue(actorId, out var unitId) ||
            !_units.TryGetValue(unitId, out var unit))
        {
            return;
        }

        var member = EnsureComp<NCNcpdUnitMemberComponent>(entityUid);
        member.UnitId = unitId;
        member.LeaderActorId = unit.Leader;
        member.Role = actorId == unit.Leader
            ? NCNcpdUnitRole.Leader
            : _officers.TryGetRoleProfile(actorId, out _, out var role) &&
              role.OfficerRole == NCNcpdOfficerRole.Cadet
                ? NCNcpdUnitRole.Cadet
                : NCNcpdUnitRole.Partner;
        RaiseLocalEvent(new NCNcpdUnitMembershipAppliedEvent(actorId, entityUid, unitId));
    }

    private int GetRank(ProtoId<JobPrototype> job)
    {
        return _prototypes.TryIndex(NcpdDepartment, out DepartmentPrototype? department) && department != null &&
               department.NCPositionRanks.TryGetValue(job, out var rank)
            ? rank
            : int.MinValue;
    }

    private sealed record OfficerCandidate(NCActorId ActorId, NCNcpdRoleProfilePrototype Profile);
    private sealed record UnitState(NCNcpdUnitId UnitId, NCActorId Leader, NCActorId[] Members);
}
