// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.Server._NC.Bank;
using Content.Shared.Mind.Components;
using Content.Server._NC.City.Atlas;
using Content.Server._NC.Corporations;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Shared._NC.City.Map;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.Fixers;
using Content.Shared.DoAfter;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
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
    private const float ReconRange = 2f;

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
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private NCMapCoordinatesSystem _mapCoordinates = default!;
    [Dependency] private NCCityAtlasSystem _atlas = default!;
    [Dependency] private NCBankSystem _bank = default!;
    [Dependency] private NCCorpContractSystem _goods = default!;
    [Dependency] private NCStreetReputationSystem _reputation = default!;
    [Dependency] private NCMissionGuardSystem _guards = default!;
    [Dependency] private NCAmbientDefenseSystem _ambient = default!;

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
        public EntityUid? Terminal;
        public string? TargetName;
        public readonly List<EntityUid> ReconTargets = new();
        public readonly HashSet<EntityUid> ReconVisited = new();
        public string? LaunderTarget;
        public long Laundered;
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
        SubscribeLocalEvent<NCMissionTerminalComponent, ActivateInWorldEvent>(OnTerminalActivated);
        SubscribeLocalEvent<NCMissionTerminalComponent, NCMissionHackDoAfterEvent>(OnTerminalHacked);
        SubscribeLocalEvent<NCBankTransferredEvent>(OnBankTransferred);
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
        else if (mission.Kind == NCFixerMissionKind.Delivery && mission.ItemFromFixer)
        {
            if (mission.Item == null)
                return false;
        }
        else if (mission.Kind == NCFixerMissionKind.Recon)
        {
            if (PickMarker(NCMissionMarkerKind.Objective, mission.Owner, null) == null)
                return false;
        }
        else if (mission.Kind == NCFixerMissionKind.Laundering)
        {
            if (mission.LaunderAmount <= 0 || mission.LaunderAccount is not { } shell || !_bank.TryGetDepartmentAccount(shell, out _))
                return false;
        }
        else if (mission.Kind != NCFixerMissionKind.Collection &&
                 (mission.Item == null || PickMarker(NCMissionMarkerKind.Objective, mission.Owner, null) == null))
        {
            return false;
        }

        return mission.Kind switch
        {
            NCFixerMissionKind.Delivery when mission.DropAtMarker => PickMarker(NCMissionMarkerKind.DeadDrop, DropOwner(mission), null) != null,
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

    /// <summary>Goods the fixer hands over go to a buyer on the mission owner's ground; other drops are anyone's.</summary>
    private static ProtoId<NCMissionOwnerPrototype>? DropOwner(NCFixerMissionPrototype mission)
    {
        return mission.ItemFromFixer ? mission.Owner : null;
    }

    /// <summary>
    /// Whether the user may take this fixer's jobs: anyone for a street fixer, only the employer's staff for a
    /// corporate job board.
    /// </summary>
    public bool CanWorkFor(ProtoId<NCFixerPrototype> fixer, EntityUid user)
    {
        if (!_prototypes.TryIndex(fixer, out var prototype) || prototype.Employer is not { } employer)
            return true;

        return _prototypes.TryIndex(employer, out var account) &&
               account.Department is { } departmentId &&
               _prototypes.TryIndex(departmentId, out var department) &&
               _minds.TryGetMind(user, out var mindId, out _) &&
               _jobs.MindTryGetJobId(mindId, out var job) &&
               job is { } jobId &&
               department.Roles.Contains(jobId);
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

        if (!CanWorkFor(fixer.Comp.Fixer, user))
        {
            _popup.PopupEntity(Loc.GetString("nc-fixer-not-employee"), fixer, user);
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
        else if (mission.Kind == NCFixerMissionKind.Delivery && mission.ItemFromFixer)
        {
            // The fixer hands the goods over: straight into the taker's hands, or at their feet.
            if (mission.Item is not { } cargo)
                return false;

            active.Item = SpawnMissionEntity(active, cargo, Transform(taker).Coordinates);
            _hands.TryPickupAnyHand(taker, active.Item.Value);
        }
        else if (mission.Kind == NCFixerMissionKind.Recon)
        {
            if (!TryStartRecon(active, mission, taker))
                return false;
        }
        else if (mission.Kind == NCFixerMissionKind.Laundering)
        {
            if (!TryStartLaundering(active, mission))
                return false;
        }
        else if (mission.Kind == NCFixerMissionKind.Retrieval && mission.Terminal is { } terminal)
        {
            // The item does not exist yet: it comes out of the terminal once someone hacks it.
            if (PickMarker(NCMissionMarkerKind.Objective, mission.Owner, null) is not { } objective)
                return false;

            active.Objective = objective;
            active.Terminal = SpawnMissionEntity(active, terminal, Transform(objective).Coordinates);
            EnsureComp<NCMissionTerminalComponent>(active.Terminal.Value);
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
            if (mission.Kind is NCFixerMissionKind.Elimination or NCFixerMissionKind.Beating)
            {
                active.Target = active.Item;
                _guards.MakeGuard(active.Item.Value, active.Id,
                    mission.Encounter is { } targetEncounter ? _prototypes.Index(targetEncounter) : null);
            }
        }

        switch (mission.Kind)
        {
            case NCFixerMissionKind.Delivery when mission.DropAtMarker:
                if (PickMarker(NCMissionMarkerKind.DeadDrop, DropOwner(mission), active.Objective) is not { } dropMarker)
                    return false;
                active.Drop = SpawnMissionEntity(active, "NCFixerMissionDeadDrop", Transform(dropMarker).Coordinates);
                // Goods handed over by the fixer have no objective: the buyers wait around the drop.
                if (mission.ItemFromFixer && mission.Encounter is { } buyers)
                    SpawnEncounter(active, buyers, dropMarker, taker);
                break;
            case NCFixerMissionKind.Delivery or NCFixerMissionKind.Retrieval or NCFixerMissionKind.Collection:
                active.Drop = FindFixerDrop(mission.Fixer);
                if (active.Drop == null)
                    return false;
                break;
        }

        if (mission.Kind != NCFixerMissionKind.Recon && mission.Encounter is { } encounter && active.Objective is { } place)
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

        // People already standing at the place without a mission become the mission's defenders.
        if (_ambient.TryAdopt(objective, active.Id, active.Spawned))
            return;

        var level = _reputation.TryGetProfile(taker, out var profile) ? _reputation.GetLevel(_reputation.GetReputation(profile)) : 0;
        var extra = (int) MathF.Floor(level * encounter.ExtraPerLevel);
        active.Spawned.AddRange(_guards.SpawnDefenders(objective, encounter, active.Id, extra));
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

        if (mission.FailOnHostile && AnyGuardHostile(active))
        {
            Fail(active, mission, Loc.GetString("nc-fixer-mission-hostile", ("mission", Loc.GetString(mission.Name))));
            return;
        }

        switch (mission.Kind)
        {
            case NCFixerMissionKind.Recon:
                CheckRecon(active, mission);
                break;
            case NCFixerMissionKind.Delivery:
            case NCFixerMissionKind.Retrieval:
                if (active.Item is { } item && active.Drop is { } drop && !Deleted(drop) && IsInside(item, drop))
                {
                    QueueDel(item);
                    Complete(active, mission);
                }
                else if (active.Item is { } lost && Deleted(lost) ||
                         active.Item == null && active.Terminal is { } terminal && TerminatingOrDeleted(terminal))
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
            case NCFixerMissionKind.Beating:
                if (active.Target is not { } victim || TerminatingOrDeleted(victim) || _mobState.IsDead(victim))
                    Fail(active, mission, Loc.GetString("nc-fixer-mission-target-killed", ("mission", Loc.GetString(mission.Name))));
                else if (_mobState.IsCritical(victim))
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

        var fixer = _prototypes.Index(active.Fixer);
        if (payment == NCFixerPayment.Employer && fixer.Employer is { } employer &&
            _bank.TryPayContract(employer, fixer.EmployerCorporation ?? employer, mission.Reward, fixer.EmployerShare, mission.ID, null))
        {
            Notify(active, Loc.GetString("nc-fixer-mission-paid-employer", ("mission", name), ("reward", mission.Reward)));
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
        if (mission.Kind == NCFixerMissionKind.Laundering && active.LaunderTarget != null)
            TakeBackLaundering(active, mission);

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

    #region Terminals

    private void OnTerminalActivated(Entity<NCMissionTerminalComponent> ent, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        args.Handled = true;
        TryStartHack(ent, args.User);
    }

    /// <summary>Starts hacking a mission terminal. Anyone can do it; the shard goes to whoever finishes.</summary>
    public bool TryStartHack(Entity<NCMissionTerminalComponent> ent, EntityUid user)
    {
        if (ent.Comp.Hacked || FindMissionByTerminal(ent) == null)
        {
            _popup.PopupEntity(Loc.GetString("nc-mission-terminal-empty"), ent, user);
            return false;
        }

        if (!_doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, ent.Comp.HackTime, new NCMissionHackDoAfterEvent(), ent, ent)
            {
                BreakOnMove = true,
                BreakOnDamage = true,
                NeedHand = true,
            }))
        {
            return false;
        }

        _popup.PopupEntity(Loc.GetString("nc-mission-terminal-hacking"), ent, user);
        return true;
    }

    private void OnTerminalHacked(Entity<NCMissionTerminalComponent> ent, ref NCMissionHackDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;

        args.Handled = true;
        CompleteHack(ent, args.User);
    }

    /// <summary>Finishes a hack: the mission's item comes out of the terminal into the hacker's hands.</summary>
    public bool CompleteHack(Entity<NCMissionTerminalComponent> ent, EntityUid user)
    {
        if (ent.Comp.Hacked || FindMissionByTerminal(ent) is not { } active ||
            !_prototypes.TryIndex(active.Mission, out var mission) || mission.Item is not { } item)
        {
            return false;
        }

        ent.Comp.Hacked = true;
        active.Item = SpawnMissionEntity(active, item, Transform(ent).Coordinates);
        _hands.TryPickupAnyHand(user, active.Item.Value);
        _popup.PopupEntity(Loc.GetString("nc-mission-terminal-hacked"), ent, user);
        return true;
    }

    private ActiveMission? FindMissionByTerminal(EntityUid terminal)
    {
        foreach (var active in _active.Values)
        {
            if (active.Terminal == terminal && active.Status == NCFixerMissionStatus.Active)
                return active;
        }

        return null;
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
            var employee = CanWorkFor(ent.Comp.Fixer, actor);
            var lines = new List<NCFixerOffer>();
            for (var slot = 0; slot < offers.Count; slot++)
            {
                if (offers[slot] is { } id && _prototypes.TryIndex(id, out var prototype))
                    lines.Add(new NCFixerOffer(slot, id, !employee || reputation < prototype.MinimumReputation));
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
        if (active.ReconTargets.Count > 0)
        {
            foreach (var point in active.ReconTargets)
            {
                if (!active.ReconVisited.Contains(point))
                    AddMarker(args.Markers, point, "nc-fixer-map-recon");
            }
        }
        else
        {
            AddMarker(args.Markers, active.Objective, "nc-fixer-map-objective");
        }

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
