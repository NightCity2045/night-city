// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.Server._NC.Bank;
using Content.Server._NC.City.Atlas;
using Content.Server._NC.Corporations;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Shared._NC.City.Map;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.Fixers;
using Content.Shared.GameTicking;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Shared.Mind;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.PDA;
using Content.Shared.Popups;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Content.Shared.Storage.Components;
using Content.Shared.Storage.EntitySystems;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._NC.Fixers;

/// <summary>
/// Fixer missions. Every fixer offers a few missions drawn from its pool; a player takes one at a time, and it
/// is finished by whoever does the work, but the reward and the street reputation go to the taker.
/// Mission places come from mapped markers; the city atlas names their district and building.
/// </summary>
public sealed partial class NCFixerMissionSystem : EntitySystem
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(5);
    private const float CleanupPlayerRange = 12f;
    private const float GuardMarkerRange = 24f;
    private const int GuardFloorRange = 2;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedMindSystem _minds = default!;
    [Dependency] private SharedJobSystem _jobs = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedEntityStorageSystem _entityStorage = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private NCMapCoordinatesSystem _mapCoordinates = default!;
    [Dependency] private NCCityAtlasSystem _atlas = default!;
    [Dependency] private NCBankSystem _bank = default!;
    [Dependency] private NCCorpContractSystem _goods = default!;
    [Dependency] private NCStreetReputationSystem _reputation = default!;

    private readonly Dictionary<string, List<ProtoId<NCFixerMissionPrototype>?>> _offers = new();
    private readonly Dictionary<string, TimeSpan> _nextRefresh = new();
    private readonly Dictionary<int, ActiveMission> _active = new();
    private readonly Dictionary<EntityUid, int> _greetings = new();
    private readonly List<EntityUid> _cleanup = new();
    private int _nextMissionId = 1;
    private TimeSpan _nextCheck;
    private TimeSpan _nextCleanup;

    private sealed class ActiveMission
    {
        public int Id;
        public ProtoId<NCFixerMissionPrototype> Mission;
        public ProtoId<NCFixerPrototype> Fixer;
        public int ProfileId;
        public EntityUid Mind;
        public TimeSpan Deadline;
        public NCFixerMissionStatus Status = NCFixerMissionStatus.Active;
        public EntityUid? Objective;
        public EntityUid? Item;
        public EntityUid? Drop;
        public EntityUid? Target;
        public string? TargetName;
        public readonly List<EntityUid> Spawned = new();
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCFixerComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCFixerComponent, NCFixerAcceptMessage>(OnAccept);
        SubscribeLocalEvent<NCFixerComponent, NCFixerAbandonMessage>(OnAbandon);
        SubscribeLocalEvent<NCFixerComponent, NCFixerCollectPaymentMessage>(OnCollectPayment);
        SubscribeLocalEvent<MobStateComponent, NCCityMapCollectMarkersEvent>(OnCollectMarkers);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _offers.Clear();
        _nextRefresh.Clear();
        _active.Clear();
        _greetings.Clear();
        _cleanup.Clear();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        if (now >= _nextCheck)
        {
            _nextCheck = now + CheckInterval;
            foreach (var mission in _active.Values.ToList())
            {
                if (mission.Status == NCFixerMissionStatus.Active)
                    CheckMission(mission, now);
            }

            var fixers = EntityQueryEnumerator<NCFixerComponent>();
            while (fixers.MoveNext(out var uid, out var fixer))
            {
                if (_ui.IsUiOpen(uid, NCFixerUiKey.Key))
                    UpdateUi((uid, fixer));
            }
        }

        if (now >= _nextCleanup)
        {
            _nextCleanup = now + CleanupInterval;
            RunCleanup();
        }
    }

    #region Offers

    /// <summary>The missions a fixer offers now; taken slots are null until the next refresh.</summary>
    public IReadOnlyList<ProtoId<NCFixerMissionPrototype>?> GetOffers(ProtoId<NCFixerPrototype> fixer)
    {
        var now = _timing.CurTime;
        if (_offers.TryGetValue(fixer, out var offers) && now < _nextRefresh.GetValueOrDefault(fixer))
            return offers;

        var prototype = _prototypes.Index(fixer);
        offers = new List<ProtoId<NCFixerMissionPrototype>?>();
        for (var i = 0; i < prototype.Offers; i++)
            offers.Add(Draw(fixer, offers));

        _offers[fixer] = offers;
        _nextRefresh[fixer] = now + prototype.RefreshInterval;
        return offers;
    }

    /// <summary>Puts a specific mission of the fixer into an offer slot.</summary>
    public bool TrySetOffer(ProtoId<NCFixerPrototype> fixer, int slot, ProtoId<NCFixerMissionPrototype> mission)
    {
        GetOffers(fixer);
        if (!_offers.TryGetValue(fixer, out var offers) || slot < 0 || slot >= offers.Count ||
            !_prototypes.TryIndex(mission, out var prototype) || prototype.Fixer != fixer)
        {
            return false;
        }

        offers[slot] = mission;
        return true;
    }

    private ProtoId<NCFixerMissionPrototype>? Draw(string fixer, List<ProtoId<NCFixerMissionPrototype>?> taken)
    {
        var pool = _prototypes.EnumeratePrototypes<NCFixerMissionPrototype>()
            .Where(mission => mission.Fixer == fixer && mission.Weight > 0 && !taken.Contains(mission.ID) &&
                              IsAvailable(mission, null))
            .ToList();
        if (pool.Count == 0)
            return null;

        var roll = _random.NextFloat() * pool.Sum(mission => mission.Weight);
        foreach (var mission in pool)
        {
            roll -= mission.Weight;
            if (roll <= 0)
                return mission.ID;
        }

        return pool[^1].ID;
    }

    /// <summary>Whether the map has what the mission needs: markers, a drop and, for player targets, a target.</summary>
    private bool IsAvailable(NCFixerMissionPrototype mission, EntityUid? taker)
    {
        if (mission.TargetDepartments.Count > 0)
        {
            if (PickPlayerTarget(mission, taker) == null)
                return false;
        }
        else if (mission.Kind != NCFixerMissionKind.Collection &&
                 (mission.Item == null || PickMarker(NCMissionMarkerKind.Objective, mission.Owner, null) == null))
        {
            return false;
        }

        return mission.Kind switch
        {
            NCFixerMissionKind.Delivery when mission.DropAtMarker => PickMarker(NCMissionMarkerKind.DeadDrop, null, null) != null,
            NCFixerMissionKind.Delivery or NCFixerMissionKind.Retrieval or NCFixerMissionKind.Collection =>
                FindFixerDrop(mission.Fixer) != null,
            _ => true,
        };
    }

    #endregion

    #region Places

    private EntityUid? PickMarker(NCMissionMarkerKind kind, ProtoId<NCMissionOwnerPrototype>? owner, EntityUid? avoid)
    {
        var candidates = new List<EntityUid>();
        var query = EntityQueryEnumerator<NCMissionMarkerComponent>();
        while (query.MoveNext(out var uid, out var marker))
        {
            if (marker.Kind == kind && (owner == null || marker.Owner == owner) && uid != avoid)
                candidates.Add(uid);
        }

        return candidates.Count == 0 ? null : _random.Pick(candidates);
    }

    private EntityUid? FindFixerDrop(ProtoId<NCFixerPrototype> fixer)
    {
        var query = EntityQueryEnumerator<NCFixerDeadDropComponent>();
        while (query.MoveNext(out var uid, out var drop))
        {
            if (drop.Fixer == fixer)
                return uid;
        }

        return null;
    }

    private NCMissionPlace? Describe(EntityUid? place)
    {
        if (place is not { } uid || Deleted(uid) || !_mapCoordinates.TryGetCoordinates(uid, out var coordinates))
            return null;

        _atlas.GetLocation(uid, out var district, out var building);
        var owner = TryComp<NCMissionMarkerComponent>(uid, out var marker) ? marker.Owner : (ProtoId<NCMissionOwnerPrototype>?) null;
        return new NCMissionPlace(district, building, coordinates.Z, owner);
    }

    /// <summary>A random living player of one of the mission's target departments, never the taker.</summary>
    private EntityUid? PickPlayerTarget(NCFixerMissionPrototype mission, EntityUid? taker)
    {
        var roles = new HashSet<ProtoId<JobPrototype>>();
        foreach (var department in mission.TargetDepartments)
        {
            if (_prototypes.TryIndex(department, out var prototype))
                roles.UnionWith(prototype.Roles);
        }

        var candidates = new List<EntityUid>();
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { } mob || mob == taker ||
                !_minds.TryGetMind(session, out var mindId, out var mind) || mind.OwnedEntity != mob ||
                _mobState.IsDead(mob) ||
                !_jobs.MindTryGetJobId(mindId, out var job) || job is not { } jobId || !roles.Contains(jobId))
            {
                continue;
            }

            if (mission.Kind == NCFixerMissionKind.Retrieval && GetPda(mob) == null)
                continue;

            candidates.Add(mob);
        }

        return candidates.Count == 0 ? null : _random.Pick(candidates);
    }

    private EntityUid? GetPda(EntityUid mob)
    {
        return _inventory.TryGetSlotEntity(mob, "id", out var item) && HasComp<PdaComponent>(item) ? item : null;
    }

    #endregion

    #region Taking a mission

    private void OnAccept(Entity<NCFixerComponent> ent, ref NCFixerAcceptMessage args)
    {
        TryAccept(ent, args.Slot, args.Actor);
        UpdateUi(ent);
    }

    /// <summary>Gives the user the mission in an offer slot of this fixer.</summary>
    public bool TryAccept(Entity<NCFixerComponent> fixer, int slot, EntityUid user)
    {
        if (!_reputation.TryGetProfile(user, out var profileId) ||
            !_minds.TryGetMind(user, out var mindId, out _))
        {
            return false;
        }

        if (_active.ContainsKey(profileId))
        {
            _popup.PopupEntity(Loc.GetString("nc-fixer-already-busy"), fixer, user);
            return false;
        }

        var offers = GetOffers(fixer.Comp.Fixer);
        if (slot < 0 || slot >= offers.Count || offers[slot] is not { } missionId ||
            !_prototypes.TryIndex(missionId, out var mission))
        {
            return false;
        }

        if (_reputation.GetReputation(profileId) < mission.MinimumReputation)
        {
            _popup.PopupEntity(Loc.GetString("nc-fixer-not-trusted"), fixer, user);
            return false;
        }

        var active = new ActiveMission
        {
            Id = _nextMissionId++,
            Mission = missionId,
            Fixer = fixer.Comp.Fixer,
            ProfileId = profileId,
            Mind = mindId,
            Deadline = _timing.CurTime + mission.TimeLimit,
        };

        if (!TryStart(active, mission, user))
        {
            Cleanup(active);
            _popup.PopupEntity(Loc.GetString("nc-fixer-mission-unavailable"), fixer, user);
            return false;
        }

        if (_offers.TryGetValue(fixer.Comp.Fixer, out var list))
            list[slot] = null;
        _active[profileId] = active;
        Notify(active, Loc.GetString("nc-fixer-mission-taken", ("mission", Loc.GetString(mission.Name))));
        return true;
    }

    private bool TryStart(ActiveMission active, NCFixerMissionPrototype mission, EntityUid taker)
    {
        if (mission.TargetDepartments.Count > 0)
        {
            if (PickPlayerTarget(mission, taker) is not { } target)
                return false;

            active.Target = target;
            active.TargetName = Name(target);
            if (mission.Kind == NCFixerMissionKind.Retrieval)
                active.Item = GetPda(target);
        }
        else if (mission.Kind != NCFixerMissionKind.Collection)
        {
            if (mission.Item is not { } item ||
                PickMarker(NCMissionMarkerKind.Objective, mission.Owner, null) is not { } objective)
            {
                return false;
            }

            active.Objective = objective;
            active.Item = SpawnMissionEntity(active, item, Transform(objective).Coordinates);
            if (mission.Kind == NCFixerMissionKind.Elimination)
                active.Target = active.Item;
        }

        switch (mission.Kind)
        {
            case NCFixerMissionKind.Delivery when mission.DropAtMarker:
                if (PickMarker(NCMissionMarkerKind.DeadDrop, null, active.Objective) is not { } dropMarker)
                    return false;
                active.Drop = SpawnMissionEntity(active, "NCFixerMissionDeadDrop", Transform(dropMarker).Coordinates);
                break;
            case NCFixerMissionKind.Delivery or NCFixerMissionKind.Retrieval or NCFixerMissionKind.Collection:
                active.Drop = FindFixerDrop(mission.Fixer);
                if (active.Drop == null)
                    return false;
                break;
        }

        if (mission.Encounter is { } encounter && active.Objective is { } place)
            SpawnEncounter(active, encounter, place, taker);

        return true;
    }

    private EntityUid SpawnMissionEntity(ActiveMission active, EntProtoId prototype, EntityCoordinates coordinates)
    {
        var uid = Spawn(prototype, coordinates);
        EnsureComp<NCMissionEntityComponent>(uid).Mission = active.Id;
        active.Spawned.Add(uid);
        return uid;
    }

    /// <summary>Spawns the defenders at guard markers of the objective's owner in the same building, on nearby floors.</summary>
    private void SpawnEncounter(ActiveMission active, ProtoId<NCFixerEncounterPrototype> encounterId, EntityUid objective, EntityUid taker)
    {
        if (!_prototypes.TryIndex(encounterId, out var encounter))
            return;

        var owner = CompOrNull<NCMissionMarkerComponent>(objective)?.Owner;
        var posts = new List<EntityUid>();
        var query = EntityQueryEnumerator<NCMissionMarkerComponent>();
        while (query.MoveNext(out var uid, out var marker))
        {
            if (marker.Kind == NCMissionMarkerKind.Guard && (owner == null || marker.Owner == owner) && IsNearby(objective, uid))
                posts.Add(uid);
        }

        var count = 0;
        var level = _reputation.TryGetProfile(taker, out var profile) ? _reputation.GetLevel(_reputation.GetReputation(profile)) : 0;
        var extra = (int) MathF.Floor(level * encounter.ExtraPerLevel);
        var mobs = new List<EntProtoId>();
        foreach (var entry in encounter.Entries)
        {
            var amount = _random.Next(entry.Min, entry.Max + 1);
            for (var i = 0; i < amount; i++)
                mobs.Add(entry.Mob);
        }

        for (var i = 0; i < extra && encounter.Entries.Count > 0; i++)
            mobs.Add(_random.Pick(encounter.Entries).Mob);

        foreach (var mob in mobs)
        {
            var at = posts.Count > 0
                ? Transform(posts[count++ % posts.Count]).Coordinates
                : Transform(objective).Coordinates.Offset(new System.Numerics.Vector2(_random.Next(-2, 3), _random.Next(-2, 3)));
            SpawnMissionEntity(active, mob, at);
        }
    }

    /// <summary>
    /// Whether a guard post serves the objective: on the city network, within range on the plan and a couple of
    /// floors up or down; off the network, on the same map and within range.
    /// </summary>
    private bool IsNearby(EntityUid objective, EntityUid post)
    {
        if (_mapCoordinates.TryGetCoordinates(objective, out var origin) &&
            _mapCoordinates.TryGetCoordinates(post, out var coordinates))
        {
            return coordinates.NetworkId == origin.NetworkId &&
                   Math.Abs(coordinates.Z - origin.Z) <= GuardFloorRange &&
                   (coordinates.Position - origin.Position).Length() <= GuardMarkerRange;
        }

        var a = _transform.GetMapCoordinates(objective);
        var b = _transform.GetMapCoordinates(post);
        return a.MapId == b.MapId && (a.Position - b.Position).Length() <= GuardMarkerRange;
    }

    private string Name(EntityUid uid)
    {
        return MetaData(uid).EntityName;
    }

    #endregion

    #region Progress

    private void CheckMission(ActiveMission active, TimeSpan now)
    {
        if (!_prototypes.TryIndex(active.Mission, out var mission))
        {
            Finish(active);
            return;
        }

        if (now >= active.Deadline)
        {
            Fail(active, mission, Loc.GetString("nc-fixer-mission-expired", ("mission", Loc.GetString(mission.Name))));
            return;
        }

        switch (mission.Kind)
        {
            case NCFixerMissionKind.Delivery:
            case NCFixerMissionKind.Retrieval:
                if (active.Item is { } item && active.Drop is { } drop && !Deleted(drop) && IsInside(item, drop))
                {
                    QueueDel(item);
                    Complete(active, mission);
                }
                else if (active.Item is { } lost && Deleted(lost))
                {
                    Fail(active, mission, Loc.GetString("nc-fixer-mission-item-lost", ("mission", Loc.GetString(mission.Name))));
                }
                break;
            case NCFixerMissionKind.Sabotage:
                if (active.Item is not { } targetObject || TerminatingOrDeleted(targetObject))
                    Complete(active, mission);
                break;
            case NCFixerMissionKind.Elimination:
                if (active.Target is not { } target || TerminatingOrDeleted(target) || _mobState.IsDead(target))
                    Complete(active, mission);
                break;
            case NCFixerMissionKind.Collection:
                if (active.Drop is { } collectionDrop && !Deleted(collectionDrop))
                {
                    var match = _goods.Match(mission.Requirements, _goods.GetContents(collectionDrop));
                    if (match.TrueForAll(entry => entry.Progress.Have >= entry.Progress.Need))
                    {
                        _goods.Consume(match);
                        Complete(active, mission);
                    }
                }
                break;
        }
    }

    private bool IsInside(EntityUid item, EntityUid container)
    {
        if (TerminatingOrDeleted(item))
            return false;

        var parent = Transform(item).ParentUid;
        while (parent.IsValid())
        {
            if (parent == container)
                return true;
            if (HasComp<MobStateComponent>(parent))
                return false;
            parent = Transform(parent).ParentUid;
        }

        return false;
    }

    private void Complete(ActiveMission active, NCFixerMissionPrototype mission)
    {
        _reputation.AddReputation(active.ProfileId, mission.ReputationGain);
        var payment = mission.Payment ?? _prototypes.Index(active.Fixer).Payment;
        var name = Loc.GetString(mission.Name);

        if (payment == NCFixerPayment.Transfer && _bank.TryPayFixer(active.ProfileId, mission.Reward, mission.ID))
        {
            Notify(active, Loc.GetString("nc-fixer-mission-paid-transfer", ("mission", name), ("reward", mission.Reward)));
            Finish(active);
            return;
        }

        if (payment == NCFixerPayment.DeadDrop && FindFixerDrop(active.Fixer) is { } drop)
        {
            var cash = _bank.SpawnCash(Transform(drop).Coordinates, (int) mission.Reward);
            if (!_entityStorage.Insert(cash, drop))
                _transform.SetCoordinates(cash, Transform(drop).Coordinates);
            Notify(active, Loc.GetString("nc-fixer-mission-paid-drop", ("mission", name), ("reward", mission.Reward)));
            Finish(active);
            return;
        }

        // In person, or the other ways were not possible: the fixer pays when the taker comes back.
        active.Status = NCFixerMissionStatus.AwaitingPayment;
        Notify(active, Loc.GetString("nc-fixer-mission-come-back", ("mission", name)));
        Cleanup(active);
    }

    private void Fail(ActiveMission active, NCFixerMissionPrototype mission, string message)
    {
        _reputation.AddReputation(active.ProfileId, -mission.ReputationLoss);
        Notify(active, message);
        Finish(active);
    }

    private void Finish(ActiveMission active)
    {
        _active.Remove(active.ProfileId);
        Cleanup(active);
    }

    private void Cleanup(ActiveMission active)
    {
        foreach (var uid in active.Spawned)
        {
            if (!Deleted(uid))
                _cleanup.Add(uid);
        }

        active.Spawned.Clear();
    }

    /// <summary>Deletes leftover mission entities once no player is close enough to see it happen.</summary>
    private void RunCleanup()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            var uid = _cleanup[i];
            if (TerminatingOrDeleted(uid))
            {
                _cleanup.RemoveAt(i);
                continue;
            }

            if (IsHeldByPlayer(uid) || PlayerNearby(uid))
                continue;

            QueueDel(uid);
            _cleanup.RemoveAt(i);
        }
    }

    private bool IsHeldByPlayer(EntityUid uid)
    {
        var parent = Transform(uid).ParentUid;
        while (parent.IsValid())
        {
            if (HasComp<ActorComponent>(parent))
                return true;
            parent = Transform(parent).ParentUid;
        }

        return false;
    }

    private bool PlayerNearby(EntityUid uid)
    {
        var position = _transform.GetMapCoordinates(uid);
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { } mob)
                continue;

            var other = _transform.GetMapCoordinates(mob);
            if (other.MapId == position.MapId && (other.Position - position.Position).Length() < CleanupPlayerRange)
                return true;
        }

        return false;
    }

    private void Notify(ActiveMission active, string message)
    {
        if (TryComp<MindComponent>(active.Mind, out var mind) && mind.UserId is { } userId &&
            _players.TryGetSessionById(userId, out var session))
        {
            _chat.DispatchServerMessage(session, message);
        }
    }

    #endregion

    #region Fixer conversation

    private void OnAbandon(Entity<NCFixerComponent> ent, ref NCFixerAbandonMessage args)
    {
        if (_reputation.TryGetProfile(args.Actor, out var profileId) &&
            _active.TryGetValue(profileId, out var active) &&
            active.Fixer == ent.Comp.Fixer &&
            active.Status == NCFixerMissionStatus.Active &&
            _prototypes.TryIndex(active.Mission, out var mission))
        {
            Fail(active, mission, Loc.GetString("nc-fixer-mission-abandoned", ("mission", Loc.GetString(mission.Name))));
        }

        UpdateUi(ent);
    }

    private void OnCollectPayment(Entity<NCFixerComponent> ent, ref NCFixerCollectPaymentMessage args)
    {
        TryCollectPayment(ent, args.Actor);
        UpdateUi(ent);
    }

    /// <summary>Pays an in-person reward when the taker comes back to the fixer who gave the job.</summary>
    public bool TryCollectPayment(Entity<NCFixerComponent> ent, EntityUid user)
    {
        if (!_reputation.TryGetProfile(user, out var profileId) ||
            !_active.TryGetValue(profileId, out var active) ||
            active.Fixer != ent.Comp.Fixer ||
            active.Status != NCFixerMissionStatus.AwaitingPayment ||
            !_prototypes.TryIndex(active.Mission, out var mission))
        {
            return false;
        }

        _bank.GiveCashTo(user, (int) mission.Reward);
        _popup.PopupEntity(Loc.GetString("nc-fixer-paid-in-person", ("reward", mission.Reward)), ent, user);
        Finish(active);
        return true;
    }

    /// <summary>The status of a character's current mission, if it has one.</summary>
    public bool TryGetActiveMission(int profileId, out ProtoId<NCFixerMissionPrototype> mission, out NCFixerMissionStatus status)
    {
        mission = default;
        status = default;
        if (!_active.TryGetValue(profileId, out var active))
            return false;

        mission = active.Mission;
        status = active.Status;
        return true;
    }

    /// <summary>Entities spawned for a character's current mission: the item or target first, then defenders.</summary>
    public IReadOnlyList<EntityUid> GetMissionEntities(int profileId)
    {
        return _active.TryGetValue(profileId, out var active) ? active.Spawned : [];
    }

    private void OnUiOpened(Entity<NCFixerComponent> ent, ref BoundUIOpenedEvent args)
    {
        var greetings = _prototypes.Index(ent.Comp.Fixer).Greetings.Count;
        _greetings[ent.Owner] = greetings > 0 ? _random.Next(greetings) : 0;
        UpdateUi(ent);
    }

    private void UpdateUi(Entity<NCFixerComponent> ent)
    {
        foreach (var actor in _ui.GetActors(ent.Owner, NCFixerUiKey.Key).ToList())
        {
            _reputation.TryGetProfile(actor, out var profileId);
            var reputation = _reputation.GetReputation(profileId);
            var offers = GetOffers(ent.Comp.Fixer);
            var lines = new List<NCFixerOffer>();
            for (var slot = 0; slot < offers.Count; slot++)
            {
                if (offers[slot] is { } id && _prototypes.TryIndex(id, out var prototype))
                    lines.Add(new NCFixerOffer(slot, id, reputation < prototype.MinimumReputation));
            }

            NCFixerActiveMission? current = null;
            var here = false;
            if (_active.TryGetValue(profileId, out var active))
            {
                here = active.Fixer == ent.Comp.Fixer;
                current = new NCFixerActiveMission(
                    active.Mission,
                    active.Fixer,
                    active.Status,
                    active.Status == NCFixerMissionStatus.Active ? active.Deadline - _timing.CurTime : TimeSpan.Zero,
                    Describe(active.Objective),
                    Describe(active.Drop),
                    active.TargetName);
            }

            _ui.SetUiState(ent.Owner, NCFixerUiKey.Key, new NCFixerUiState(
                ent.Comp.Fixer,
                _greetings.GetValueOrDefault(ent.Owner),
                reputation,
                _reputation.GetLevel(reputation),
                lines,
                current,
                here));
        }
    }

    /// <summary>Shows the taker where the mission objective and drop are on the city map.</summary>
    private void OnCollectMarkers(Entity<MobStateComponent> ent, ref NCCityMapCollectMarkersEvent args)
    {
        if (!_reputation.TryGetProfile(ent, out var profileId) ||
            !_active.TryGetValue(profileId, out var active) ||
            active.Status != NCFixerMissionStatus.Active)
        {
            return;
        }

        // Player targets are hunted, not tracked: only mapped places get a marker.
        AddMarker(args.Markers, active.Objective, "nc-fixer-map-objective");
        AddMarker(args.Markers, active.Drop, "nc-fixer-map-drop");
    }

    private void AddMarker(List<Content.Shared._NC.City.Map.NCCityMapMarker> markers, EntityUid? place, string label)
    {
        if (place is not { } uid || Deleted(uid) || !_mapCoordinates.TryGetCoordinates(uid, out var coordinates))
            return;

        markers.Add(new Content.Shared._NC.City.Map.NCCityMapMarker(
            Loc.GetString(label),
            coordinates.NetworkId.ToString(),
            coordinates.Z,
            coordinates.Position,
            true));
    }

    #endregion
}
