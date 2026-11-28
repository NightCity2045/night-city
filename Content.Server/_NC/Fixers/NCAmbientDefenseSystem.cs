// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Fixers;
using Content.Shared.GameTicking;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Fixers;

/// <summary>
/// Mission places are not empty without a mission. When a player walks up to an objective of an owner that keeps
/// people there (<see cref="NCMissionOwnerPrototype.Ambient"/>), that owner's defenders appear at the guard posts
/// nearby and behave like mission guards. They leave once nobody has been around for a while; a beaten group
/// keeps the place empty for <see cref="Cooldown"/>. A mission taken for the place adopts the group instead of
/// spawning a second one.
/// </summary>
public sealed class NCAmbientDefenseSystem : EntitySystem
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DespawnDelay = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(20);

    /// <summary>A player this close to an objective, on its floor, brings the defenders out.</summary>
    private const float TriggerRange = 10f;

    /// <summary>A player this close keeps a group at its place.</summary>
    private const float StayRange = 30f;

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private NCMissionGuardSystem _guards = default!;

    private readonly List<Site> _sites = new();
    private readonly List<EntityUid> _players = new();
    private TimeSpan _nextScan;
    private int _nextGroup = -1;

    private sealed class Site
    {
        public int Group;
        public EntityUid Place;
        public readonly List<EntityUid> Members = new();
        public TimeSpan LastSeen;
        public TimeSpan? ClearedAt;
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _sites.Clear();
        _nextGroup = -1;
    }

    /// <summary>A fresh negative group id, shared with patrols so groups never collide with mission ids.</summary>
    public int NewGroup()
    {
        return _nextGroup--;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        if (now < _nextScan)
            return;

        _nextScan = now + ScanInterval;
        CollectPlayers();
        UpdateSites(now);
        if (_players.Count > 0)
            SpawnWhereVisited(now);
    }

    private void CollectPlayers()
    {
        _players.Clear();
        // Anyone with a mind counts, like for mission guards: the defenders are there for people, not NPCs.
        var query = EntityQueryEnumerator<MindContainerComponent>();
        while (query.MoveNext(out var uid, out var mind))
        {
            if (mind.HasMind && !_mobState.IsIncapacitated(uid) && !HasComp<NCMissionGuardComponent>(uid))
                _players.Add(uid);
        }
    }

    private void UpdateSites(TimeSpan now)
    {
        for (var i = _sites.Count - 1; i >= 0; i--)
        {
            var site = _sites[i];
            site.Members.RemoveAll(uid => TerminatingOrDeleted(uid));
            if (TerminatingOrDeleted(site.Place))
            {
                DeleteMembers(site);
                _sites.RemoveAt(i);
                continue;
            }

            if (AnyPlayerNear(site.Place, StayRange, 2))
                site.LastSeen = now;

            if (site.ClearedAt == null && site.Members.TrueForAll(uid => _mobState.IsIncapacitated(uid)))
                site.ClearedAt = now;

            if (now - site.LastSeen > DespawnDelay)
            {
                DeleteMembers(site);
                if (site.ClearedAt == null)
                {
                    _sites.RemoveAt(i);
                    continue;
                }
            }

            if (site.ClearedAt is { } cleared && now - cleared >= Cooldown && site.Members.Count == 0)
                _sites.RemoveAt(i);
        }
    }

    private void SpawnWhereVisited(TimeSpan now)
    {
        var query = EntityQueryEnumerator<NCMissionMarkerComponent>();
        while (query.MoveNext(out var uid, out var marker))
        {
            if (marker.Kind != NCMissionMarkerKind.Objective ||
                !_prototypes.TryIndex(marker.Owner, out var owner) ||
                owner.Ambient is not { } ambient ||
                !_prototypes.TryIndex(ambient, out var encounter) ||
                !AnyPlayerNear(uid, TriggerRange, 0) ||
                Covered(uid) ||
                MissionGuardsNear(uid))
            {
                continue;
            }

            var site = new Site { Group = NewGroup(), Place = uid, LastSeen = now };
            site.Members.AddRange(_guards.SpawnDefenders(uid, encounter, site.Group));
            _sites.Add(site);
        }
    }

    /// <summary>
    /// Hands the people standing at a place over to a mission: they keep their posts and become its defenders.
    /// </summary>
    public bool TryAdopt(EntityUid place, int mission, List<EntityUid> spawned)
    {
        // Places removed from the world (a deleted map, an admin cleaning up) are forgotten first.
        _sites.RemoveAll(site => TerminatingOrDeleted(site.Place));
        for (var i = 0; i < _sites.Count; i++)
        {
            var site = _sites[i];
            if (!_guards.IsNearby(site.Place, place))
                continue;

            site.Members.RemoveAll(uid => TerminatingOrDeleted(uid));
            if (site.ClearedAt != null || site.Members.Count == 0)
                return false;

            _guards.Regroup(site.Members, mission);
            spawned.AddRange(site.Members);
            _sites.RemoveAt(i);
            return true;
        }

        return false;
    }

    /// <summary>Whether a group (standing or beaten and still on cooldown) already holds the place.</summary>
    private bool Covered(EntityUid place)
    {
        foreach (var site in _sites)
        {
            if (_guards.IsNearby(site.Place, place))
                return true;
        }

        return false;
    }

    /// <summary>Whether a mission's defenders already stand around the place.</summary>
    private bool MissionGuardsNear(EntityUid place)
    {
        var query = EntityQueryEnumerator<NCMissionGuardComponent>();
        while (query.MoveNext(out var uid, out var guard))
        {
            if (guard.Mission > 0 && _guards.IsNearby(place, uid))
                return true;
        }

        return false;
    }

    private bool AnyPlayerNear(EntityUid place, float range, int floors)
    {
        foreach (var player in _players)
        {
            if (_guards.IsNearby(place, player, range, floors))
                return true;
        }

        return false;
    }

    private void DeleteMembers(Site site)
    {
        foreach (var uid in site.Members)
        {
            if (!TerminatingOrDeleted(uid))
                QueueDel(uid);
        }

        site.Members.Clear();
    }

    /// <summary>The members of the group standing at a place, for tests and admin tools.</summary>
    public IReadOnlyList<EntityUid> GetDefenders(EntityUid place)
    {
        foreach (var site in _sites)
        {
            if (site.Place == place)
                return site.Members;
        }

        return Array.Empty<EntityUid>();
    }

    /// <summary>Whether a place is resting after its group was beaten.</summary>
    public bool IsOnCooldown(EntityUid place)
    {
        foreach (var site in _sites)
        {
            if (site.Place == place && site.ClearedAt != null)
                return true;
        }

        return false;
    }
}
