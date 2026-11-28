// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.Server.Chat.Systems;
using Content.Shared._NC.Fixers;
using Content.Shared.Chat;
using Content.Shared.Damage.Systems;
using Content.Shared.Examine;
using Content.Shared.Hands;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Mind.Components;
using Content.Shared._NC.Coordinates.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._NC.Fixers;

/// <summary>
/// Mission defenders are neutral until provoked. Someone walking into a post is warned; drawing a weapon there,
/// lingering close after the warning, hurting anyone or anything of the mission, or taking a mission item in
/// view of a guard turns the whole group hostile to that person. Aggressive groups skip the warning.
/// A group is a mission's defenders, or (with a negative id) people standing at a place or walking a patrol
/// without one.
/// </summary>
public sealed partial class NCMissionGuardSystem : EntitySystem
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(0.5);
    private const float PostRange = 24f;
    private const int PostFloorRange = 2;

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private ExamineSystemShared _examine = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private NpcFactionSystem _factions = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCMapCoordinatesSystem _mapCoordinates = default!;

    private TimeSpan _nextScan;
    private readonly HashSet<Entity<MindContainerComponent>> _nearby = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCMissionEntityComponent, DamageDealtEvent>(OnDamaged);
        SubscribeLocalEvent<NCMissionEntityComponent, GotEquippedHandEvent>(OnPickedUp);
    }

    /// <summary>Makes a spawned mob a guard of the mission with the encounter's lines.</summary>
    public void MakeGuard(EntityUid mob, int mission, NCFixerEncounterPrototype? encounter)
    {
        var guard = EnsureComp<NCMissionGuardComponent>(mob);
        guard.Mission = mission;
        if (encounter != null)
        {
            guard.Warnings = encounter.Warnings.ToList();
            guard.Alarms = encounter.Alarms.ToList();
            guard.Aggressive = encounter.Aggressive;
        }

        EnsureComp<FactionExceptionComponent>(mob);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        if (now < _nextScan)
            return;

        _nextScan = now + ScanInterval;
        var query = EntityQueryEnumerator<NCMissionGuardComponent>();
        while (query.MoveNext(out var guardUid, out var guard))
        {
            if (_mobState.IsIncapacitated(guardUid))
                continue;

            _nearby.Clear();
            _lookup.GetEntitiesInRange(Transform(guardUid).Coordinates, guard.WarnRange, _nearby);
            foreach (var (player, mindContainer) in _nearby)
            {
                if (!mindContainer.HasMind || HasComp<NCMissionGuardComponent>(player) ||
                    _mobState.IsIncapacitated(player) ||
                    guard.Hostile.Contains(player) ||
                    !_examine.InRangeUnOccluded(guardUid, player, guard.WarnRange))
                {
                    continue;
                }

                if (guard.Aggressive || HoldsWeapon(player))
                {
                    Alarm(guard.Mission, player);
                    continue;
                }

                if (!guard.Warned.TryGetValue(player, out var warnedAt))
                {
                    guard.Warned[player] = now;
                    Say(guardUid, guard.Warnings);
                    continue;
                }

                if (now - warnedAt > guard.Patience && _examine.InRangeUnOccluded(guardUid, player, guard.CloseRange))
                    Alarm(guard.Mission, player);
            }
        }
    }

    /// <summary>
    /// Spawns an encounter's defenders at the guard posts that serve a place (posts of the place's owner, or the
    /// place itself when there are none) and makes them one group.
    /// </summary>
    public List<EntityUid> SpawnDefenders(EntityUid place, NCFixerEncounterPrototype encounter, int group, int extra = 0)
    {
        var owner = CompOrNull<NCMissionMarkerComponent>(place)?.Owner;
        var posts = new List<EntityUid>();
        var query = EntityQueryEnumerator<NCMissionMarkerComponent>();
        while (query.MoveNext(out var uid, out var marker))
        {
            if (marker.Kind == NCMissionMarkerKind.Guard && (owner == null || marker.Owner == owner) && IsNearby(place, uid))
                posts.Add(uid);
        }

        var mobs = new List<EntProtoId>();
        foreach (var entry in encounter.Entries)
        {
            var amount = _random.Next(entry.Min, entry.Max + 1);
            for (var i = 0; i < amount; i++)
                mobs.Add(entry.Mob);
        }

        for (var i = 0; i < extra && encounter.Entries.Count > 0; i++)
            mobs.Add(_random.Pick(encounter.Entries).Mob);

        var spawned = new List<EntityUid>();
        var count = 0;
        foreach (var mob in mobs)
        {
            var at = posts.Count > 0
                ? Transform(posts[count++ % posts.Count]).Coordinates
                : Transform(place).Coordinates.Offset(new System.Numerics.Vector2(_random.Next(-2, 3), _random.Next(-2, 3)));
            spawned.Add(SpawnGuard(mob, at, group, encounter));
        }

        return spawned;
    }

    /// <summary>Spawns one member of a group.</summary>
    public EntityUid SpawnGuard(EntProtoId mob, EntityCoordinates at, int group, NCFixerEncounterPrototype? encounter)
    {
        var uid = Spawn(mob, at);
        EnsureComp<NCMissionEntityComponent>(uid).Mission = group;
        MakeGuard(uid, group, encounter);
        return uid;
    }

    /// <summary>Moves a group's members over to another group (a mission taking over people already there).</summary>
    public void Regroup(IEnumerable<EntityUid> members, int group)
    {
        foreach (var uid in members)
        {
            if (TryComp<NCMissionGuardComponent>(uid, out var guard))
                guard.Mission = group;
            if (TryComp<NCMissionEntityComponent>(uid, out var entity))
                entity.Mission = group;
        }
    }

    /// <summary>
    /// Whether a guard post serves a place: on the city network, within range on the plan and a couple of floors
    /// up or down; off the network, on the same map and within range.
    /// </summary>
    public bool IsNearby(EntityUid place, EntityUid post, float range = PostRange, int floors = PostFloorRange)
    {
        if (TerminatingOrDeleted(place) || TerminatingOrDeleted(post))
            return false;

        if (_mapCoordinates.TryGetCoordinates(place, out var origin) &&
            _mapCoordinates.TryGetCoordinates(post, out var coordinates))
        {
            return coordinates.NetworkId == origin.NetworkId &&
                   Math.Abs(coordinates.Z - origin.Z) <= floors &&
                   (coordinates.Position - origin.Position).Length() <= range;
        }

        var a = _transform.GetMapCoordinates(place);
        var b = _transform.GetMapCoordinates(post);
        return a.MapId == b.MapId && (a.Position - b.Position).Length() <= range;
    }

    private bool HoldsWeapon(EntityUid player)
    {
        foreach (var held in _hands.EnumerateHeld(player))
        {
            if (HasComp<GunComponent>(held) || HasComp<MeleeWeaponComponent>(held))
                return true;
        }

        return false;
    }

    private void OnDamaged(Entity<NCMissionEntityComponent> ent, ref DamageDealtEvent args)
    {
        if (args.Origin is { } origin && origin != ent.Owner &&
            TryComp<MindContainerComponent>(origin, out var mind) && mind.HasMind)
            Alarm(ent.Comp.Mission, origin);
    }

    /// <summary>Taking a mission item in front of a guard is theft.</summary>
    private void OnPickedUp(Entity<NCMissionEntityComponent> ent, ref GotEquippedHandEvent args)
    {
        var query = EntityQueryEnumerator<NCMissionGuardComponent>();
        while (query.MoveNext(out var guardUid, out var guard))
        {
            if (guard.Mission == ent.Comp.Mission && !_mobState.IsIncapacitated(guardUid) &&
                _examine.InRangeUnOccluded(guardUid, args.User, guard.WarnRange))
            {
                Alarm(ent.Comp.Mission, args.User);
                return;
            }
        }
    }

    /// <summary>Turns every guard of the mission hostile to the player.</summary>
    public void Alarm(int mission, EntityUid player)
    {
        var shouted = false;
        var query = EntityQueryEnumerator<NCMissionGuardComponent>();
        while (query.MoveNext(out var guardUid, out var guard))
        {
            if (guard.Mission != mission || !guard.Hostile.Add(player))
                continue;

            _factions.AggroEntity(guardUid, player);
            if (!shouted && !_mobState.IsIncapacitated(guardUid))
            {
                Say(guardUid, guard.Alarms);
                shouted = true;
            }
        }
    }

    private void Say(EntityUid guard, List<LocId> lines)
    {
        if (lines.Count == 0)
            return;

        _chat.TrySendInGameICMessage(guard, Loc.GetString(_random.Pick(lines)), InGameICChatType.Speak, false);
    }
}
