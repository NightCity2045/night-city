// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Economy.Cash;

/// <summary>
/// Durable state machine for electronic/physical cash handoffs. A prior-round pending deposit is completed because
/// its inserted cash disappeared with that round; a prior-round pending withdrawal is refunded because no undelivered
/// cash can survive the round boundary.
/// </summary>
public sealed partial class NCCashHandoffSystem : EntitySystem
{
    public const string AggregateType = "living-city.cash-handoff";
    public const int SnapshotFormatVersion = 1;

    private const string RecoveryAggregateType = "living-city.cash-handoff-recovery";
    private const string RecoveryAggregateId = "global";
    private const int RecoverySnapshotFormatVersion = 1;
    private const int MutationGateStripeCount = 64;
    private const int RecoveryPageSize = 500;
    private const int MaximumReferenceLength = 256;
    private const int MaximumAuditSourceLength = 128;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;

    private readonly ConcurrentDictionary<NCCashHandoffId, NCCashHandoffRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = CreateGates();
    private Task _recovery = Task.CompletedTask;

    public bool IsReady => _recovery.IsCompletedSuccessfully;
    public Task WaitForRecoveryAsync() => _recovery;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        _recovery = RecoverPriorSessionsAsync(args.Epoch);
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        // A clean round close must not overtake recovery of a prior crashed round.
        args.AddPendingWrite(_recovery);
    }

    public async Task<NCCashHandoffRecord?> GetAsync(NCCashHandoffId handoffId, bool forceReload = false)
    {
        if (!handoffId.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(handoffId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, Key(handoffId));
        var record = Deserialize(stored, handoffId);
        return record == null ? null : CacheNewest(record);
    }

    /// <summary>
    /// Commits the signed electronic balance change and PendingPhysical handoff in one database transaction.
    /// The caller performs the bounded ECS cash action and then calls CompleteAsync or ReverseAsync.
    /// </summary>
    public async Task<NCCashHandoffMutationResult> StartAsync(NCStartCashHandoffRequest request)
    {
        try
        {
            await _recovery;
        }
        catch (Exception exception)
        {
            return Reject(NCCashHandoffMutationStatus.StorageFailure,
                $"Prior cash handoffs were not recovered: {exception.Message}");
        }

        if (!ValidStart(request) || !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCCashHandoffMutationStatus.Rejected :
                NCCashHandoffMutationStatus.ClockUnavailable, "The cash handoff request is invalid.");
        }

        var gate = Gate(request.HandoffId);
        await gate.WaitAsync();
        try
        {
            var existing = await GetAsync(request.HandoffId, true);
            if (existing != null)
            {
                return Matches(existing.Snapshot, request)
                    ? new(NCCashHandoffMutationStatus.Duplicate, existing, null)
                    : Reject(NCCashHandoffMutationStatus.Rejected,
                        "The cash handoff identity is already bound to different immutable data.");
            }

            var eventId = Guid.NewGuid();
            var snapshot = new NCCashHandoffSnapshot(
                request.HandoffId,
                request.Party,
                request.Direction,
                request.LedgerDelta,
                request.PhysicalAmount,
                request.TaxAmount,
                request.TransactionId,
                null,
                NCCashHandoffStatus.PendingPhysical,
                request.Initiator,
                request.Reference.Trim(),
                now,
                now,
                null,
                1,
                eventId);
            if (!Valid(snapshot, request.HandoffId))
                return Reject(NCCashHandoffMutationStatus.Rejected, "The cash handoff snapshot is invalid.");

            NCEconomyMutationResult? transaction = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var party = await _finance.EnsurePrimaryAccountAsync(request.Party, request.AuditSource,
                    request.CausationId);
                var issuance = await _finance.EnsureIssuanceAccountAsync(request.AuditSource);
                if (!party.IsResolved || !issuance.IsResolved)
                {
                    return Reject(NCCashHandoffMutationStatus.Rejected,
                        party.Error ?? issuance.Error ?? "A cash handoff account is unavailable.");
                }

                var commandId = attempt == 0
                    ? request.CommandId
                    : NCPersistentIdentity.Command(
                        "cash-handoff",
                        "start-attempt",
                        $"{Key(request.HandoffId)}/{party.Account!.AggregateVersion}/" +
                        issuance.Account!.AggregateVersion);
                transaction = await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
                    commandId,
                    request.TransactionId,
                    NCLedgerTransactionKind.Adjustment,
                    snapshot.Reference,
                    [
                        new NCLedgerLegRequest(
                            issuance.AccountId,
                            issuance.Account!.AggregateVersion,
                            -request.LedgerDelta),
                        new NCLedgerLegRequest(
                            party.AccountId,
                            party.Account!.AggregateVersion,
                            request.LedgerDelta),
                    ],
                    request.Initiator,
                    request.CorrelationId,
                    request.CausationId,
                    request.AuditSource,
                    ParticipantWrites:
                    [
                        Participant(snapshot, 0, "living-city.cash-handoff.started", request.AuditSource),
                    ]));
                if (transaction.Status != NCEconomyMutationStatus.VersionConflict)
                    break;

                existing = await GetAsync(request.HandoffId, true);
                if (existing != null)
                {
                    return Matches(existing.Snapshot, request)
                        ? new(NCCashHandoffMutationStatus.Duplicate, existing, null)
                        : Reject(NCCashHandoffMutationStatus.Rejected,
                            "The cash handoff identity changed during an optimistic retry.");
                }
            }

            if (transaction == null)
                return Reject(NCCashHandoffMutationStatus.StorageFailure, "The cash handoff was not attempted.");
            if (transaction.Status == NCEconomyMutationStatus.StorageFailure)
            {
                var reconciliation = await ReconcileStartAsync(request);
                if (reconciliation.Result != null)
                    return reconciliation.Result;
                if (!reconciliation.ReadCompleted)
                {
                    return Reject(NCCashHandoffMutationStatus.OutcomeUnknown,
                        "The cash handoff commit outcome could not be reconciled.");
                }
            }
            if (transaction.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
                return Reject(Map(transaction.Status), transaction.Error ?? "The cash handoff was not committed.");

            var persisted = await GetAsync(request.HandoffId, true);
            return persisted == null
                ? Reject(NCCashHandoffMutationStatus.StorageFailure,
                    "The committed cash handoff could not be reloaded.")
                : new(transaction.Status == NCEconomyMutationStatus.Applied
                        ? NCCashHandoffMutationStatus.Applied
                        : NCCashHandoffMutationStatus.Duplicate,
                    persisted,
                    null);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to start cash handoff {request.HandoffId}: {exception}");
            var reconciliation = await ReconcileStartAsync(request);
            return reconciliation.Result ?? Reject(
                reconciliation.ReadCompleted
                    ? NCCashHandoffMutationStatus.StorageFailure
                    : NCCashHandoffMutationStatus.OutcomeUnknown,
                reconciliation.ReadCompleted
                    ? exception.Message
                    : "The cash handoff commit outcome could not be reconciled.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCCashHandoffMutationResult> CompleteAsync(
        NCCashHandoffId handoffId,
        NCPartyRef initiator,
        string auditSource)
    {
        try
        {
            await _recovery;
        }
        catch (Exception exception)
        {
            return Reject(NCCashHandoffMutationStatus.StorageFailure, exception.Message);
        }
        return await CompleteCoreAsync(handoffId, initiator, auditSource);
    }

    public async Task<NCCashHandoffMutationResult> ReverseAsync(
        NCCashHandoffId handoffId,
        NCPartyRef initiator,
        string auditSource)
    {
        try
        {
            await _recovery;
        }
        catch (Exception exception)
        {
            return Reject(NCCashHandoffMutationStatus.StorageFailure, exception.Message);
        }
        return await ReverseCoreAsync(handoffId, initiator, auditSource);
    }

    private async Task<NCCashHandoffMutationResult> CompleteCoreAsync(
        NCCashHandoffId handoffId,
        NCPartyRef initiator,
        string auditSource)
    {
        if (!handoffId.IsValid || !initiator.IsValid || !ValidAuditSource(auditSource) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCCashHandoffMutationStatus.Rejected :
                NCCashHandoffMutationStatus.ClockUnavailable, "The cash completion request is invalid.");
        }

        var gate = Gate(handoffId);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(handoffId, true);
            if (current == null)
                return Reject(NCCashHandoffMutationStatus.NotFound, "The cash handoff does not exist.");
            if (current.Snapshot.Status == NCCashHandoffStatus.Completed)
                return new(NCCashHandoffMutationStatus.Duplicate, current, null);
            if (current.Snapshot.Status != NCCashHandoffStatus.PendingPhysical)
                return Reject(NCCashHandoffMutationStatus.Rejected, "A reversed handoff cannot complete.");

            var eventId = Guid.NewGuid();
            var next = current.Snapshot with
            {
                Status = NCCashHandoffStatus.Completed,
                UpdatedAt = now,
                ResolvedAt = now,
                DataVersion = checked(current.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            var receipt = new CashReceipt(next, current.AggregateVersion + 1, epoch, now);
            var persisted = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
                NCPersistentIdentity.Command("cash-handoff", "complete", Key(handoffId)),
                "living-city.cash-handoff.complete",
                "cash-handoff",
                Key(handoffId),
                eventId,
                "living-city.cash-handoff.completed",
                AggregateType,
                Key(handoffId),
                current.AggregateVersion,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(next, JsonOptions),
                JsonSerializer.Serialize(new CashEvent(handoffId, initiator, next.Status, auditSource), JsonOptions),
                JsonSerializer.Serialize(receipt, JsonOptions),
                NCPersistentIdentity.Correlation("cash-handoff", "complete", Key(handoffId)),
                null,
                epoch,
                now,
                []));
            return ApplySingle(persisted, receipt);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to complete cash handoff {handoffId}: {exception}");
            return Reject(NCCashHandoffMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<NCCashHandoffMutationResult> ReverseCoreAsync(
        NCCashHandoffId handoffId,
        NCPartyRef initiator,
        string auditSource)
    {
        if (!handoffId.IsValid || !initiator.IsValid || !ValidAuditSource(auditSource) ||
            !_clock.TryGetCurrentTime(out _, out var now))
        {
            return Reject(_clock.IsReady ? NCCashHandoffMutationStatus.Rejected :
                NCCashHandoffMutationStatus.ClockUnavailable, "The cash reversal request is invalid.");
        }

        var gate = Gate(handoffId);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(handoffId, true);
            if (current == null)
                return Reject(NCCashHandoffMutationStatus.NotFound, "The cash handoff does not exist.");
            if (current.Snapshot.Status == NCCashHandoffStatus.Reversed)
                return new(NCCashHandoffMutationStatus.Duplicate, current, null);
            if (current.Snapshot.Status != NCCashHandoffStatus.PendingPhysical)
                return Reject(NCCashHandoffMutationStatus.Rejected, "A completed handoff cannot be reversed.");

            var stableKey = Key(handoffId);
            var eventId = Guid.NewGuid();
            var reversalId = NCPersistentIdentity.Transaction("cash-handoff", "reverse", stableKey);
            var next = current.Snapshot with
            {
                ReversalTransactionId = reversalId,
                Status = NCCashHandoffStatus.Reversed,
                UpdatedAt = now,
                ResolvedAt = now,
                DataVersion = checked(current.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            NCEconomyMutationResult? transaction = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var party = await _finance.EnsurePrimaryAccountAsync(current.Snapshot.Party, auditSource);
                var issuance = await _finance.EnsureIssuanceAccountAsync(auditSource);
                if (!party.IsResolved || !issuance.IsResolved)
                {
                    return Reject(NCCashHandoffMutationStatus.Rejected,
                        party.Error ?? issuance.Error ?? "A cash reversal account is unavailable.");
                }

                var commandId = attempt == 0
                    ? NCPersistentIdentity.Command("cash-handoff", "reverse", stableKey)
                    : NCPersistentIdentity.Command(
                        "cash-handoff",
                        "reverse-attempt",
                        $"{stableKey}/{party.Account!.AggregateVersion}/{issuance.Account!.AggregateVersion}");
                transaction = await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
                    commandId,
                    reversalId,
                    NCLedgerTransactionKind.Refund,
                    $"cash handoff reversal {handoffId}",
                    [
                        new NCLedgerLegRequest(
                            issuance.AccountId,
                            issuance.Account!.AggregateVersion,
                            current.Snapshot.LedgerDelta),
                        new NCLedgerLegRequest(
                            party.AccountId,
                            party.Account!.AggregateVersion,
                            -current.Snapshot.LedgerDelta),
                    ],
                    initiator,
                    NCPersistentIdentity.Correlation("cash-handoff", "reverse", stableKey),
                    AuditSource: auditSource,
                    ParticipantWrites:
                    [
                        Participant(next, current.AggregateVersion, "living-city.cash-handoff.reversed", auditSource),
                    ]));
                if (transaction.Status != NCEconomyMutationStatus.VersionConflict)
                    break;

                var reloaded = await GetAsync(handoffId, true);
                if (reloaded?.Snapshot.Status == NCCashHandoffStatus.Reversed &&
                    reloaded.Snapshot.ReversalTransactionId == reversalId)
                {
                    return new(NCCashHandoffMutationStatus.Duplicate, reloaded, null);
                }
                if (reloaded?.Snapshot.Status != NCCashHandoffStatus.PendingPhysical ||
                    reloaded.AggregateVersion != current.AggregateVersion)
                {
                    return Reject(NCCashHandoffMutationStatus.VersionConflict,
                        "The cash handoff changed during reversal.");
                }
            }

            if (transaction == null)
                return Reject(NCCashHandoffMutationStatus.StorageFailure, "The cash reversal was not attempted.");
            if (transaction.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
                return Reject(Map(transaction.Status), transaction.Error ?? "The cash reversal was not committed.");

            var persisted = await GetAsync(handoffId, true);
            return persisted == null
                ? Reject(NCCashHandoffMutationStatus.StorageFailure,
                    "The reversed cash handoff could not be reloaded.")
                : new(transaction.Status == NCEconomyMutationStatus.Applied
                        ? NCCashHandoffMutationStatus.Applied
                        : NCCashHandoffMutationStatus.Duplicate,
                    persisted,
                    null);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to reverse cash handoff {handoffId}: {exception}");
            return Reject(NCCashHandoffMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RecoverPriorSessionsAsync(NCSessionEpoch currentEpoch)
    {
        var watermark = await LoadRecoveryWatermarkAsync();
        var previousHighWatermark = watermark?.Snapshot.LastScannedHandoffId;
        string? newestRecoveredRangeId = null;
        string? before = null;
        var reachedWatermark = false;
        do
        {
            var page = await _database.GetNCAggregateSnapshotPageDescendingAsync(
                AggregateType,
                before,
                RecoveryPageSize);
            foreach (var stored in page)
            {
                if (previousHighWatermark != null &&
                    string.CompareOrdinal(stored.AggregateId, previousHighWatermark) <= 0)
                {
                    reachedWatermark = true;
                    break;
                }

                if (!Guid.TryParse(stored.AggregateId, out var parsedId) || parsedId == Guid.Empty)
                    throw new InvalidOperationException($"Cash handoff key {stored.AggregateId} is invalid.");
                var record = Deserialize(stored, new NCCashHandoffId(parsedId));
                if (record == null)
                    throw new InvalidOperationException($"Cash handoff {stored.AggregateId} snapshot is invalid.");

                // Current-round records must stay above the watermark so they can be recovered after a later crash.
                if (record.SessionEpoch == currentEpoch)
                    continue;

                newestRecoveredRangeId ??= stored.AggregateId;
                if (record.Snapshot.Status != NCCashHandoffStatus.PendingPhysical)
                    continue;

                var result = record.Snapshot.Direction == NCCashHandoffDirection.Deposit
                    ? await CompleteCoreAsync(record.Snapshot.HandoffId, record.Snapshot.Initiator,
                        "cash-handoff-round-recovery")
                    : await ReverseCoreAsync(record.Snapshot.HandoffId,
                        NCPersistentIdentity.CityService("night-city-monetary-authority"),
                        "cash-handoff-round-recovery");
                if (result.Status is not (NCCashHandoffMutationStatus.Applied or
                    NCCashHandoffMutationStatus.Duplicate))
                {
                    throw new InvalidOperationException(
                        $"Cash handoff {record.Snapshot.HandoffId} recovery failed: {result.Error}");
                }
            }

            if (reachedWatermark || page.Count < RecoveryPageSize)
                break;
            before = page[^1].AggregateId;
        } while (true);

        if (newestRecoveredRangeId != null && newestRecoveredRangeId != previousHighWatermark)
            await AdvanceRecoveryWatermarkAsync(watermark, newestRecoveredRangeId);
    }

    private async Task<StartReconciliation> ReconcileStartAsync(NCStartCashHandoffRequest request)
    {
        try
        {
            var existing = await GetAsync(request.HandoffId, true);
            if (existing == null)
                return new StartReconciliation(true, null);

            return new StartReconciliation(true, Matches(existing.Snapshot, request)
                ? new NCCashHandoffMutationResult(NCCashHandoffMutationStatus.Duplicate, existing, null)
                : Reject(NCCashHandoffMutationStatus.Rejected,
                    "The cash handoff identity is bound to different immutable data."));
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to reconcile cash handoff {request.HandoffId}: {exception}");
            return new StartReconciliation(false, null);
        }
    }

    private async Task<RecoveryWatermarkRecord?> LoadRecoveryWatermarkAsync()
    {
        var stored = await _database.GetNCAggregateSnapshotAsync(RecoveryAggregateType, RecoveryAggregateId);
        if (stored == null)
            return null;
        if (stored.FormatVersion != RecoverySnapshotFormatVersion || !stored.SessionEpoch.IsValid ||
            !stored.ActiveWorldTime.IsValid)
        {
            throw new InvalidOperationException("The cash handoff recovery watermark metadata is invalid.");
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<RecoveryWatermarkSnapshot>(stored.Payload, JsonOptions);
            if (snapshot == null || !Guid.TryParse(snapshot.LastScannedHandoffId, out var handoffId) ||
                handoffId == Guid.Empty || !snapshot.UpdatedAt.IsValid || snapshot.DataVersion < 1 ||
                snapshot.LastAppliedEventId == Guid.Empty)
            {
                throw new InvalidOperationException("The cash handoff recovery watermark snapshot is invalid.");
            }

            return new RecoveryWatermarkRecord(snapshot, stored.AggregateVersion);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The cash handoff recovery watermark payload is invalid.", exception);
        }
    }

    private async Task AdvanceRecoveryWatermarkAsync(RecoveryWatermarkRecord? current, string handoffId)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (current != null &&
                string.CompareOrdinal(current.Snapshot.LastScannedHandoffId, handoffId) >= 0)
            {
                return;
            }
            if (!_clock.TryGetCurrentTime(out var epoch, out var now))
                throw new InvalidOperationException("Active-world time is unavailable during cash recovery.");

            var eventId = Guid.NewGuid();
            var next = new RecoveryWatermarkSnapshot(
                handoffId,
                now,
                checked((current?.Snapshot.DataVersion ?? 0) + 1),
                eventId);
            var expectedVersion = current?.AggregateVersion ?? 0;
            var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
                NCPersistentIdentity.Command(
                    "cash-handoff",
                    "advance-recovery-watermark",
                    $"{handoffId}/{expectedVersion}/{epoch}/{attempt}"),
                "living-city.cash-handoff.advance-recovery-watermark",
                "cash-handoff-recovery",
                RecoveryAggregateId,
                eventId,
                "living-city.cash-handoff.recovery-watermark-advanced",
                RecoveryAggregateType,
                RecoveryAggregateId,
                expectedVersion,
                RecoverySnapshotFormatVersion,
                JsonSerializer.Serialize(next, JsonOptions),
                JsonSerializer.Serialize(new RecoveryWatermarkEvent(handoffId), JsonOptions),
                JsonSerializer.Serialize(next, JsonOptions),
                NCPersistentIdentity.Correlation("cash-handoff", "recovery-watermark", handoffId),
                null,
                epoch,
                now,
                []));
            if (result.Status is NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted)
            {
                var persisted = await LoadRecoveryWatermarkAsync();
                if (persisted != null &&
                    string.CompareOrdinal(persisted.Snapshot.LastScannedHandoffId, handoffId) >= 0)
                {
                    return;
                }
                throw new InvalidOperationException("The advanced cash recovery watermark could not be reloaded.");
            }
            if (result.Status is not (NCPersistentMutationStatus.VersionConflict or
                NCPersistentMutationStatus.DuplicateFailed))
            {
                throw new InvalidOperationException(
                    result.Error ?? "The cash handoff recovery watermark could not be advanced.");
            }

            current = await LoadRecoveryWatermarkAsync();
        }

        throw new InvalidOperationException("The cash handoff recovery watermark exceeded its retry limit.");
    }

    private NCCashHandoffMutationResult ApplySingle(NCPersistentMutationResult result, CashReceipt submitted)
    {
        if (result.Status == NCPersistentMutationStatus.Applied)
        {
            var record = CacheNewest(new NCCashHandoffRecord(
                submitted.Snapshot,
                result.AggregateVersion,
                submitted.SessionEpoch,
                submitted.PersistedAt));
            return new(NCCashHandoffMutationStatus.Applied, record, null);
        }
        if (result.Status == NCPersistentMutationStatus.DuplicateCompleted)
        {
            var receipt = DeserializeReceipt(result.ResultPayload);
            if (receipt != null)
            {
                var record = CacheNewest(new NCCashHandoffRecord(
                    receipt.Snapshot,
                    receipt.AggregateVersion,
                    receipt.SessionEpoch,
                    receipt.PersistedAt));
                return new(NCCashHandoffMutationStatus.Duplicate, record, null);
            }
        }

        return Reject(result.Status is NCPersistentMutationStatus.VersionConflict or
                NCPersistentMutationStatus.DuplicateFailed
                ? NCCashHandoffMutationStatus.VersionConflict
                : result.Status == NCPersistentMutationStatus.ClockRejected
                    ? NCCashHandoffMutationStatus.ClockUnavailable
                    : NCCashHandoffMutationStatus.StorageFailure,
            result.Error ?? "The cash handoff mutation was not committed.");
    }

    private static NCAtomicLedgerParticipantWrite Participant(
        NCCashHandoffSnapshot snapshot,
        long expectedVersion,
        string eventType,
        string auditSource)
    {
        return new NCAtomicLedgerParticipantWrite(
            snapshot.LastAppliedEventId,
            eventType,
            AggregateType,
            Key(snapshot.HandoffId),
            expectedVersion,
            SnapshotFormatVersion,
            JsonSerializer.Serialize(snapshot, JsonOptions),
            JsonSerializer.Serialize(new CashEvent(
                snapshot.HandoffId,
                snapshot.Initiator,
                snapshot.Status,
                auditSource), JsonOptions));
    }

    private static bool ValidStart(NCStartCashHandoffRequest request)
    {
        if (request.CommandId == Guid.Empty || !request.HandoffId.IsValid || !request.TransactionId.IsValid ||
            !request.Party.IsValid || !request.Initiator.IsValid || request.CorrelationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Reference) || request.Reference.Trim().Length > MaximumReferenceLength ||
            !ValidAuditSource(request.AuditSource) || !Enum.IsDefined(request.Direction))
        {
            return false;
        }

        return request.Direction switch
        {
            NCCashHandoffDirection.Deposit => request.LedgerDelta > 0 && request.PhysicalAmount > 0 &&
                request.PhysicalAmount >= request.LedgerDelta &&
                request.TaxAmount == request.PhysicalAmount - request.LedgerDelta,
            NCCashHandoffDirection.Withdrawal => request.LedgerDelta < 0 && request.LedgerDelta != long.MinValue &&
                request.PhysicalAmount == -request.LedgerDelta && request.TaxAmount == 0,
            _ => false,
        };
    }

    private static bool Matches(NCCashHandoffSnapshot snapshot, NCStartCashHandoffRequest request)
    {
        return snapshot.HandoffId == request.HandoffId && snapshot.TransactionId == request.TransactionId &&
               snapshot.Party == request.Party && snapshot.Direction == request.Direction &&
               snapshot.LedgerDelta == request.LedgerDelta && snapshot.PhysicalAmount == request.PhysicalAmount &&
               snapshot.TaxAmount == request.TaxAmount && snapshot.Initiator == request.Initiator &&
               snapshot.Reference == request.Reference.Trim();
    }

    private static bool Valid(NCCashHandoffSnapshot? snapshot, NCCashHandoffId? expected)
    {
        if (snapshot == null || !snapshot.HandoffId.IsValid ||
            expected is { } id && snapshot.HandoffId != id || !snapshot.Party.IsValid ||
            !snapshot.Initiator.IsValid || !snapshot.TransactionId.IsValid || !Enum.IsDefined(snapshot.Direction) ||
            !Enum.IsDefined(snapshot.Status) || string.IsNullOrWhiteSpace(snapshot.Reference) ||
            snapshot.Reference.Trim() != snapshot.Reference || snapshot.Reference.Length > MaximumReferenceLength ||
            !snapshot.CreatedAt.IsValid || snapshot.UpdatedAt < snapshot.CreatedAt || snapshot.DataVersion < 1 ||
            snapshot.LastAppliedEventId == Guid.Empty)
        {
            return false;
        }

        var amountsValid = snapshot.Direction switch
        {
            NCCashHandoffDirection.Deposit => snapshot.LedgerDelta > 0 && snapshot.PhysicalAmount > 0 &&
                snapshot.PhysicalAmount >= snapshot.LedgerDelta &&
                snapshot.TaxAmount == snapshot.PhysicalAmount - snapshot.LedgerDelta,
            NCCashHandoffDirection.Withdrawal => snapshot.LedgerDelta < 0 &&
                snapshot.LedgerDelta != long.MinValue && snapshot.PhysicalAmount == -snapshot.LedgerDelta &&
                snapshot.TaxAmount == 0,
            _ => false,
        };
        if (!amountsValid)
            return false;

        return snapshot.Status switch
        {
            NCCashHandoffStatus.PendingPhysical => snapshot.ResolvedAt == null &&
                snapshot.ReversalTransactionId == null,
            NCCashHandoffStatus.Completed => snapshot.ResolvedAt >= snapshot.CreatedAt &&
                snapshot.ReversalTransactionId == null,
            NCCashHandoffStatus.Reversed => snapshot.ResolvedAt >= snapshot.CreatedAt &&
                snapshot.ReversalTransactionId is { IsValid: true },
            _ => false,
        };
    }

    private static NCCashHandoffRecord? Deserialize(NCAggregateSnapshotData? stored, NCCashHandoffId? expected)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion || !stored.SessionEpoch.IsValid ||
            !stored.ActiveWorldTime.IsValid)
        {
            return null;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<NCCashHandoffSnapshot>(stored.Payload, JsonOptions);
            return Valid(snapshot, expected)
                ? new NCCashHandoffRecord(snapshot!, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CashReceipt? DeserializeReceipt(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<CashReceipt>(payload, JsonOptions);
            return receipt != null && receipt.AggregateVersion >= 1 && receipt.SessionEpoch.IsValid &&
                   receipt.PersistedAt.IsValid && Valid(receipt.Snapshot, receipt.Snapshot.HandoffId)
                ? receipt
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private NCCashHandoffRecord CacheNewest(NCCashHandoffRecord candidate)
    {
        return _cache.AddOrUpdate(candidate.Snapshot.HandoffId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);
    }

    private SemaphoreSlim Gate(NCCashHandoffId handoffId) =>
        _gates[(handoffId.GetHashCode() & int.MaxValue) % _gates.Length];

    private static SemaphoreSlim[] CreateGates()
    {
        var gates = new SemaphoreSlim[MutationGateStripeCount];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static bool ValidAuditSource(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumAuditSourceLength;

    private static string Key(NCCashHandoffId handoffId) => handoffId.Value.ToString("D");

    private static NCCashHandoffMutationStatus Map(NCEconomyMutationStatus status)
    {
        return status switch
        {
            NCEconomyMutationStatus.InsufficientFunds => NCCashHandoffMutationStatus.InsufficientFunds,
            NCEconomyMutationStatus.VersionConflict => NCCashHandoffMutationStatus.VersionConflict,
            NCEconomyMutationStatus.NotFound => NCCashHandoffMutationStatus.NotFound,
            NCEconomyMutationStatus.ClockUnavailable => NCCashHandoffMutationStatus.ClockUnavailable,
            NCEconomyMutationStatus.StorageFailure => NCCashHandoffMutationStatus.StorageFailure,
            _ => NCCashHandoffMutationStatus.Rejected,
        };
    }

    private static NCCashHandoffMutationResult Reject(NCCashHandoffMutationStatus status, string error) =>
        new(status, null, error);

    private sealed record CashEvent(
        NCCashHandoffId HandoffId,
        NCPartyRef Initiator,
        NCCashHandoffStatus Status,
        string AuditSource);

    private sealed record CashReceipt(
        NCCashHandoffSnapshot Snapshot,
        long AggregateVersion,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);

    private sealed record RecoveryWatermarkSnapshot(
        string LastScannedHandoffId,
        NCActiveWorldTime UpdatedAt,
        int DataVersion,
        Guid LastAppliedEventId);

    private sealed record RecoveryWatermarkRecord(
        RecoveryWatermarkSnapshot Snapshot,
        long AggregateVersion);

    private sealed record RecoveryWatermarkEvent(string LastScannedHandoffId);

    private sealed record StartReconciliation(
        bool ReadCompleted,
        NCCashHandoffMutationResult? Result);
}
