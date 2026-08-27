// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Events;
using Content.Server.Preferences.Managers;
using Content.Shared.GameTicking;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Roles;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Bank;

/// <summary>
/// Runs organization-funded payroll for living characters who are actively present in the round.
/// Employment and account ownership are always resolved through the selected CharacterId.
/// </summary>
public sealed partial class NCPaydaySystem : EntitySystem
{
    [Dependency] private NCBankSystem _bank = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private GameTicker _gameTicker = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private readonly Dictionary<ProtoId<JobPrototype>, ProtoId<DepartmentPrototype>> _organizationsByJob = new();
    private float _elapsed;
    private bool _paydayRunning;

    public override void Initialize()
    {
        base.Initialize();

        BuildOrganizationIndex();
        SubscribeLocalEvent<RoundStartingEvent>(OnRoundStarting);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_gameTicker.RunLevel != GameRunLevel.InRound || _paydayRunning)
            return;

        _elapsed += frameTime;
        var interval = Math.Max(1f, _bank.Configuration.PaydayIntervalSeconds);
        if (_elapsed < interval)
            return;

        _elapsed %= interval;
        _paydayRunning = true;
        ProcessPaydayAsync();
    }

    private void BuildOrganizationIndex()
    {
        _organizationsByJob.Clear();
        foreach (var department in _prototypes.EnumeratePrototypes<DepartmentPrototype>())
        {
            if (!department.NCSelectable)
                continue;

            foreach (var job in department.Roles)
                _organizationsByJob[job] = department.ID;
        }
    }

    private async void OnRoundStarting(RoundStartingEvent args)
    {
        _elapsed = 0f;
        BuildOrganizationIndex();

        // Create every configured organization account even if that faction has no employees this round.
        foreach (var department in _prototypes.EnumeratePrototypes<DepartmentPrototype>())
        {
            if (!department.NCSelectable || department.StartingBudget < 0)
                continue;

            try
            {
                await _bank.EnsureOrganizationAccountAsync(department.ID, department.StartingBudget);
            }
            catch (Exception exception)
            {
                Log.Error($"Failed to initialize organization account {department.ID}: {exception}");
            }
        }
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _elapsed = 0f;
    }

    private async void ProcessPaydayAsync()
    {
        try
        {
            // Stable ordering makes low-budget payroll deterministic instead of depending on session enumeration order.
            var sessions = _players.Sessions
                .Where(session => session.Status == SessionStatus.InGame)
                .OrderBy(session => session.UserId.ToString())
                .ToArray();

            foreach (var session in sessions)
            {
                if (_gameTicker.RunLevel != GameRunLevel.InRound)
                    break;

                await TryPaySessionAsync(session);
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Organization payday failed: {exception}");
        }
        finally
        {
            _paydayRunning = false;
        }
    }

    private async Task TryPaySessionAsync(ICommonSession session)
    {
        if (session.AttachedEntity is not { Valid: true } entity ||
            !TryComp<MobStateComponent>(entity, out var mobState) ||
            mobState.CurrentState is not (MobState.Alive or MobState.Critical) ||
            !_preferences.TryGetSelectedNCCharacterId(session.UserId, out var characterId) ||
            !_preferences.TryGetSelectedNCEmployment(session.UserId, out var jobId) ||
            !_organizationsByJob.TryGetValue(jobId, out var departmentId) ||
            !_prototypes.TryIndex(jobId, out JobPrototype? job) || job.Salary <= 0 ||
            !_prototypes.TryIndex(departmentId, out DepartmentPrototype? department))
        {
            return;
        }

        var payment = await _bank.TryPaySalaryAsync(
            characterId,
            department.ID,
            department.StartingBudget,
            job.Salary,
            job.ID);

        switch (payment.Result)
        {
            case NCSalaryPaymentResult.Success:
                _chat.DispatchServerMessage(session,
                    Loc.GetString("nc-payday-paid", ("amount", job.Salary),
                        ("organization", Loc.GetString(department.Name))));
                break;
            case NCSalaryPaymentResult.InsufficientOrganizationFunds:
                _chat.DispatchServerMessage(session,
                    Loc.GetString("nc-payday-insufficient-funds",
                        ("organization", Loc.GetString(department.Name))));
                break;
            default:
                Log.Error($"Salary payment for character {characterId.Value} failed with {payment.Result}.");
                break;
        }
    }
}
