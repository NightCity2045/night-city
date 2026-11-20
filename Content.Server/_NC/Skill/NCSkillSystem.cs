// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.Identity;
using Content.Server.Afk;
using Content.Server.Database;
using Content.Server.EUI;
using Content.Server.GameTicking;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Skill;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Players;
using Content.Shared.Popups;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Asynchronous;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Skill;

public sealed partial class NCSkillSystem : EntitySystem
{
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IAfkManager _afk = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private EuiManager _eui = default!;
    [Dependency] private ITaskManager _tasks = default!;

    private readonly Dictionary<NCCharacterId, Progress> _characters = new();
    private readonly Dictionary<EntityUid, Progress> _bodies = new();
    private readonly Dictionary<ICommonSession, NCSkillsEui> _windows = new();
    private readonly HashSet<Task> _pending = new();
    private TimeSpan _nextSample;
    private TimeSpan _nextSave;

    public override void Initialize()
    {
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnSpawn);
        SubscribeLocalEvent<PlayerAttachedEvent>(OnAttached);
        SubscribeLocalEvent<PlayerDetachedEvent>(OnDetached);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<NCSkillPermissionsComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<NCSkillPermissionsComponent, NCPhysiologyChangedEvent>(OnPhysiologyChanged);
        SubscribeLocalEvent<NCRequiredSkillComponent, ShotAttemptedEvent>(OnShootAttempt);
        SubscribeLocalEvent<NCRequiredSkillComponent, NCWeaponUseAttemptEvent>(OnWeaponUseAttempt);
    }

    private void OnSpawn(PlayerSpawnCompleteEvent args)
    {
        if (_preferences.TryGetSelectedNCCharacterId(args.Player.UserId, out var id))
            Bind(id, args.Mob, args.Player);
    }

    private void OnAttached(PlayerAttachedEvent args)
    {
        if (args.Player.GetMind() is { } mind && TryComp<MindComponent>(mind, out var mindComponent) &&
            mindComponent.OwnedEntity == args.Entity && TryComp<NCCharacterIdentityComponent>(mind, out var identity))
        {
            Bind(identity.CharacterId, args.Entity, args.Player);
        }
    }

    private void Bind(NCCharacterId id, EntityUid body, ICommonSession session)
    {
        if (!id.IsValid)
            return;

        if (!_characters.TryGetValue(id, out var progress))
        {
            progress = new Progress(id);
            _characters.Add(id, progress);
        }

        if (progress.Body is { } oldBody && oldBody != body)
        {
            Accrue(progress, _timing.RealTime);
            _bodies.Remove(oldBody);
        }

        progress.Body = body;
        progress.Session = session;
        _bodies[body] = progress;
        Sample(progress, _timing.RealTime);
        if (!progress.Ready && !progress.Loading)
            Track(LoadProgress(progress));

        UpdatePermissions(progress);
    }

    private async Task LoadProgress(Progress progress)
    {
        progress.Loading = true;
        try
        {
            var saved = await _database.GetNCSkillProgressAsync(progress.Id);
            progress.ActiveTicks += saved.ActiveTicks;
            progress.SavedTicks = saved.ActiveTicks;
            progress.Learned.UnionWith(saved.Learned);
            progress.SurvivedRounds += saved.SurvivedRounds;
            progress.LastSurvivedRound ??= saved.LastSurvivedRound;
            progress.SavedSurvivedRound = saved.LastSurvivedRound;
            progress.Ready = true;
            UpdatePermissions(progress);
        }
        catch (Exception e)
        {
            Log.Error($"Unable to load character skills for {progress.Id}: {e}");
        }
        finally
        {
            progress.Loading = false;
        }
    }

    private void OnDetached(PlayerDetachedEvent args)
    {
        if (!_bodies.TryGetValue(args.Entity, out var progress) || progress.Session != args.Player)
            return;

        Accrue(progress, _timing.RealTime);
        progress.Session = null;
        Track(SaveProgress(progress));
        Refresh(args.Player);
    }

    private void OnMobStateChanged(Entity<NCSkillPermissionsComponent> body, ref MobStateChangedEvent args)
    {
        if (!_bodies.TryGetValue(body.Owner, out var progress))
            return;

        Sample(progress, _timing.RealTime);
        Refresh(progress.Session);
    }

    private void OnPhysiologyChanged(Entity<NCSkillPermissionsComponent> entity, ref NCPhysiologyChangedEvent args)
    {
        if (!TryComp<NCPhysiologyComponent>(entity.Owner, out var physiology) || !physiology.Irreversible ||
            !_bodies.TryGetValue(entity.Owner, out var progress))
            return;

        Accrue(progress, _timing.RealTime);
        progress.Dead = true;
        UpdatePermissions(progress);
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent args)
    {
        if (args.New == GameRunLevel.InRound)
        {
            foreach (var progress in _characters.Values)
                Sample(progress, _timing.RealTime);

            return;
        }

        if (args.Old != GameRunLevel.InRound)
            return;

        foreach (var progress in _characters.Values)
        {
            Accrue(progress, _timing.RealTime);
            if (args.New == GameRunLevel.PostRound &&
                progress.Body is { } body && Survived(progress, body) &&
                progress.LastSurvivedRound != _ticker.RoundId)
            {
                progress.SurvivedRounds++;
                progress.LastSurvivedRound = _ticker.RoundId;
            }

            Track(SaveProgress(progress));
        }
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        foreach (var progress in _characters.Values)
        {
            progress.Body = null;
            progress.ActiveSince = null;
            Refresh(progress.Session);
            progress.Session = null;
            Track(SaveProgress(progress));
        }

        _bodies.Clear();
        foreach (var id in _characters.Where(pair => pair.Value.Dead).Select(pair => pair.Key).ToArray())
            _characters.Remove(id);
    }

    public override void Update(float frameTime)
    {
        var now = _timing.RealTime;
        if (now < _nextSample)
            return;

        _nextSample = now + TimeSpan.FromSeconds(1);
        var save = now >= _nextSave;
        if (save)
            _nextSave = now + TimeSpan.FromMinutes(1);

        foreach (var progress in _characters.Values)
        {
            Sample(progress, now);

            if (save)
            {
                if (!progress.Ready && !progress.Loading && progress.Session != null)
                    Track(LoadProgress(progress));
                Track(SaveProgress(progress));
            }

        }
    }

    private void Sample(Progress progress, TimeSpan now)
    {
        Accrue(progress, now);
        if (_ticker.RunLevel == GameRunLevel.InRound &&
            progress.Body is { } body && progress.Session is { } session &&
            session.AttachedEntity == body && IsAlive(progress, body) && !_afk.IsAfk(session))
        {
            progress.ActiveSince = now;
        }
    }

    private void Accrue(Progress progress, TimeSpan now)
    {
        if (progress.ActiveSince is not { } since)
            return;

        progress.ActiveSince = null;
        var previousExperience = NCSkillProgression.Experience(progress.ActiveTicks);
        progress.ActiveTicks += (now - since).Ticks;
        if (previousExperience != NCSkillProgression.Experience(progress.ActiveTicks))
            UpdatePermissions(progress);
    }

    private bool IsAlive(Progress progress, EntityUid body) =>
        !progress.Dead && !TerminatingOrDeleted(body) && _mobState.IsAlive(body) &&
        (!TryComp<NCPhysiologyComponent>(body, out var physiology) || !physiology.Irreversible);

    private bool Survived(Progress progress, EntityUid body)
    {
        if (progress.Dead || TerminatingOrDeleted(body))
            return false;

        return TryComp<NCPhysiologyComponent>(body, out var physiology)
            ? !physiology.Irreversible &&
                physiology.Condition is not (NCPhysiologicalCondition.Dead or NCPhysiologicalCondition.ClinicalArrest)
            : _mobState.IsAlive(body) || _mobState.IsCritical(body);
    }

    public void OpenWindow(ICommonSession player)
    {
        if (_windows.ContainsKey(player))
            return;

        var window = new NCSkillsEui(this);
        _windows.Add(player, window);
        _eui.OpenEui(window, player);
    }

    internal void CloseWindow(ICommonSession player) => _windows.Remove(player);

    private void Refresh(ICommonSession? player)
    {
        if (player != null && _windows.TryGetValue(player, out var window))
            window.StateDirty();
    }

    private void UpdatePermissions(Progress progress)
    {
        if (progress.Body is { } body && !TerminatingOrDeleted(body))
        {
            var permission = EnsureComp<NCSkillPermissionsComponent>(body);
            var available = progress.Ready && !progress.Dead;
            permission.Experience = available ? NCSkillProgression.Experience(progress.ActiveTicks) : -1;
            permission.Learned = available ? progress.Learned.ToArray() : [];
            Dirty(body, permission);
        }

        Refresh(progress.Session);
    }

    private Progress? GetProgress(ICommonSession session) =>
        session.AttachedEntity is { } body && _bodies.TryGetValue(body, out var progress) &&
        progress.Session == session && IsAlive(progress, body) && progress.Ready
            ? progress
            : null;

    internal NCSkillsState GetState(ICommonSession session) => GetProgress(session) is { } progress
        ? new NCSkillsState(NCSkillProgression.Experience(progress.ActiveTicks), progress.Learned.ToArray())
        : new NCSkillsState(-1, []);

    internal Task<bool> Learn(ICommonSession session, string id)
    {
        var progress = GetProgress(session);
        if (progress == null || string.IsNullOrWhiteSpace(id) || id.Length > 128 ||
            !_prototypes.TryIndex<NCSkillPrototype>(id, out var skill) ||
            progress.Learned.Contains(id) || !skill.MeetsPrerequisites(progress.Learned) ||
            NCSkillProgression.Points(NCSkillProgression.Experience(progress.ActiveTicks), progress.Learned.Count) == 0)
        {
            return Task.FromResult(false);
        }

        var task = SaveProgress(progress, id);
        Track(task);
        return task;
    }

    private void Track(Task task)
    {
        _pending.RemoveWhere(existing => existing.IsCompleted);
        if (!task.IsCompleted)
            _pending.Add(task);
    }

    public void FlushProgress()
    {
        foreach (var progress in _characters.Values)
            Accrue(progress, _timing.RealTime);

        _tasks.BlockWaitOnTask(Task.WhenAll(_pending.ToArray()));
        var writes = _characters.Values.Select(progress => SaveProgress(progress)).ToArray();
        _tasks.BlockWaitOnTask(Task.WhenAll(writes));
        _tasks.BlockWaitOnTask(Task.WhenAll(_pending.ToArray()));
    }

    private async Task<bool> SaveProgress(Progress progress, string? purchase = null)
    {
        if (!progress.Ready || progress.Dead)
            return false;

        if (progress.Writing)
        {
            progress.SaveRequested |= purchase == null;
            return false;
        }

        if (purchase == null && progress.ActiveTicks == progress.SavedTicks &&
            progress.LastSurvivedRound == progress.SavedSurvivedRound)
        {
            return true;
        }

        progress.Writing = true;
        var learned = purchase == null ? progress.Learned.ToArray() : [.. progress.Learned, purchase];
        var snapshot = new NCSkillProgressData(progress.ActiveTicks, learned,
            progress.SurvivedRounds, progress.LastSurvivedRound);

        try
        {
            if (!await _database.SaveNCSkillProgressAsync(progress.Id, snapshot) || progress.Dead)
                return false;

            progress.SavedTicks = snapshot.ActiveTicks;
            progress.SavedSurvivedRound = snapshot.LastSurvivedRound;
            if (purchase != null)
                progress.Learned.Add(purchase);

            if (purchase != null)
                UpdatePermissions(progress);

            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Unable to save character skills for {progress.Id}: {e}");
            return false;
        }
        finally
        {
            progress.Writing = false;
            if (progress.SaveRequested)
            {
                progress.SaveRequested = false;
                Track(SaveProgress(progress));
            }
        }
    }

    private void OnShootAttempt(Entity<NCRequiredSkillComponent> weapon, ref ShotAttemptedEvent args)
    {
        if (!args.Cancelled && !TryUseWeapon(weapon.Owner, args.User))
            args.Cancel();
    }

    private void OnWeaponUseAttempt(Entity<NCRequiredSkillComponent> weapon, ref NCWeaponUseAttemptEvent args)
    {
        if (!args.Cancelled && !TryUseWeapon(weapon.Owner, args.User))
            args.Cancelled = true;
    }

    public bool TryUseWeapon(EntityUid weapon, EntityUid user)
    {
        if (!TryComp<NCRequiredSkillComponent>(weapon, out var requirement))
            return true;

        if (!_bodies.TryGetValue(user, out var progress))
        {
            // World machinery and autonomous actors have no player progression.
            if (!HasComp<ActorComponent>(user) && !_mind.TryGetMind(user, out _, out _))
                return true;
        }
        else if (progress.Ready && !progress.Dead && progress.Learned.Contains(requirement.Skill.Id))
        {
            return true;
        }

        if (HasComp<ActorComponent>(user) &&
            (progress == null || _timing.RealTime >= progress.NextNotice))
        {
            if (progress != null)
                progress.NextNotice = _timing.RealTime + TimeSpan.FromSeconds(1);

            _popup.PopupEntity(Loc.GetString("nc-skill-denied"), weapon, user);
        }

        return false;
    }

    private sealed class Progress(NCCharacterId id)
    {
        public readonly NCCharacterId Id = id;
        public readonly HashSet<string> Learned = new(StringComparer.Ordinal);
        public EntityUid? Body;
        public ICommonSession? Session;
        public TimeSpan? ActiveSince;
        public TimeSpan NextNotice;
        public long ActiveTicks;
        public long SavedTicks;
        public int SurvivedRounds;
        public int? LastSurvivedRound;
        public int? SavedSurvivedRound;
        public bool Ready;
        public bool Loading;
        public bool Dead;
        public bool Writing;
        public bool SaveRequested;
    }
}
