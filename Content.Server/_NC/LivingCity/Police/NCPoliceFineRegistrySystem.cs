// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Police;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Database._NC.Police;

namespace Content.Server._NC.LivingCity.Police;

public enum NCPoliceFineMutationStatus : byte
{
    Applied,
    Duplicate,
    NotFound,
    VersionConflict,
    ClockUnavailable,
    Rejected,
    StorageFailure,
}

public sealed record NCPoliceFineRecord(
    NCPoliceFineSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCPoliceFineMutationResult(
    NCPoliceFineMutationStatus Status,
    NCPoliceFineRecord? Fine,
    string? Error);

public sealed record NCPoliceFinePaymentData(NCPoliceFinePaymentResult Result, long? Balance);

/// <summary>
/// Canonical fine authority. Fine state uses active-world time, and payment commits the Paid snapshot in the same
/// database transaction as the double-entry transfer to NCPD.
/// </summary>
public sealed partial class NCPoliceFineRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.police-fine";
    public const int SnapshotFormatVersion = 1;

    private const int MutationGateStripeCount = 64;
    private const int PersistencePageSize = 500;
    private const int MaximumFilteredPages = 8;
    private const string NcpdOrganizationId = "ncpd";
    private const string NcpdDisplayName = "Night City Police Department";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;

    private readonly ConcurrentDictionary<NCPoliceFineId, NCPoliceFineRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = CreateGates();

    public async Task<NCPoliceFineRecord?> GetAsync(NCPoliceFineId fineId, bool forceReload = false)
    {
        if (!fineId.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(fineId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, Key(fineId));
        var record = Deserialize(stored, fineId);
        return record == null ? null : CacheNewest(record);
    }

    /// <summary>
    /// Reads a bounded newest-first page. Target filtering may inspect at most four thousand snapshots and therefore
    /// cannot turn an ATM refresh into an unbounded database scan.
    /// </summary>
    public async Task<IReadOnlyList<NCPoliceFineRecord>> GetPageAsync(
        int limit,
        NCPartyRef? target = null)
    {
        if (limit is < 1 or > 200 || target is { IsValid: false })
            return [];

        var result = new List<NCPoliceFineRecord>(limit);
        string? before = null;
        for (var pageNumber = 0; pageNumber < MaximumFilteredPages && result.Count < limit; pageNumber++)
        {
            var requested = target == null ? limit : PersistencePageSize;
            var page = await _database.GetNCAggregateSnapshotPageDescendingAsync(
                AggregateType,
                before,
                requested);
            if (page.Count == 0)
                break;

            foreach (var stored in page)
            {
                var record = Deserialize(stored, null);
                if (record == null || target is { } owner && record.Snapshot.Target != owner)
                    continue;
                result.Add(CacheNewest(record));
                if (result.Count == limit)
                    break;
            }

            before = page[^1].AggregateId;
            if (page.Count < requested)
                break;
        }

        return result;
    }

    public async Task<NCPoliceFineMutationResult> CreateAsync(
        NCPartyRef target,
        string article,
        string reason,
        long amount,
        TimeSpan duePeriod,
        NCPartyRef issuedBy,
        string auditSource)
    {
        article = article.Trim();
        reason = reason.Trim();
        if (!target.IsValid || target.Kind != NCPartyKind.Character || !issuedBy.IsValid || amount <= 0 ||
            duePeriod <= TimeSpan.Zero ||
            article.Length is < 1 or > 128 || reason.Length is < 1 or > 512 ||
            string.IsNullOrWhiteSpace(auditSource) || auditSource.Length > 128 ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCPoliceFineMutationStatus.Rejected :
                NCPoliceFineMutationStatus.ClockUnavailable, "The fine request or active-world clock is invalid.");
        }

        var targetResolution = await _parties.ResolveAsync(target, true);
        var issuerResolution = await _parties.ResolveAsync(issuedBy, true);
        if (!targetResolution.IsResolved || !issuerResolution.IsResolved)
            return Reject(NCPoliceFineMutationStatus.Rejected,
                targetResolution.Error ?? issuerResolution.Error ?? "A fine party is unavailable.");

        var fineId = NCPoliceFineId.New();
        var eventId = Guid.NewGuid();
        NCActiveWorldTime dueAt;
        try
        {
            dueAt = now.Advance(duePeriod);
        }
        catch (OverflowException)
        {
            return Reject(NCPoliceFineMutationStatus.Rejected, "The fine deadline overflows active-world time.");
        }

        var snapshot = new NCPoliceFineSnapshot(
            fineId,
            target,
            targetResolution.DisplayName,
            article,
            reason,
            amount,
            NCPoliceFineStatus.Issued,
            issuedBy,
            issuerResolution.DisplayName,
            now,
            dueAt,
            now,
            null,
            null,
            1,
            eventId);
        var commandId = Guid.NewGuid();
        var receipt = new FineReceipt(snapshot, 1, epoch, now);
        var persisted = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
            commandId,
            "living-city.police-fine.create",
            "police-fine",
            Key(fineId),
            eventId,
            "living-city.police-fine.issued",
            AggregateType,
            Key(fineId),
            0,
            SnapshotFormatVersion,
            JsonSerializer.Serialize(snapshot, JsonOptions),
            JsonSerializer.Serialize(new FineEvent(fineId, target, NCPoliceFineStatus.Issued, auditSource), JsonOptions),
            JsonSerializer.Serialize(receipt, JsonOptions),
            commandId,
            null,
            epoch,
            now,
            []));
        return ApplySingle(persisted, receipt);
    }

    public async Task<NCPoliceFineMutationResult> SetStatusAsync(
        NCPoliceFineId fineId,
        NCPoliceFineStatus status,
        string reason,
        NCPartyRef actor,
        string auditSource)
    {
        reason = reason.Trim();
        if (!fineId.IsValid || !actor.IsValid || !Enum.IsDefined(status) ||
            status is NCPoliceFineStatus.Issued or NCPoliceFineStatus.Paid ||
            reason.Length is < 1 or > 512 || string.IsNullOrWhiteSpace(auditSource) || auditSource.Length > 128 ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCPoliceFineMutationStatus.Rejected :
                NCPoliceFineMutationStatus.ClockUnavailable, "The fine status request is invalid.");
        }

        var actorResolution = await _parties.ResolveAsync(actor, true);
        if (!actorResolution.IsResolved)
            return Reject(NCPoliceFineMutationStatus.Rejected, actorResolution.Error ?? "The actor is unavailable.");

        var gate = Gate(fineId);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(fineId, true);
            if (current == null)
                return Reject(NCPoliceFineMutationStatus.NotFound, "The fine does not exist.");
            if (current.Snapshot.Status is NCPoliceFineStatus.Paid or NCPoliceFineStatus.Voided)
                return Reject(NCPoliceFineMutationStatus.Rejected, "A paid or voided fine cannot change status.");

            var eventId = Guid.NewGuid();
            var next = current.Snapshot with
            {
                Status = status,
                UpdatedAt = now,
                DataVersion = current.Snapshot.DataVersion + 1,
                LastAppliedEventId = eventId,
            };
            var commandId = Guid.NewGuid();
            var receipt = new FineReceipt(next, current.AggregateVersion + 1, epoch, now);
            var persisted = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
                commandId,
                "living-city.police-fine.set-status",
                "police-fine",
                Key(fineId),
                eventId,
                "living-city.police-fine.status-changed",
                AggregateType,
                Key(fineId),
                current.AggregateVersion,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(next, JsonOptions),
                JsonSerializer.Serialize(new FineStatusEvent(fineId, actor, current.Snapshot.Status, status,
                    reason, auditSource), JsonOptions),
                JsonSerializer.Serialize(receipt, JsonOptions),
                commandId,
                null,
                epoch,
                now,
                []));
            return ApplySingle(persisted, receipt);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCPoliceFinePaymentData> PayAsync(NCPoliceFineId fineId, NCPartyRef payer)
    {
        if (!fineId.IsValid || !payer.IsValid)
            return new(NCPoliceFinePaymentResult.FineNotFound, null);

        var gate = Gate(fineId);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(fineId, true);
            if (current == null)
                return new(NCPoliceFinePaymentResult.FineNotFound, null);
            if (current.Snapshot.Target != payer)
                return new(NCPoliceFinePaymentResult.NotOwner, null);
            var stableKey = $"{fineId}/{payer}";
            var paymentTransactionId = NCPersistentIdentity.Transaction("police-fine", "payment", stableKey);
            if (current.Snapshot.Status == NCPoliceFineStatus.Paid)
            {
                if (current.Snapshot.PaymentTransactionId != paymentTransactionId ||
                    await _ledger.GetTransactionAsync(paymentTransactionId, true) == null)
                    return new(NCPoliceFinePaymentResult.NotPayable, null);
                var replayBalance = await _finance.GetPrimaryAccountAsync(payer, true);
                return new(NCPoliceFinePaymentResult.Success, replayBalance?.Snapshot.Balance);
            }
            if (current.Snapshot.Status is not (NCPoliceFineStatus.Issued or NCPoliceFineStatus.Overdue))
                return new(NCPoliceFinePaymentResult.NotPayable, null);
            if (!_clock.TryGetCurrentTime(out _, out var now))
                return new(NCPoliceFinePaymentResult.NotPayable, null);

            NCEconomyMutationResult? transaction = null;
            long? latestPayerBalance = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var payerAccount = await _finance.EnsurePrimaryAccountAsync(payer, "police-fine-payment");
                var ncpdAccount = await _finance.EnsureOrganizationAsync(
                    NcpdOrganizationId,
                    NcpdDisplayName,
                    "police-fine-payment");
                latestPayerBalance = payerAccount.Account?.Snapshot.Balance;
                if (!payerAccount.IsResolved || !ncpdAccount.IsResolved)
                    return new(NCPoliceFinePaymentResult.AccountNotFound, latestPayerBalance);
                var payerRecord = payerAccount.Account!;
                var ncpdRecord = ncpdAccount.Account!;

                var eventId = Guid.NewGuid();
                var next = current.Snapshot with
                {
                    Status = NCPoliceFineStatus.Paid,
                    UpdatedAt = now,
                    PaidAt = now,
                    PaymentTransactionId = paymentTransactionId,
                    DataVersion = current.Snapshot.DataVersion + 1,
                    LastAppliedEventId = eventId,
                };
                var commandId = attempt == 0
                    ? NCPersistentIdentity.Command("police-fine", "payment", stableKey)
                    : NCPersistentIdentity.Command(
                        "police-fine",
                        "payment-attempt",
                        $"{stableKey}/{current.AggregateVersion}/{payerRecord.AggregateVersion}/" +
                        ncpdRecord.AggregateVersion);
                transaction = await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
                    commandId,
                    paymentTransactionId,
                    NCLedgerTransactionKind.Fee,
                    $"police fine {fineId}",
                    [
                        new NCLedgerLegRequest(
                            payerAccount.AccountId,
                            payerRecord.AggregateVersion,
                            -current.Snapshot.Amount),
                        new NCLedgerLegRequest(
                            ncpdAccount.AccountId,
                            ncpdRecord.AggregateVersion,
                            current.Snapshot.Amount),
                    ],
                    payer,
                    NCPersistentIdentity.Correlation("police-fine", "payment", stableKey),
                    AuditSource: "police-fine-payment",
                    ParticipantWrites:
                    [
                        new NCAtomicLedgerParticipantWrite(
                            eventId,
                            "living-city.police-fine.paid",
                            AggregateType,
                            Key(fineId),
                            current.AggregateVersion,
                            SnapshotFormatVersion,
                            JsonSerializer.Serialize(next, JsonOptions),
                            JsonSerializer.Serialize(new FinePaymentEvent(
                                fineId,
                                payer,
                                current.Snapshot.Amount,
                                "police-fine-payment"), JsonOptions)),
                    ]));

                if (transaction.Status != NCEconomyMutationStatus.VersionConflict)
                    break;

                // The fine itself is an atomic participant. If another attempt completed it, validate the stable
                // transaction before reporting success; otherwise rebuild the request from current account versions.
                current = await GetAsync(fineId, true);
                if (current == null || current.Snapshot.Target != payer)
                    return new(NCPoliceFinePaymentResult.NotPayable, latestPayerBalance);
                if (current.Snapshot.Status == NCPoliceFineStatus.Paid)
                {
                    if (current.Snapshot.PaymentTransactionId != paymentTransactionId ||
                        await _ledger.GetTransactionAsync(paymentTransactionId, true) == null)
                    {
                        return new(NCPoliceFinePaymentResult.NotPayable, latestPayerBalance);
                    }

                    var replayBalance = await _finance.GetPrimaryAccountAsync(payer, true);
                    return new(NCPoliceFinePaymentResult.Success, replayBalance?.Snapshot.Balance);
                }
                if (current.Snapshot.Status is not (NCPoliceFineStatus.Issued or NCPoliceFineStatus.Overdue))
                    return new(NCPoliceFinePaymentResult.NotPayable, latestPayerBalance);
            }

            if (transaction?.Status == NCEconomyMutationStatus.InsufficientFunds)
                return new(NCPoliceFinePaymentResult.InsufficientFunds, latestPayerBalance);
            if (transaction?.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
                return new(NCPoliceFinePaymentResult.NotPayable, latestPayerBalance);

            var paid = await GetAsync(fineId, true);
            if (paid?.Snapshot.Status != NCPoliceFineStatus.Paid || paid.Snapshot.Target != payer)
                return new(NCPoliceFinePaymentResult.NotPayable, null);
            var balance = await _finance.GetPrimaryAccountAsync(payer, true);
            return new(NCPoliceFinePaymentResult.Success, balance?.Snapshot.Balance);
        }
        finally
        {
            gate.Release();
        }
    }

    public NCPoliceFineStatus EffectiveStatus(NCPoliceFineSnapshot snapshot, NCActiveWorldTime now)
    {
        return snapshot.Status == NCPoliceFineStatus.Issued && snapshot.DueAt <= now
            ? NCPoliceFineStatus.Overdue
            : snapshot.Status;
    }

    private NCPoliceFineMutationResult ApplySingle(NCPersistentMutationResult result, FineReceipt submitted)
    {
        if (result.Status == NCPersistentMutationStatus.Applied)
        {
            var record = CacheNewest(new NCPoliceFineRecord(
                submitted.Snapshot,
                result.AggregateVersion,
                submitted.SessionEpoch,
                submitted.PersistedAt));
            return new(NCPoliceFineMutationStatus.Applied, record, null);
        }
        if (result.Status == NCPersistentMutationStatus.DuplicateCompleted)
        {
            var receipt = DeserializeReceipt(result.ResultPayload);
            if (receipt != null)
            {
                var record = CacheNewest(new NCPoliceFineRecord(
                    receipt.Snapshot,
                    receipt.AggregateVersion,
                    receipt.SessionEpoch,
                    receipt.PersistedAt));
                return new(NCPoliceFineMutationStatus.Duplicate, record, null);
            }
        }

        var status = result.Status is NCPersistentMutationStatus.VersionConflict or
            NCPersistentMutationStatus.DuplicateFailed
            ? NCPoliceFineMutationStatus.VersionConflict
            : result.Status == NCPersistentMutationStatus.ClockRejected
                ? NCPoliceFineMutationStatus.ClockUnavailable
                : NCPoliceFineMutationStatus.StorageFailure;
        return Reject(status, result.Error ?? "The fine mutation was not committed.");
    }

    private static NCPoliceFineRecord? Deserialize(NCAggregateSnapshotData? stored, NCPoliceFineId? expected)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCPoliceFineSnapshot>(stored.Payload, JsonOptions);
            return Valid(snapshot, expected)
                ? new NCPoliceFineRecord(snapshot!, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FineReceipt? DeserializeReceipt(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<FineReceipt>(payload, JsonOptions);
            return receipt != null && receipt.AggregateVersion >= 1 && receipt.SessionEpoch.IsValid &&
                   receipt.PersistedAt.IsValid && Valid(receipt.Snapshot, receipt.Snapshot.FineId)
                ? receipt
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Valid(NCPoliceFineSnapshot? snapshot, NCPoliceFineId? expected)
    {
        return snapshot != null && snapshot.FineId.IsValid &&
               (expected == null || snapshot.FineId == expected) && snapshot.Target.IsValid &&
               snapshot.Target.Kind == NCPartyKind.Character &&
               snapshot.IssuedBy.IsValid && snapshot.TargetName.Length is > 0 and <= 128 &&
               snapshot.TargetName.Trim() == snapshot.TargetName && snapshot.IssuedByName.Length is > 0 and <= 128 &&
               snapshot.IssuedByName.Trim() == snapshot.IssuedByName && snapshot.Article.Length is > 0 and <= 128 &&
               snapshot.Article.Trim() == snapshot.Article && snapshot.Reason.Length is > 0 and <= 512 &&
               snapshot.Reason.Trim() == snapshot.Reason && snapshot.Amount > 0 && Enum.IsDefined(snapshot.Status) &&
               snapshot.IssuedAt.IsValid && snapshot.DueAt > snapshot.IssuedAt && snapshot.UpdatedAt >= snapshot.IssuedAt &&
               (snapshot.Status == NCPoliceFineStatus.Paid) == snapshot.PaidAt.HasValue &&
               (snapshot.Status == NCPoliceFineStatus.Paid) == snapshot.PaymentTransactionId.HasValue &&
               (!snapshot.PaymentTransactionId.HasValue || snapshot.PaymentTransactionId.Value.IsValid) &&
               (!snapshot.PaidAt.HasValue || snapshot.PaidAt.Value >= snapshot.IssuedAt) &&
               snapshot.DataVersion >= 1 && snapshot.LastAppliedEventId != Guid.Empty;
    }

    private NCPoliceFineRecord CacheNewest(NCPoliceFineRecord candidate)
    {
        return _cache.AddOrUpdate(candidate.Snapshot.FineId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);
    }

    private SemaphoreSlim Gate(NCPoliceFineId fineId)
    {
        return _gates[(fineId.GetHashCode() & int.MaxValue) % _gates.Length];
    }

    private static SemaphoreSlim[] CreateGates()
    {
        var gates = new SemaphoreSlim[MutationGateStripeCount];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static string Key(NCPoliceFineId fineId) => fineId.Value.ToString("D");

    private static NCPoliceFineMutationResult Reject(NCPoliceFineMutationStatus status, string error)
    {
        return new NCPoliceFineMutationResult(status, null, error);
    }

    private sealed record FineEvent(
        NCPoliceFineId FineId,
        NCPartyRef Target,
        NCPoliceFineStatus Status,
        string AuditSource);

    private sealed record FineStatusEvent(
        NCPoliceFineId FineId,
        NCPartyRef Actor,
        NCPoliceFineStatus PreviousStatus,
        NCPoliceFineStatus Status,
        string Reason,
        string AuditSource);

    private sealed record FinePaymentEvent(
        NCPoliceFineId FineId,
        NCPartyRef Payer,
        long Amount,
        string AuditSource);

    private sealed record FineReceipt(
        NCPoliceFineSnapshot Snapshot,
        long AggregateVersion,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);
}
