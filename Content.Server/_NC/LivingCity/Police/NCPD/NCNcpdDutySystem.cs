// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>
/// Advances only officers queued by schedule/materialization events; it never scans the city or ECS every tick.
/// </summary>
public sealed partial class NCNcpdDutySystem : EntitySystem
{
    private static readonly ProtoId<NCNcpdDutyPolicyPrototype> DefaultPolicy = "NCDefaultNcpdDutyPolicy";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCNcpdWorkforceProjectionSystem _officers = default!;

    private readonly Dictionary<NCActorId, NCNcpdDutySnapshot> _states = [];
    private readonly Queue<NCActorId> _pending = [];
    private readonly HashSet<NCActorId> _pendingSet = [];
    private readonly HashSet<NCActorId> _commandAssigned = [];
    private readonly List<(NCActorId ActorId, ProtoId<NCNcpdRoleProfilePrototype> Profile)> _officerBuffer = [];

    public int OfficerCount => _states.Count;
    public int OnDutyCount => _states.Values.Count(static state => state.Status is
        NCNcpdDutyStatus.ReportToStation or NCNcpdDutyStatus.CheckIn or NCNcpdDutyStatus.Available or
        NCNcpdDutyStatus.Assigned);
    public int AvailableCount => _states.Values.Count(static state => state.Status == NCNcpdDutyStatus.Available);
    public int PendingTransitions => _pending.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCNcpdOfficerProjectedEvent>(OnOfficerProjected);
        SubscribeLocalEvent<NCCityActorPersistedEvent>(OnActorPersisted);
        SubscribeLocalEvent<NCCityActorMaterializationChangedEvent>(OnMaterializationChanged);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(_ => BootstrapOfficers());
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => ClearRound());
        BootstrapOfficers();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_prototypes.TryIndex(DefaultPolicy, out NCNcpdDutyPolicyPrototype? policy) || policy == null)
            return;

        var limit = Math.Max(1, policy.MaximumTransitionsPerEvent);
        for (var i = 0; i < limit && _pending.TryDequeue(out var actorId); i++)
        {
            _pendingSet.Remove(actorId);
            Advance(actorId);
        }
    }

    public bool TryGetState(NCActorId actorId, out NCNcpdDutySnapshot snapshot) =>
        _states.TryGetValue(actorId, out snapshot!);

    public void GetSnapshots(List<NCNcpdDutySnapshot> result)
    {
        result.Clear();
        result.AddRange(_states.Values.OrderBy(static value => value.ActorId.Value));
    }

    public void SetCommandAssignment(IEnumerable<NCActorId> actors, bool assigned)
    {
        foreach (var actorId in actors)
        {
            var changed = assigned ? _commandAssigned.Add(actorId) : _commandAssigned.Remove(actorId);
            if (changed)
                Enqueue(actorId);
        }
    }

    private void OnOfficerProjected(NCNcpdOfficerProjectedEvent args)
    {
        if (!_states.ContainsKey(args.ActorId))
            _states.Add(args.ActorId, new NCNcpdDutySnapshot(args.ActorId, args.RoleProfile, NCNcpdDutyStatus.OffDuty));
        else
            _states[args.ActorId] = _states[args.ActorId] with { RoleProfile = args.RoleProfile };
        Enqueue(args.ActorId);
    }

    private void OnActorPersisted(NCCityActorPersistedEvent args)
    {
        if (_states.ContainsKey(args.ActorId))
            Enqueue(args.ActorId);
    }

    private void OnMaterializationChanged(NCCityActorMaterializationChangedEvent args)
    {
        if (!_states.TryGetValue(args.ActorId, out var state))
            return;
        if (args.Materialized)
            ApplyPhysical(args.ActorId, state);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<NCNcpdDutyPolicyPrototype>() && !args.WasModified<NCNcpdRoleProfilePrototype>())
            return;

        BootstrapOfficers();
    }

    private void BootstrapOfficers()
    {
        _officers.GetOfficerProjections(_officerBuffer);
        foreach (var (actorId, profile) in _officerBuffer)
        {
            if (_states.TryGetValue(actorId, out var state))
                _states[actorId] = state with { RoleProfile = profile };
            else
                _states.Add(actorId, new NCNcpdDutySnapshot(actorId, profile, NCNcpdDutyStatus.OffDuty));
            Enqueue(actorId);
        }
    }

    private void Advance(NCActorId actorId)
    {
        if (!_states.TryGetValue(actorId, out var current) ||
            !_officers.TryGetRoleProfile(actorId, out var profileId, out var profile))
        {
            return;
        }

        var desired = GetDesiredTerminal(actorId, profile);
        var next = NextStatus(current.Status, desired);
        if (profileId != current.RoleProfile || next != current.Status)
        {
            var updated = new NCNcpdDutySnapshot(actorId, profileId, next);
            _states[actorId] = updated;
            ApplyPhysical(actorId, updated);
            RaiseLocalEvent(new NCNcpdDutyChangedEvent(actorId, current.Status, next));
        }

        if (next != desired)
            Enqueue(actorId);
    }

    private NCNcpdDutyStatus GetDesiredTerminal(NCActorId actorId, NCNcpdRoleProfilePrototype profile)
    {
        if (!_actors.TryGetCached(actorId, out var actor) || actor.Snapshot.Lifecycle.Frozen ||
            actor.Snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived or
                NCCityActorStatus.Hospitalized or NCCityActorStatus.Arrested or NCCityActorStatus.Imprisoned or
                NCCityActorStatus.Unconscious ||
            !string.Equals(actor.Snapshot.Schedule.CurrentActivity, "work", StringComparison.OrdinalIgnoreCase))
        {
            return NCNcpdDutyStatus.OffDuty;
        }

        if (_commandAssigned.Contains(actorId))
            return NCNcpdDutyStatus.Assigned;

        var atStation = actor.Snapshot.Location.CurrentZone == profile.WorkZone &&
                        actor.Snapshot.Location.TravelPhase is NCCityActorTravelPhase.Stationary or
                            NCCityActorTravelPhase.Arrived;
        return atStation ? NCNcpdDutyStatus.Available : NCNcpdDutyStatus.ReportToStation;
    }

    private static NCNcpdDutyStatus NextStatus(NCNcpdDutyStatus current, NCNcpdDutyStatus desired)
    {
        if (desired == NCNcpdDutyStatus.OffDuty)
        {
            return current switch
            {
                NCNcpdDutyStatus.OffDuty => current,
                NCNcpdDutyStatus.CheckOut => NCNcpdDutyStatus.OffDuty,
                _ => NCNcpdDutyStatus.CheckOut,
            };
        }

        if (desired == NCNcpdDutyStatus.ReportToStation)
            return current is NCNcpdDutyStatus.OffDuty or NCNcpdDutyStatus.CheckOut
                ? NCNcpdDutyStatus.ReportToStation
                : current == NCNcpdDutyStatus.Available
                    ? NCNcpdDutyStatus.ReportToStation
                    : current;

        if (desired == NCNcpdDutyStatus.Assigned)
        {
            return current switch
            {
                NCNcpdDutyStatus.OffDuty or NCNcpdDutyStatus.CheckOut => NCNcpdDutyStatus.ReportToStation,
                NCNcpdDutyStatus.ReportToStation => NCNcpdDutyStatus.CheckIn,
                NCNcpdDutyStatus.CheckIn or NCNcpdDutyStatus.Available => NCNcpdDutyStatus.Assigned,
                _ => current,
            };
        }

        return current switch
        {
            NCNcpdDutyStatus.OffDuty or NCNcpdDutyStatus.CheckOut => NCNcpdDutyStatus.ReportToStation,
            NCNcpdDutyStatus.ReportToStation => NCNcpdDutyStatus.CheckIn,
            NCNcpdDutyStatus.CheckIn => NCNcpdDutyStatus.Available,
            NCNcpdDutyStatus.Assigned => NCNcpdDutyStatus.Available,
            _ => current,
        };
    }

    private void ApplyPhysical(NCActorId actorId, NCNcpdDutySnapshot state)
    {
        if (!_runtime.TryGetEntity(actorId, out var entityUid) ||
            !_officers.TryGetRoleProfile(actorId, out _, out var profile) || !Exists(entityUid))
        {
            return;
        }

        var duty = EnsureComp<NCNcpdDutyComponent>(entityUid);
        duty.Status = state.Status;
        duty.StationZone = profile.WorkZone;
    }

    private void Enqueue(NCActorId actorId)
    {
        if (_pendingSet.Add(actorId))
            _pending.Enqueue(actorId);
    }

    private void ClearRound()
    {
        _states.Clear();
        _pending.Clear();
        _pendingSet.Clear();
        _commandAssigned.Clear();
    }
}
