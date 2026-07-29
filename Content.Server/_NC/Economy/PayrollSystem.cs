// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.Organizations;
using Content.Server._NC.Persistence;
using Content.Server._NC.RED.Progression;
using Content.Shared._NC.Organizations;
using Content.Shared._NC.Persistence.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using System.Linq;
using System.Threading.Tasks;

namespace Content.Server._NC.Economy;

/// <summary>
/// Pays online active employees on the interval configured by their position prototype.
/// Each payout has a unique request identifier and an immutable bank transaction.
/// </summary>
public sealed partial class PayrollSystem : EntitySystem
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private CharacterProgressionSystem _progression = default!;

    private readonly Dictionary<int, PayrollEntry> _entries = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CharacterPersistentStateLoadedEvent>(OnLoaded);
        SubscribeLocalEvent<NCEmploymentChangedEvent>(OnEmploymentChanged);
    }

    private void OnLoaded(ref CharacterPersistentStateLoadedEvent args)
    {
        Refresh(args.ProfileId.Value, args.AccountId, args.Mind, args.Snapshot.Employment);
    }

    private void OnEmploymentChanged(ref NCEmploymentChangedEvent args)
    {
        if (TryComp<Content.Shared._NC.Identity.Components.CharacterIdentityComponent>(
                args.Mind,
                out var identity))
        {
            Refresh(args.ProfileId, identity.AccountId, args.Mind, args.Employment);
        }
    }

    private void Refresh(
        int profileId,
        NetUserId accountId,
        EntityUid mind,
        NCCharacterEmployment? employment)
    {
        if (employment == null ||
            employment.EmploymentState is not
                (NCEmploymentState.Active or NCEmploymentState.SuspendedPaid) ||
            !TryGetPosition(employment.PositionId, out var position) ||
            position.BaseSalary <= 0 ||
            position.PayIntervalSeconds <= 0)
        {
            _entries.Remove(profileId);
            return;
        }

        _entries[profileId] = new PayrollEntry(
            mind,
            accountId,
            position.PositionId,
            _timing.CurTime + TimeSpan.FromSeconds(position.PayIntervalSeconds),
            false);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        foreach (var (profileId, entry) in _entries.ToArray())
        {
            if (entry.InFlight || _timing.CurTime < entry.NextPayment)
                continue;
            if (!_players.TryGetSessionById(entry.AccountId, out var session) ||
                session.Status != SessionStatus.InGame ||
                !Exists(entry.Mind) ||
                !TryComp<CharacterPersistentStateComponent>(entry.Mind, out var state) ||
                !TryGetPosition(entry.PositionId, out var position))
            {
                _entries.Remove(profileId);
                continue;
            }

            _entries[profileId] = entry with { InFlight = true };
            _ = PayAsync(profileId, entry.Mind, entry.AccountId, position);
        }
    }

    private async Task PayAsync(
        int profileId,
        EntityUid mind,
        NetUserId accountId,
        NCPositionPrototype position)
    {
        var result = await _database.PayNCPositionSalaryAsync(
            profileId,
            position.PositionId,
            $"position:{position.ID}",
            _ticker.RoundId,
            Guid.NewGuid());
        if (result.Success &&
            Exists(mind) &&
            TryComp<CharacterPersistentStateComponent>(mind, out var state))
        {
            state.PersonalBalance = result.CreditBalance;
            _progression.SendState(accountId, state);
        }

        if (_entries.TryGetValue(profileId, out var entry))
        {
            _entries[profileId] = entry with
            {
                NextPayment = _timing.CurTime + TimeSpan.FromSeconds(position.PayIntervalSeconds),
                InFlight = false,
            };
        }
    }

    private bool TryGetPosition(Guid positionId, out NCPositionPrototype position)
    {
        position = _prototypes.EnumeratePrototypes<NCPositionPrototype>()
            .FirstOrDefault(p => p.PositionId == positionId)!;
        return position != null;
    }

    private sealed record PayrollEntry(
        EntityUid Mind,
        NetUserId AccountId,
        Guid PositionId,
        TimeSpan NextPayment,
        bool InFlight);
}
