// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared._NC.Fixers;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._NC.Fixers;

/// <summary>
/// Street patrols: while a player is on an owner's ground, a group or two of its people walk between the owner's
/// places. A patrol appears out of sight of every player, walks waypoint to waypoint on its floor and behaves like
/// any defender group (warns, then fights). Nobody on the ground for a while sends it home; a beaten patrol stays
/// away for <see cref="NCMissionPatrolPrototype.RespawnDelay"/>.
/// </summary>
public sealed partial class NCMissionPatrolSystem : EntitySystem
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DespawnDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LegTimeout = TimeSpan.FromSeconds(90);
    private static readonly ProtoId<HTNCompoundPrototype> PatrolTask = "NCPatrolHumanoidCompound";

    /// <summary>Blackboard key with the coordinates the patrol walks to next.</summary>
    public const string TargetKey = "NCPatrolTarget";

    private const float MinSpawnDistance = 14f;
    private const float MaxSpawnDistance = 40f;
    private const float SightRange = 20f;
    private const float MinLeg = 6f;
    private const float MaxLeg = 40f;

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private ExamineSystemShared _examine = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private NPCSystem _npc = default!;
    [Dependency] private HTNSystem _htn = default!;
    [Dependency] private NCMissionGuardSystem _guards = default!;
    [Dependency] private NCAmbientDefenseSystem _ambient = default!;

    private readonly Dictionary<string, PatrolState> _patrols = new();
    private readonly List<EntityUid> _fallen = new();
    private readonly List<(EntityUid Uid, MapCoordinates Position)> _players = new();
    private readonly List<(EntityUid Uid, MapCoordinates Position)> _waypoints = new();
    private TimeSpan _nextScan;

    private sealed class PatrolState
    {
        public readonly List<Group> Groups = new();
        public TimeSpan NextSpawn;
        public TimeSpan LastActive;
    }

    private sealed class Group
    {
        public int Id;
        public MapId Map;
        public readonly List<EntityUid> Members = new();
        public TimeSpan LegStarted;
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _patrols.Clear();
        _fallen.Clear();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        if (now < _nextScan)
            return;

        _nextScan = now + ScanInterval;
        CollectPlayers();
        foreach (var patrol in _prototypes.EnumeratePrototypes<NCMissionPatrolPrototype>())
        {
            UpdatePatrol(patrol, now);
        }

        ClearFallen();
    }

    /// <summary>Bodies of beaten patrols are removed once no player is close enough to see it.</summary>
    private void ClearFallen()
    {
        for (var i = _fallen.Count - 1; i >= 0; i--)
        {
            var uid = _fallen[i];
            if (TerminatingOrDeleted(uid))
            {
                _fallen.RemoveAt(i);
                continue;
            }

            var position = _transform.GetMapCoordinates(uid);
            if (_players.Any(p => p.Position.MapId == position.MapId && (p.Position.Position - position.Position).Length() <= SightRange))
                continue;

            QueueDel(uid);
            _fallen.RemoveAt(i);
        }
    }

    private void CollectPlayers()
    {
        _players.Clear();
        // Anyone with a mind counts, like for mission guards: the defenders are there for people, not NPCs.
        var query = EntityQueryEnumerator<MindContainerComponent>();
        while (query.MoveNext(out var uid, out var mind))
        {
            if (mind.HasMind && !_mobState.IsIncapacitated(uid) && !HasComp<NCMissionGuardComponent>(uid))
                _players.Add((uid, _transform.GetMapCoordinates(uid)));
        }
    }

    private void UpdatePatrol(NCMissionPatrolPrototype patrol, TimeSpan now)
    {
        if (!_patrols.TryGetValue(patrol.ID, out var state))
        {
            state = new PatrolState();
            _patrols[patrol.ID] = state;
        }

        for (var i = state.Groups.Count - 1; i >= 0; i--)
        {
            var group = state.Groups[i];
            group.Members.RemoveAll(uid => TerminatingOrDeleted(uid));
            if (group.Members.TrueForAll(uid => _mobState.IsIncapacitated(uid)))
            {
                // Beaten: the bodies stay until nobody is around and the patrol stays away for a while.
                _fallen.AddRange(group.Members);
                state.Groups.RemoveAt(i);
                state.NextSpawn = now + patrol.RespawnDelay;
            }
        }

        if (state.Groups.Count == 0 && _players.Count == 0)
            return;

        CollectWaypoints(patrol);
        var active = ActivePlayer(patrol);
        if (active != null)
            state.LastActive = now;
        else if (now - state.LastActive > DespawnDelay)
        {
            foreach (var group in state.Groups)
                SendHome(group);
            state.Groups.Clear();
            return;
        }

        if (active is { } player && state.Groups.Count < patrol.Groups && now >= state.NextSpawn &&
            TrySpawn(patrol, player, now) is { } spawned)
        {
            state.Groups.Add(spawned);
        }

        foreach (var group in state.Groups)
            Walk(group, now);
    }

    private void CollectWaypoints(NCMissionPatrolPrototype patrol)
    {
        _waypoints.Clear();
        var query = EntityQueryEnumerator<NCMissionMarkerComponent>();
        while (query.MoveNext(out var uid, out var marker))
        {
            if (marker.Kind is NCMissionMarkerKind.Objective or NCMissionMarkerKind.Guard && patrol.Owners.Contains(marker.Owner))
                _waypoints.Add((uid, _transform.GetMapCoordinates(uid)));
        }
    }

    /// <summary>A player standing on the patrol's ground, if any.</summary>
    private (EntityUid Uid, MapCoordinates Position)? ActivePlayer(NCMissionPatrolPrototype patrol)
    {
        foreach (var player in _players)
        {
            foreach (var (_, position) in _waypoints)
            {
                if (position.MapId == player.Position.MapId &&
                    (position.Position - player.Position.Position).Length() <= patrol.ActivationRange)
                {
                    return player;
                }
            }
        }

        return null;
    }

    private Group? TrySpawn(NCMissionPatrolPrototype patrol, (EntityUid Uid, MapCoordinates Position) player, TimeSpan now)
    {
        if (!_prototypes.TryIndex(patrol.Encounter, out var encounter))
            return null;

        var candidates = new List<EntityUid>();
        foreach (var (uid, position) in _waypoints)
        {
            if (position.MapId != player.Position.MapId ||
                (position.Position - player.Position.Position).Length() > MaxSpawnDistance ||
                !OutOfSight(uid, position))
            {
                continue;
            }

            candidates.Add(uid);
        }

        if (candidates.Count == 0)
            return null;

        var start = _random.Pick(candidates);
        var coordinates = Transform(start).Coordinates;
        var group = new Group { Id = _ambient.NewGroup(), Map = player.Position.MapId, LegStarted = now };
        foreach (var entry in encounter.Entries)
        {
            var amount = _random.Next(entry.Min, entry.Max + 1);
            for (var i = 0; i < amount; i++)
            {
                var mob = _guards.SpawnGuard(entry.Mob, coordinates, group.Id, encounter);
                var member = EnsureComp<NCMissionPatrolMemberComponent>(mob);
                member.Patrol = patrol.ID;
                member.Group = group.Id;
                if (TryComp<HTNComponent>(mob, out var htn))
                {
                    htn.RootTask = new HTNCompoundTask { Task = PatrolTask };
                    _htn.Replan(htn);
                }

                group.Members.Add(mob);
            }
        }

        if (group.Members.Count == 0)
            return null;

        SetLeg(group, now);
        return group;
    }

    /// <summary>Far enough from every player on the floor and hidden from those close enough to see it.</summary>
    private bool OutOfSight(EntityUid waypoint, MapCoordinates position)
    {
        foreach (var (uid, player) in _players)
        {
            if (player.MapId != position.MapId)
                continue;

            var distance = (player.Position - position.Position).Length();
            if (distance < MinSpawnDistance)
                return false;
            if (distance <= SightRange && _examine.InRangeUnOccluded(uid, waypoint, SightRange))
                return false;
        }

        return true;
    }

    private void Walk(Group group, TimeSpan now)
    {
        var leader = group.Members.FirstOrDefault(uid => !_mobState.IsIncapacitated(uid));
        if (leader == default || !TryComp<HTNComponent>(leader, out var htn))
            return;

        // The move operator drops the key once the leader arrives; a leg that takes too long is given up.
        if (!htn.Blackboard.ContainsKey(TargetKey) || now - group.LegStarted > LegTimeout)
            SetLeg(group, now);
    }

    private void SetLeg(Group group, TimeSpan now)
    {
        var leader = group.Members.FirstOrDefault(uid => !_mobState.IsIncapacitated(uid));
        if (leader == default)
            return;

        var from = _transform.GetMapCoordinates(leader);
        var legs = new List<EntityUid>();
        foreach (var (uid, position) in _waypoints)
        {
            if (position.MapId != group.Map)
                continue;

            var distance = (position.Position - from.Position).Length();
            if (distance >= MinLeg && distance <= MaxLeg)
                legs.Add(uid);
        }

        if (legs.Count == 0)
            return;

        var target = Transform(_random.Pick(legs)).Coordinates;
        foreach (var uid in group.Members)
        {
            if (!_mobState.IsIncapacitated(uid))
                _npc.SetBlackboard(uid, TargetKey, target);
        }

        group.LegStarted = now;
    }

    /// <summary>A patrol nobody is around to see walks off: its members are removed.</summary>
    private void SendHome(Group group)
    {
        foreach (var uid in group.Members)
        {
            if (!TerminatingOrDeleted(uid))
                QueueDel(uid);
        }

        group.Members.Clear();
    }

    /// <summary>The members of a patrol walking now, for tests and admin tools.</summary>
    public IEnumerable<EntityUid> GetMembers(ProtoId<NCMissionPatrolPrototype> patrol)
    {
        if (!_patrols.TryGetValue(patrol, out var state))
            yield break;

        foreach (var group in state.Groups)
        {
            foreach (var uid in group.Members)
                yield return uid;
        }
    }
}
