// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Market;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Employment;

/// <summary>
/// Pays canonical employments from employer accounts on active-world time. The next due time advances atomically with
/// the double-entry salary transaction, so a retry cannot pay twice and offline wall time never creates catch-up pay.
/// </summary>
public sealed partial class NCEmploymentPayrollSystem : EntitySystem
{
    private const int RecoveryPageSize = 100;
    private const int MaximumRecoveredEmployments = 20_000;
    private const int MaximumPaymentsPerFrame = 8;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCEmploymentRegistrySystem _employment = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;
    [Dependency] private NCBusinessRegistrySystem _businesses = default!;

    private readonly PriorityQueue<NCEmploymentId, long> _due = new();
    private readonly HashSet<NCEmploymentId> _queued = [];
    private readonly HashSet<NCEmploymentId> _processing = [];
    private readonly HashSet<Task> _inFlight = [];
    private bool _active;
    private int _generation;

    public int ScheduledPayrollCount => _queued.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
        SubscribeLocalEvent<NCEmploymentPersistedEvent>(OnEmploymentChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || !_clock.TryGetCurrentTime(out _, out var now))
            return;
        var count = 0;
        while (count < MaximumPaymentsPerFrame && _due.TryPeek(out var employmentId, out var ticks) &&
               ticks <= now.Ticks)
        {
            _due.Dequeue();
            _queued.Remove(employmentId);
            if (_processing.Add(employmentId))
                Track(PayAsync(employmentId, _generation));
            count++;
        }
    }

    private async Task PayAsync(NCEmploymentId employmentId, int generation)
    {
        try
        {
            if (!_active || generation != _generation || !_clock.TryGetCurrentTime(out _, out var now))
                return;
            var employment = await _employment.GetAsync(employmentId, true);
            if (employment == null || employment.Snapshot.Status != NCEmploymentStatus.Active ||
                employment.Snapshot.SalaryAmount <= 0 || employment.Snapshot.PayrollIntervalTicks <= 0 ||
                employment.Snapshot.NextPayrollAt is not { } due)
                return;
            if (now < due)
            {
                Enqueue(employmentId, due);
                return;
            }
            var employer = await _ledger.GetAccountAsync(employment.Snapshot.EmployerAccountId, true);
            var payee = await _ledger.GetAccountAsync(employment.Snapshot.PayeeAccountId, true);
            if (employer == null || payee == null)
            {
                Enqueue(employmentId, now.Advance(TimeSpan.FromMinutes(1)));
                return;
            }
            var nextDue = new NCActiveWorldTime(checked(due.Ticks + employment.Snapshot.PayrollIntervalTicks));
            var eventId = Guid.NewGuid();
            var next = employment.Snapshot with
            {
                NextPayrollAt = nextDue,
                ChangedAt = now,
                DataVersion = checked(employment.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            var stable = $"{employmentId}.{due.Ticks}";
            var result = await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
                NCPersistentIdentity.Command("payroll", "pay", stable),
                NCPersistentIdentity.Transaction("payroll", "pay", stable),
                NCLedgerTransactionKind.Salary,
                $"employment:{employmentId}",
                [
                    new NCLedgerLegRequest(employer.Snapshot.AccountId, employer.AggregateVersion,
                        -employment.Snapshot.SalaryAmount),
                    new NCLedgerLegRequest(payee.Snapshot.AccountId, payee.AggregateVersion,
                        employment.Snapshot.SalaryAmount),
                ],
                employment.Snapshot.Employer,
                NCPersistentIdentity.Correlation("payroll", "pay", stable),
                employment.Snapshot.LastAppliedEventId,
                "employment-payroll",
                [new NCAtomicLedgerParticipantWrite(
                    eventId,
                    "living-city.employment.salary-paid",
                    NCEmploymentRegistrySystem.EmploymentAggregateType,
                    employmentId.ToString(),
                    employment.AggregateVersion,
                    NCEmploymentRegistrySystem.SnapshotFormatVersion,
                    JsonSerializer.Serialize(next, JsonOptions),
                    JsonSerializer.Serialize(new PayrollEvent(employmentId, due, employment.Snapshot.SalaryAmount),
                        JsonOptions))]));
            if (result.Status is NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate)
            {
                var loaded = await _employment.GetAsync(employmentId, true);
                if (loaded?.Snapshot.NextPayrollAt is { } loadedDue)
                    Enqueue(employmentId, loadedDue);
                return;
            }
            if (result.Status == NCEconomyMutationStatus.InsufficientFunds)
                await MarkBusinessInsolventAsync(employment);
            Enqueue(employmentId, now.Advance(TimeSpan.FromMinutes(1)));
        }
        catch (Exception exception)
        {
            Log.Error($"Failed canonical payroll for employment {employmentId}: {exception}");
            if (_active && generation == _generation && _clock.TryGetCurrentTime(out _, out var now))
                Enqueue(employmentId, now.Advance(TimeSpan.FromMinutes(1)));
        }
        finally
        {
            _processing.Remove(employmentId);
        }
    }

    private async Task MarkBusinessInsolventAsync(NCEmploymentRecord employment)
    {
        if (employment.Snapshot.Employer.Kind != NCPartyKind.Business)
            return;
        var businessId = new NCBusinessId(employment.Snapshot.Employer.PersistentId);
        var business = await _businesses.GetAsync(businessId, true);
        if (business == null || business.Snapshot.Status == NCBusinessStatus.Dissolved)
            return;
        await _businesses.ChangeStatusAsync(new NCChangeBusinessStatusRequest(
            NCPersistentIdentity.Command("payroll", "insolvent", employment.Snapshot.EmploymentId.ToString()),
            businessId,
            business.AggregateVersion,
            NCBusinessStatus.Insolvent,
            business.Snapshot.BusinessParty,
            NCPersistentIdentity.Correlation("payroll", "insolvent", employment.Snapshot.EmploymentId.ToString()),
            employment.Snapshot.LastAppliedEventId,
            "employment-payroll"));
    }

    private void OnEmploymentChanged(NCEmploymentPersistedEvent args)
    {
        if (!_active)
            return;
        Track(RefreshScheduleAsync(args.EmploymentId, _generation));
    }

    private async Task RefreshScheduleAsync(NCEmploymentId id, int generation)
    {
        var employment = await _employment.GetAsync(id, true);
        if (!_active || generation != _generation || employment?.Snapshot.Status != NCEmploymentStatus.Active ||
            employment.Snapshot.NextPayrollAt is not { } due)
            return;
        Enqueue(id, due);
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        _active = true;
        _generation++;
        _due.Clear();
        _queued.Clear();
        _processing.Clear();
        Track(RecoverAsync(_generation));
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        _active = false;
        _generation++;
        if (_inFlight.Count > 0)
            args.AddPendingWrite(Task.WhenAll(_inFlight.ToArray()));
    }

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args) => ClearRuntime();
    private void OnClockFailed(NCActiveWorldClockFailedEvent args) => ClearRuntime();

    private void ClearRuntime()
    {
        _active = false;
        _generation++;
        _due.Clear();
        _queued.Clear();
        _processing.Clear();
    }

    private async Task RecoverAsync(int generation)
    {
        string? after = null;
        var recovered = 0;
        while (_active && generation == _generation && recovered < MaximumRecoveredEmployments)
        {
            var page = await _database.GetNCAggregateSnapshotPageAsync(
                NCEmploymentRegistrySystem.EmploymentAggregateType, after, RecoveryPageSize);
            if (!_active || generation != _generation)
                return;
            foreach (var stored in page)
            {
                if (!Guid.TryParse(stored.AggregateId, out var raw))
                    continue;
                var employment = await _employment.GetAsync(new NCEmploymentId(raw), true);
                if (employment?.Snapshot.Status == NCEmploymentStatus.Active &&
                    employment.Snapshot.NextPayrollAt is { } due)
                    Enqueue(employment.Snapshot.EmploymentId, due);
                recovered++;
            }
            if (page.Count < RecoveryPageSize)
                return;
            after = page[^1].AggregateId;
        }
    }

    private void Enqueue(NCEmploymentId id, NCActiveWorldTime due)
    {
        if (_active && _queued.Add(id))
            _due.Enqueue(id, due.Ticks);
    }

    private void Track(Task task)
    {
        _inFlight.Add(task);
        _ = CompleteTrackedAsync(task);
    }

    private async Task CompleteTrackedAsync(Task task)
    {
        try
        {
            await task;
        }
        finally
        {
            _inFlight.Remove(task);
        }
    }

    private sealed record PayrollEvent(NCEmploymentId EmploymentId, NCActiveWorldTime DueAt, long Amount);
}
