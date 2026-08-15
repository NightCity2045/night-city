// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;

namespace Content.Server._NC.LivingCity.Employment;

/// <summary>
/// Common employment authority for player characters and persistent city actors. The per-employee index is committed
/// atomically with the employment aggregate, preserving one current relation across retries and concurrent hiring.
/// </summary>
public sealed partial class NCEmploymentRegistrySystem : EntitySystem
{
    public const string EmploymentAggregateType = "living-city.employment";
    public const string EmployeeIndexAggregateType = "living-city.party-employment-index";
    public const int SnapshotFormatVersion = 1;

    private const int MaximumPositionIdLength = 64;
    private const int MaximumAuditSourceLength = 128;
    private const int MutationGateStripeCount = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly ConcurrentDictionary<NCEmploymentId, NCEmploymentRecord> _employmentCache = [];
    private readonly ConcurrentDictionary<NCPartyRef, NCPartyEmploymentIndexRecord> _indexCache = [];
    private readonly SemaphoreSlim[] _mutationGates = CreateMutationGates();

    public int CachedEmploymentCount => _employmentCache.Count;
    public int CachedEmployeeIndexCount => _indexCache.Count;

    public async Task<NCEmploymentRecord?> GetAsync(NCEmploymentId employmentId, bool forceReload = false)
    {
        if (!employmentId.IsValid)
            return null;
        if (!forceReload && _employmentCache.TryGetValue(employmentId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(
            EmploymentAggregateType,
            EmploymentKey(employmentId));
        var record = DeserializeEmployment(stored, employmentId);
        return record == null ? null : CacheNewest(record);
    }

    public async Task<NCPartyEmploymentIndexRecord?> GetEmployeeIndexAsync(
        NCPartyRef employee,
        bool forceReload = false)
    {
        if (!CanBeEmployee(employee))
            return null;
        if (!forceReload && _indexCache.TryGetValue(employee, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(EmployeeIndexAggregateType, EmployeeKey(employee));
        var record = DeserializeIndex(stored, employee);
        return record == null ? null : CacheNewest(record);
    }

    public async Task<NCEmploymentRecord?> FindCurrentByEmployeeAsync(NCPartyRef employee, bool forceReload = false)
    {
        var index = await GetEmployeeIndexAsync(employee, forceReload);
        return index?.Snapshot.CurrentEmploymentId.IsValid == true
            ? await GetAsync(index.Snapshot.CurrentEmploymentId, forceReload)
            : null;
    }

    public bool Unload(NCEmploymentId employmentId)
    {
        return _employmentCache.TryRemove(employmentId, out _);
    }

    public bool UnloadEmployeeIndex(NCPartyRef employee)
    {
        return _indexCache.TryRemove(employee, out _);
    }

    public async Task<NCEmploymentMutationResult> HireAsync(NCHirePartyRequest request)
    {
        if (!TryValidateHire(request, out var error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCEmploymentMutationStatus.Rejected :
                    NCEmploymentMutationStatus.ClockUnavailable,
                error ?? "The employment request or active-world clock is invalid.");
        }

        var replay = await TryReplay(request.CommandId, "living-city.employment.hire", request.EmploymentId);
        if (replay != null)
            return replay;

        var employee = await _parties.ResolveAsync(request.Employee, true);
        var employer = await _parties.ResolveAsync(request.Employer, true);
        var initiator = await _parties.ResolveAsync(request.Initiator, true);
        if (!employee.IsResolved || !employer.IsResolved || !initiator.IsResolved)
        {
            return Reject(NCEmploymentMutationStatus.Rejected,
                employee.Error ?? employer.Error ?? initiator.Error ?? "An employment party is unavailable.");
        }

        var gate = GetMutationGate(request.Employee);
        await gate.WaitAsync();
        try
        {
            return await WithValidatedAccounts(
                request.Employee,
                request.Employer,
                request.EmployerAccountId,
                request.PayeeAccountId,
                request.SalaryAmount,
                async () =>
                {
                    var index = await GetEmployeeIndexAsync(request.Employee);
                    if (index?.Snapshot.CurrentEmploymentId.IsValid == true)
                    {
                        return Reject(NCEmploymentMutationStatus.Rejected,
                            "The employee already has a current employment relation.");
                    }

                    var employmentEventId = Guid.NewGuid();
                    var indexEventId = Guid.NewGuid();
                    var nextPayroll = request.SalaryAmount > 0
                        ? AddTicks(now, request.PayrollIntervalTicks)
                        : null;
                    if (request.SalaryAmount > 0 && nextPayroll == null)
                        return Reject(NCEmploymentMutationStatus.Rejected, "The payroll time would overflow.");

                    var employment = new NCEmploymentSnapshot(
                        request.EmploymentId,
                        request.Employee,
                        request.Employer,
                        request.PositionId.Trim(),
                        NCEmploymentStatus.Active,
                        request.EmployerAccountId,
                        request.PayeeAccountId,
                        request.SalaryAmount,
                        request.PayrollIntervalTicks,
                        nextPayroll,
                        now,
                        now,
                        1,
                        employmentEventId);
                    var employeeIndex = new NCPartyEmploymentIndexSnapshot(
                        request.Employee,
                        request.EmploymentId,
                        now,
                        (index?.Snapshot.DataVersion ?? 0) + 1,
                        indexEventId);
                    return await Persist(
                        request.CommandId,
                        "living-city.employment.hire",
                        request.EmploymentId,
                        request.ExpectedEmployeeIndexVersion,
                        0,
                        employment,
                        employeeIndex,
                        employmentEventId,
                        "living-city.employment.hired",
                        indexEventId,
                        "living-city.employment.index-assigned",
                        request.CorrelationId,
                        request.CausationId,
                        request.AuditSource,
                        epoch,
                        now);
                });
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to hire party {request.Employee} into {request.EmploymentId}: {exception}");
            return Reject(NCEmploymentMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCEmploymentMutationResult> ChangeAsync(NCChangeEmploymentRequest request)
    {
        if (!TryValidateChange(request, out var error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCEmploymentMutationStatus.Rejected :
                    NCEmploymentMutationStatus.ClockUnavailable,
                error ?? "The employment change or active-world clock is invalid.");
        }

        var replay = await TryReplay(request.CommandId, "living-city.employment.change", request.EmploymentId);
        if (replay != null)
            return replay;

        var current = await GetAsync(request.EmploymentId);
        if (current == null)
            return Reject(NCEmploymentMutationStatus.NotFound, "The employment relation does not exist.");
        var initiator = await _parties.ResolveAsync(request.Initiator, true);
        if (!initiator.IsResolved)
            return Reject(NCEmploymentMutationStatus.Rejected, initiator.Error ?? "The initiator is unavailable.");

        var gate = GetMutationGate(current.Snapshot.Employee);
        await gate.WaitAsync();
        try
        {
            // Reload under the per-employee gate so the state used for the next snapshot cannot be stale locally.
            current = await GetAsync(request.EmploymentId, true);
            if (current == null)
                return Reject(NCEmploymentMutationStatus.NotFound, "The employment relation does not exist.");
            if (current.Snapshot.Status == NCEmploymentStatus.Terminated)
                return Reject(NCEmploymentMutationStatus.Rejected, "A terminated employment cannot be changed.");

            return await WithValidatedAccounts(
                current.Snapshot.Employee,
                current.Snapshot.Employer,
                request.EmployerAccountId,
                request.PayeeAccountId,
                request.SalaryAmount,
                async () =>
                {
                    var index = await GetEmployeeIndexAsync(current.Snapshot.Employee, true);
                    if (index == null || index.Snapshot.CurrentEmploymentId != request.EmploymentId)
                    {
                        return Reject(NCEmploymentMutationStatus.Rejected,
                            "The per-employee index does not own this employment relation.");
                    }

                    var employmentEventId = Guid.NewGuid();
                    var indexEventId = Guid.NewGuid();
                    NCActiveWorldTime? nextPayroll = request.Status switch
                    {
                        NCEmploymentStatus.Terminated => null,
                        NCEmploymentStatus.Suspended => current.Snapshot.NextPayrollAt,
                        _ when request.SalaryAmount == 0 => null,
                        _ when current.Snapshot.SalaryAmount == request.SalaryAmount &&
                               current.Snapshot.PayrollIntervalTicks == request.PayrollIntervalTicks &&
                               current.Snapshot.NextPayrollAt != null => current.Snapshot.NextPayrollAt,
                        _ => AddTicks(now, request.PayrollIntervalTicks),
                    };
                    if (request.Status == NCEmploymentStatus.Active && request.SalaryAmount > 0 && nextPayroll == null)
                        return Reject(NCEmploymentMutationStatus.Rejected, "The payroll time would overflow.");

                    var employment = current.Snapshot with
                    {
                        PositionId = request.PositionId.Trim(),
                        Status = request.Status,
                        EmployerAccountId = request.EmployerAccountId,
                        PayeeAccountId = request.PayeeAccountId,
                        SalaryAmount = request.SalaryAmount,
                        PayrollIntervalTicks = request.PayrollIntervalTicks,
                        NextPayrollAt = nextPayroll,
                        ChangedAt = now,
                        DataVersion = current.Snapshot.DataVersion + 1,
                        LastAppliedEventId = employmentEventId,
                    };
                    var employeeIndex = index.Snapshot with
                    {
                        CurrentEmploymentId = request.Status == NCEmploymentStatus.Terminated
                            ? default
                            : request.EmploymentId,
                        ChangedAt = now,
                        DataVersion = index.Snapshot.DataVersion + 1,
                        LastAppliedEventId = indexEventId,
                    };
                    return await Persist(
                        request.CommandId,
                        "living-city.employment.change",
                        request.EmploymentId,
                        request.ExpectedEmployeeIndexVersion,
                        request.ExpectedEmploymentVersion,
                        employment,
                        employeeIndex,
                        employmentEventId,
                        request.Status == NCEmploymentStatus.Terminated
                            ? "living-city.employment.terminated"
                            : "living-city.employment.changed",
                        indexEventId,
                        request.Status == NCEmploymentStatus.Terminated
                            ? "living-city.employment.index-released"
                            : "living-city.employment.index-updated",
                        request.CorrelationId,
                        request.CausationId,
                        request.AuditSource,
                        epoch,
                        now);
                });
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to change employment {request.EmploymentId}: {exception}");
            return Reject(NCEmploymentMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<NCEmploymentMutationResult> WithValidatedAccounts(
        NCPartyRef employee,
        NCPartyRef employer,
        NCAccountId employerAccountId,
        NCAccountId payeeAccountId,
        long salaryAmount,
        Func<Task<NCEmploymentMutationResult>> operation)
    {
        if (salaryAmount == 0)
        {
            if (employerAccountId.IsValid || payeeAccountId.IsValid)
                return Reject(NCEmploymentMutationStatus.Rejected, "Unpaid employment must not retain payroll accounts.");
            return await operation();
        }

        return await _ledger.RunWithAccountLocksAsync(
            new[] { employerAccountId, payeeAccountId },
            async () =>
            {
                var employerAccount = await _ledger.GetAccountAsync(employerAccountId, true);
                var payeeAccount = await _ledger.GetAccountAsync(payeeAccountId, true);
                if (employerAccount == null || payeeAccount == null)
                    return Reject(NCEmploymentMutationStatus.Rejected, "A payroll account does not exist.");
                if (employerAccount.Snapshot.Owner != employer || payeeAccount.Snapshot.Owner != employee)
                    return Reject(NCEmploymentMutationStatus.Rejected, "Payroll account ownership does not match employment parties.");
                if (employerAccount.Snapshot.Status != NCAccountStatus.Active ||
                    payeeAccount.Snapshot.Status != NCAccountStatus.Active ||
                    employerAccount.Snapshot.CurrencyId != payeeAccount.Snapshot.CurrencyId)
                {
                    return Reject(NCEmploymentMutationStatus.Rejected,
                        "Payroll accounts must be active and use the same currency.");
                }
                return await operation();
            });
    }

    private async Task<NCEmploymentMutationResult> Persist(
        Guid commandId,
        string commandType,
        NCEmploymentId employmentId,
        long expectedIndexVersion,
        long expectedEmploymentVersion,
        NCEmploymentSnapshot employment,
        NCPartyEmploymentIndexSnapshot employeeIndex,
        Guid employmentEventId,
        string employmentEventType,
        Guid indexEventId,
        string indexEventType,
        Guid correlationId,
        Guid? causationId,
        string auditSource,
        NCSessionEpoch epoch,
        NCActiveWorldTime now)
    {
        var receipt = new EmploymentReceipt(
            employment,
            expectedEmploymentVersion + 1,
            employeeIndex,
            expectedIndexVersion + 1,
            epoch,
            now);
        var writes = new NCPersistentAggregateWrite[]
        {
            new(
                employmentEventId,
                employmentEventType,
                EmploymentAggregateType,
                EmploymentKey(employmentId),
                expectedEmploymentVersion,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(employment, JsonOptions),
                JsonSerializer.Serialize(new EmploymentEvent(
                    employmentId,
                    employment.Employee,
                    employment.Employer,
                    employmentEventType,
                    auditSource,
                    employment.PositionId,
                    employment.Status), JsonOptions)),
            new(
                indexEventId,
                indexEventType,
                EmployeeIndexAggregateType,
                EmployeeKey(employment.Employee),
                expectedIndexVersion,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(employeeIndex, JsonOptions),
                JsonSerializer.Serialize(new EmployeeIndexEvent(
                    employment.Employee,
                    employeeIndex.CurrentEmploymentId,
                    indexEventType,
                    auditSource), JsonOptions)),
        };
        var persisted = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(
            commandId,
            commandType,
            "employment",
            EmploymentKey(employmentId),
            writes,
            JsonSerializer.Serialize(receipt, JsonOptions),
            correlationId,
            causationId,
            epoch,
            now,
            []));

        if (persisted.Status == NCPersistentMutationStatus.Applied)
            return ApplyReceipt(receipt, NCEmploymentMutationStatus.Applied);
        if (persisted.Status == NCPersistentMutationStatus.DuplicateCompleted)
        {
            var replay = DeserializeReceipt(persisted.ResultPayload);
            return replay == null
                ? Reject(NCEmploymentMutationStatus.StorageFailure, "The employment receipt is invalid.")
                : ApplyReceipt(replay, NCEmploymentMutationStatus.Duplicate);
        }

        return Reject(MapFailure(persisted.Status, persisted.Error),
            persisted.Error ?? "The employment mutation was not committed.");
    }

    private async Task<NCEmploymentMutationResult?> TryReplay(
        Guid commandId,
        string commandType,
        NCEmploymentId employmentId)
    {
        var existing = await _database.GetNCPersistentCommandResultAsync(commandId);
        if (existing == null)
            return null;
        if (existing.CommandType != commandType || existing.ScopeType != "employment" ||
            existing.ScopeId != EmploymentKey(employmentId))
        {
            return Reject(NCEmploymentMutationStatus.StorageFailure,
                "The command ID is already owned by another employment operation.");
        }
        if (existing.Status != NCPersistentMutationStatus.DuplicateCompleted)
        {
            return Reject(existing.Status == NCPersistentMutationStatus.DuplicateFailed
                    ? NCEmploymentMutationStatus.VersionConflict
                    : NCEmploymentMutationStatus.StorageFailure,
                existing.Error ?? "The employment command is incomplete.");
        }

        var receipt = DeserializeReceipt(existing.ResultPayload);
        return receipt == null
            ? Reject(NCEmploymentMutationStatus.StorageFailure, "The employment receipt is invalid.")
            : ApplyReceipt(receipt, NCEmploymentMutationStatus.Duplicate);
    }

    private NCEmploymentMutationResult ApplyReceipt(EmploymentReceipt receipt, NCEmploymentMutationStatus status)
    {
        var employment = CacheNewest(new NCEmploymentRecord(
            receipt.Employment,
            receipt.EmploymentVersion,
            receipt.SessionEpoch,
            receipt.PersistedAt));
        var index = CacheNewest(new NCPartyEmploymentIndexRecord(
            receipt.EmployeeIndex,
            receipt.EmployeeIndexVersion,
            receipt.SessionEpoch,
            receipt.PersistedAt));
        _taskManager.RunOnMainThread(() => RaiseLocalEvent(new NCEmploymentPersistedEvent(
            employment.Snapshot.EmploymentId,
            employment.Snapshot.Employee,
            employment.Snapshot.Employer,
            employment.AggregateVersion,
            employment.Snapshot.Status)));
        return new NCEmploymentMutationResult(status, employment, index, null);
    }

    private static bool TryValidateHire(NCHirePartyRequest request, out string? error)
    {
        if (!TryValidateMetadata(request.CommandId, request.CorrelationId, request.AuditSource, out error) ||
            !request.EmploymentId.IsValid || !CanBeEmployee(request.Employee) || !CanBeEmployer(request.Employer) ||
            request.Employee == request.Employer || !request.Initiator.IsValid || request.ExpectedEmployeeIndexVersion < 0 ||
            !ValidPosition(request.PositionId) || !ValidTerms(request.EmployerAccountId, request.PayeeAccountId,
                request.SalaryAmount, request.PayrollIntervalTicks))
        {
            error ??= "The hire request is invalid.";
            return false;
        }
        return true;
    }

    private static bool TryValidateChange(NCChangeEmploymentRequest request, out string? error)
    {
        if (!TryValidateMetadata(request.CommandId, request.CorrelationId, request.AuditSource, out error) ||
            !request.EmploymentId.IsValid || request.ExpectedEmploymentVersion < 1 ||
            request.ExpectedEmployeeIndexVersion < 1 || !request.Initiator.IsValid || !ValidPosition(request.PositionId) ||
            !Enum.IsDefined(request.Status) ||
            !ValidTerms(request.EmployerAccountId, request.PayeeAccountId,
                request.SalaryAmount, request.PayrollIntervalTicks))
        {
            error ??= "The employment change is invalid.";
            return false;
        }
        return true;
    }

    private static bool TryValidateMetadata(Guid commandId, Guid correlationId, string auditSource, out string? error)
    {
        if (commandId == Guid.Empty || correlationId == Guid.Empty || string.IsNullOrWhiteSpace(auditSource) ||
            auditSource.Length > MaximumAuditSourceLength)
        {
            error = "The employment command metadata is invalid.";
            return false;
        }
        error = null;
        return true;
    }

    private static bool ValidTerms(
        NCAccountId employerAccountId,
        NCAccountId payeeAccountId,
        long salaryAmount,
        long payrollIntervalTicks)
    {
        return salaryAmount switch
        {
            < 0 => false,
            0 => payrollIntervalTicks == 0 && !employerAccountId.IsValid && !payeeAccountId.IsValid,
            _ => payrollIntervalTicks > 0 && employerAccountId.IsValid && payeeAccountId.IsValid &&
                 employerAccountId != payeeAccountId,
        };
    }

    private static bool ValidPosition(string positionId)
    {
        return !string.IsNullOrWhiteSpace(positionId) && positionId.Trim().Length <= MaximumPositionIdLength;
    }

    private static bool CanBeEmployee(NCPartyRef party)
    {
        return party.IsValid && party.Kind is NCPartyKind.Character or NCPartyKind.Actor;
    }

    private static bool CanBeEmployer(NCPartyRef party)
    {
        return party.IsValid && party.Kind is NCPartyKind.Organization or NCPartyKind.Business or NCPartyKind.CityService;
    }

    private static NCActiveWorldTime? AddTicks(NCActiveWorldTime now, long ticks)
    {
        return ticks > 0 && now.Ticks <= long.MaxValue - ticks
            ? new NCActiveWorldTime(now.Ticks + ticks)
            : null;
    }

    private static NCEmploymentRecord? DeserializeEmployment(
        NCAggregateSnapshotData? stored,
        NCEmploymentId employmentId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCEmploymentSnapshot>(stored.Payload, JsonOptions);
            return ValidEmployment(snapshot, employmentId)
                ? new NCEmploymentRecord(snapshot!, stored.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NCPartyEmploymentIndexRecord? DeserializeIndex(
        NCAggregateSnapshotData? stored,
        NCPartyRef employee)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCPartyEmploymentIndexSnapshot>(stored.Payload, JsonOptions);
            return ValidIndex(snapshot, employee)
                ? new NCPartyEmploymentIndexRecord(snapshot!, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static EmploymentReceipt? DeserializeReceipt(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<EmploymentReceipt>(payload, JsonOptions);
            return receipt != null && receipt.EmploymentVersion >= 1 && receipt.EmployeeIndexVersion >= 1 &&
                   receipt.SessionEpoch.IsValid && receipt.PersistedAt.IsValid &&
                   ValidEmployment(receipt.Employment, receipt.Employment.EmploymentId) &&
                   ValidIndex(receipt.EmployeeIndex, receipt.Employment.Employee) &&
                   (receipt.Employment.Status == NCEmploymentStatus.Terminated
                       ? !receipt.EmployeeIndex.CurrentEmploymentId.IsValid
                       : receipt.EmployeeIndex.CurrentEmploymentId == receipt.Employment.EmploymentId)
                ? receipt
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ValidEmployment(NCEmploymentSnapshot? snapshot, NCEmploymentId employmentId)
    {
        return snapshot != null && employmentId.IsValid && snapshot.EmploymentId == employmentId &&
               CanBeEmployee(snapshot.Employee) && CanBeEmployer(snapshot.Employer) &&
               snapshot.Employee != snapshot.Employer && ValidPosition(snapshot.PositionId) &&
               snapshot.PositionId.Trim() == snapshot.PositionId && Enum.IsDefined(snapshot.Status) &&
               ValidTerms(snapshot.EmployerAccountId, snapshot.PayeeAccountId,
                   snapshot.SalaryAmount, snapshot.PayrollIntervalTicks) &&
               snapshot.StartedAt.IsValid && snapshot.ChangedAt.IsValid &&
               ValidNextPayroll(snapshot) &&
               snapshot.DataVersion >= 1 && snapshot.LastAppliedEventId != Guid.Empty;
    }

    private static bool ValidNextPayroll(NCEmploymentSnapshot snapshot)
    {
        // Termination clears scheduling; every current paid relation retains one valid active-world due time.
        return snapshot.Status == NCEmploymentStatus.Terminated || snapshot.SalaryAmount == 0
            ? snapshot.NextPayrollAt == null
            : snapshot.NextPayrollAt is { IsValid: true };
    }

    private static bool ValidIndex(NCPartyEmploymentIndexSnapshot? snapshot, NCPartyRef employee)
    {
        return snapshot != null && CanBeEmployee(employee) && snapshot.Employee == employee &&
               snapshot.ChangedAt.IsValid && snapshot.DataVersion >= 1 && snapshot.LastAppliedEventId != Guid.Empty;
    }

    private NCEmploymentRecord CacheNewest(NCEmploymentRecord candidate)
    {
        return _employmentCache.AddOrUpdate(candidate.Snapshot.EmploymentId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);
    }

    private NCPartyEmploymentIndexRecord CacheNewest(NCPartyEmploymentIndexRecord candidate)
    {
        return _indexCache.AddOrUpdate(candidate.Snapshot.Employee, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);
    }

    private SemaphoreSlim GetMutationGate(NCPartyRef employee)
    {
        var index = employee.GetHashCode() & int.MaxValue;
        return _mutationGates[index % _mutationGates.Length];
    }

    private static SemaphoreSlim[] CreateMutationGates()
    {
        var gates = new SemaphoreSlim[MutationGateStripeCount];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static NCEmploymentMutationStatus MapFailure(NCPersistentMutationStatus status, string? error)
    {
        if (status is NCPersistentMutationStatus.VersionConflict or NCPersistentMutationStatus.DuplicateFailed)
            return error?.StartsWith("The batch timestamp ") == true
                ? NCEmploymentMutationStatus.ClockUnavailable
                : NCEmploymentMutationStatus.VersionConflict;
        return NCEmploymentMutationStatus.StorageFailure;
    }

    private static string EmploymentKey(NCEmploymentId employmentId) => employmentId.Value.ToString("D");
    private static string EmployeeKey(NCPartyRef employee) => employee.ToString();

    private static NCEmploymentMutationResult Reject(NCEmploymentMutationStatus status, string error)
    {
        return new NCEmploymentMutationResult(status, null, null, error);
    }

    private sealed record EmploymentEvent(
        NCEmploymentId EmploymentId,
        NCPartyRef Employee,
        NCPartyRef Employer,
        string EventType,
        string AuditSource,
        string PositionId,
        NCEmploymentStatus Status);

    private sealed record EmployeeIndexEvent(
        NCPartyRef Employee,
        NCEmploymentId CurrentEmploymentId,
        string EventType,
        string AuditSource);

    private sealed record EmploymentReceipt(
        NCEmploymentSnapshot Employment,
        long EmploymentVersion,
        NCPartyEmploymentIndexSnapshot EmployeeIndex,
        long EmployeeIndexVersion,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);
}
