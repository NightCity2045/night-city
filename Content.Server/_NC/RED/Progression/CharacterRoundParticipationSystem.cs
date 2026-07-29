// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Server.Afk;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.Identity;
using Content.Server._NC.Persistence;
using Content.Shared._NC.CCVar;
using Content.Shared._NC.Persistence.Components;
using Content.Shared._NC.RED.Progression;
using Content.Shared.GameTicking;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using System.Threading.Tasks;

namespace Content.Server._NC.RED.Progression;

/// <summary>
/// Accumulates active play in memory and periodically commits idempotent round participation.
/// </summary>
public sealed partial class CharacterRoundParticipationSystem : EntitySystem
{
    private static readonly ProtoId<NCRedProgressionPrototype> DefaultProgression = "NCDefaultProgression";

    [Dependency] private IAfkManager _afk = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private CharacterIdentitySystem _identity = default!;
    [Dependency] private CharacterProgressionSystem _progression = default!;
    [Dependency] private GameTicker _ticker = default!;

    private readonly Dictionary<int, ParticipationRuntime> _runtime = new();
    private float _secondAccumulator;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CharacterPersistentStateLoadedEvent>(OnCharacterLoaded);
        SubscribeLocalEvent<RoundEndMessageEvent>(OnRoundEnd);
        SubscribeLocalEvent<PlayerJoinedLobbyEvent>(OnJoinedLobby);
    }

    private void OnCharacterLoaded(ref CharacterPersistentStateLoadedEvent args)
    {
        _runtime[args.ProfileId.Value] = new ParticipationRuntime(
            args.AccountId,
            args.Mind,
            args.Character);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _secondAccumulator += frameTime;
        if (_secondAccumulator < 1f)
            return;

        var elapsedSeconds = Math.Max((int) _secondAccumulator, 1);
        _secondAccumulator -= elapsedSeconds;
        var saveInterval = Math.Max(
            _configuration.GetCVar(NCCVars.ParticipationSaveIntervalSeconds),
            1);

        foreach (var (profileId, runtime) in _runtime)
        {
            if (!_players.TryGetSessionById(runtime.AccountId, out var session) ||
                session.Status != SessionStatus.InGame ||
                session.AttachedEntity is not { } attached ||
                _afk.IsAfk(session) ||
                !_identity.TryGetIdentity(attached, out var attachedProfile, out _) ||
                attachedProfile.Value != profileId)
            {
                if (runtime.PendingSeconds > 0)
                    Flush(profileId, runtime);
                continue;
            }

            runtime.PendingSeconds += elapsedSeconds;
            if (runtime.PendingSeconds >= saveInterval)
                Flush(profileId, runtime);
        }
    }

    private void OnRoundEnd(RoundEndMessageEvent args)
    {
        foreach (var (profileId, runtime) in _runtime)
            Flush(profileId, runtime);
    }

    private void OnJoinedLobby(PlayerJoinedLobbyEvent args)
    {
        foreach (var (profileId, runtime) in _runtime)
        {
            if (runtime.AccountId != args.PlayerSession.UserId)
                continue;

            Flush(profileId, runtime);
            break;
        }
    }

    private void Flush(int profileId, ParticipationRuntime runtime)
    {
        if (runtime.Flushing || runtime.PendingSeconds <= 0 || _ticker.RoundId <= 0)
            return;

        var seconds = runtime.PendingSeconds;
        runtime.PendingSeconds = 0;
        runtime.Flushing = true;
        _ = FlushAsync(profileId, runtime, seconds);
    }

    private async Task FlushAsync(int profileId, ParticipationRuntime runtime, int seconds)
    {
        try
        {
            var progression = _prototypes.Index(DefaultProgression);
            var result = await _database.AddNCActiveSecondsAsync(
                profileId,
                runtime.AccountId.UserId,
                _ticker.RoundId,
                seconds,
                Math.Max(_configuration.GetCVar(NCCVars.RoundCreditActiveSeconds), 1),
                progression.CompletedRoundThresholds,
                Guid.NewGuid());

            if (!Exists(runtime.Mind) ||
                !TryComp<CharacterPersistentStateComponent>(runtime.Mind, out var state))
            {
                return;
            }

            state.CompletedRounds = result.CompletedRounds;
            state.Level = result.Level;
            _progression.SendState(runtime.AccountId, state);

            if (result.LeveledUp &&
                _players.TryGetSessionById(runtime.AccountId, out var session))
            {
                _chat.DispatchServerMessage(session, Loc.GetString(
                    "nc-progression-level-up",
                    ("level", result.Level),
                    ("points", progression.GetTotalSkillPoints(result.Level))));
            }
        }
        catch (Exception exception)
        {
            Logger.ErrorS(
                "nc.progression",
                $"Failed to persist {seconds} active seconds for profile {profileId}: {exception}");
            runtime.PendingSeconds += seconds;
        }
        finally
        {
            runtime.Flushing = false;
        }
    }

    private sealed class ParticipationRuntime(
        Robust.Shared.Network.NetUserId accountId,
        EntityUid mind,
        EntityUid character)
    {
        public Robust.Shared.Network.NetUserId AccountId { get; } = accountId;
        public EntityUid Mind { get; } = mind;
        public EntityUid Character { get; } = character;
        public int PendingSeconds;
        public bool Flushing;
    }
}
