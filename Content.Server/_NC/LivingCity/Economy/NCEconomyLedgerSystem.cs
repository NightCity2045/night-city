// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;

namespace Content.Server._NC.LivingCity.Economy;

/// <summary>
/// Common party-owned account and double-entry ledger authority. There is intentionally no public direct balance
/// setter: every balance change is one bounded, idempotent, multi-aggregate transaction whose signed legs sum to zero.
/// </summary>
public sealed partial class NCEconomyLedgerSystem : EntitySystem
{
    public const string AccountAggregateType = "living-city.account";
    public const string TransactionAggregateType = "living-city.ledger-transaction";
    public const int SnapshotFormatVersion = 1;

    private const int MaximumCurrencyIdLength = 32;
    private const int MaximumReferenceLength = 256;
    private const int MaximumAuditSourceLength = 128;
    private const int MaximumLegs = 16;
    private const int MutationGateStripeCount = 128;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly ConcurrentDictionary<NCAccountId, NCAccountRecord> _accountCache = [];
    private readonly ConcurrentDictionary<NCTransactionId, NCLedgerTransactionRecord> _transactionCache = [];
    private readonly SemaphoreSlim[] _mutationGates = CreateMutationGates();

    public int CachedAccountCount => _accountCache.Count;
    public int CachedTransactionCount => _transactionCache.Count;

    public bool TryGetCachedAccount(NCAccountId accountId, out NCAccountRecord record)
    {
        return _accountCache.TryGetValue(accountId, out record!);
    }

    public bool UnloadAccount(NCAccountId accountId)
    {
        return _accountCache.TryRemove(accountId, out _);
    }

    public bool UnloadTransaction(NCTransactionId transactionId)
    {
        return _transactionCache.TryRemove(transactionId, out _);
    }

    public async Task<NCAccountRecord?> GetAccountAsync(NCAccountId accountId, bool forceReload = false)
    {
        if (!accountId.IsValid)
            return null;
        if (!forceReload && _accountCache.TryGetValue(accountId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(AccountAggregateType, AccountKey(accountId));
        var record = DeserializeAccount(stored, accountId);
        return record == null ? null : CacheNewest(record);
    }

    public async Task<NCLedgerTransactionRecord?> GetTransactionAsync(
        NCTransactionId transactionId,
        bool forceReload = false)
    {
        if (!transactionId.IsValid)
            return null;
        if (!forceReload && _transactionCache.TryGetValue(transactionId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(
            TransactionAggregateType,
            TransactionKey(transactionId));
        var record = DeserializeTransaction(stored, transactionId);
        return record == null ? null : CacheNewest(record);
    }

    /// <summary>
    /// Lets another authoritative domain validate account ownership/status while excluding ledger mutations.
    /// The callback must remain bounded and must not acquire a second economy scope in a different order.
    /// </summary>
    public async Task<T> RunWithAccountLocksAsync<T>(
        IReadOnlyCollection<NCAccountId> accountIds,
        Func<Task<T>> operation)
    {
        if (accountIds.Count == 0 || accountIds.Any(static id => !id.IsValid))
            throw new ArgumentException("At least one valid account is required.", nameof(accountIds));

        var gates = await AcquireGates(accountIds);
        try
        {
            return await operation();
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    public async Task<NCEconomyMutationResult> CreateAccountAsync(NCCreateAccountRequest request)
    {
        var currency = NormalizeCurrency(request.CurrencyId);
        if (!TryValidateMetadata(request.CommandId, request.CorrelationId, request.AuditSource, out var error) ||
            !request.AccountId.IsValid || !request.Owner.IsValid || currency == null ||
            !CanOwnAccount(request.Owner, request.Kind) ||
            request.AllowsNegativeBalance && request.Kind != NCAccountKind.SystemClearing ||
            request.Kind == NCAccountKind.SystemClearing && request.Owner.Kind != NCPartyKind.CityService ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCEconomyMutationStatus.Rejected :
                    NCEconomyMutationStatus.ClockUnavailable,
                error ?? "The account request or active-world clock is invalid.");
        }

        var replay = await TryReplay(request.CommandId, "living-city.account.create", "account",
            AccountKey(request.AccountId));
        if (replay != null)
            return replay;

        var owner = await _parties.ResolveAsync(request.Owner, true);
        if (!owner.IsResolved)
            return Reject(NCEconomyMutationStatus.Rejected, owner.Error ?? "The account owner is unavailable.");

        var gate = GetMutationGate(request.AccountId);
        await gate.WaitAsync();
        try
        {
            var eventId = Guid.NewGuid();
            var snapshot = new NCAccountSnapshot(
                request.AccountId,
                request.Owner,
                request.Kind,
                currency,
                0,
                request.AllowsNegativeBalance,
                NCAccountStatus.Active,
                now,
                now,
                1,
                eventId);
            var receipt = new EconomyReceipt(
                [new AccountReceipt(snapshot, 1)],
                null,
                epoch,
                now);
            var persisted = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
                request.CommandId,
                "living-city.account.create",
                "account",
                AccountKey(request.AccountId),
                eventId,
                "living-city.account.created",
                AccountAggregateType,
                AccountKey(request.AccountId),
                0,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(snapshot, JsonOptions),
                JsonSerializer.Serialize(new AccountEvent(
                    request.AccountId,
                    request.Owner,
                    "living-city.account.created",
                    request.AuditSource,
                    null,
                    0,
                    NCAccountStatus.Active), JsonOptions),
                JsonSerializer.Serialize(receipt, JsonOptions),
                request.CorrelationId,
                request.CausationId,
                epoch,
                now,
                []));
            return ApplySingleResult(persisted, snapshot, epoch, now);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to create account {request.AccountId}: {exception}");
            return Reject(NCEconomyMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCEconomyMutationResult> SetAccountStatusAsync(NCSetAccountStatusRequest request)
    {
        if (!TryValidateMetadata(request.CommandId, request.CorrelationId, request.AuditSource, out var error) ||
            !request.AccountId.IsValid || request.ExpectedVersion < 1 || !request.Initiator.IsValid ||
            !Enum.IsDefined(request.Status) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCEconomyMutationStatus.Rejected :
                    NCEconomyMutationStatus.ClockUnavailable,
                error ?? "The account status request or active-world clock is invalid.");
        }

        var replay = await TryReplay(request.CommandId, "living-city.account.set-status", "account",
            AccountKey(request.AccountId));
        if (replay != null)
            return replay;

        var initiator = await _parties.ResolveAsync(request.Initiator, true);
        if (!initiator.IsResolved)
            return Reject(NCEconomyMutationStatus.Rejected, initiator.Error ?? "The initiator is unavailable.");

        var gate = GetMutationGate(request.AccountId);
        await gate.WaitAsync();
        try
        {
            var current = await GetAccountAsync(request.AccountId);
            if (current == null)
                return Reject(NCEconomyMutationStatus.NotFound, "The account does not exist.");
            if (current.Snapshot.Status == NCAccountStatus.Closed && request.Status != NCAccountStatus.Closed)
                return Reject(NCEconomyMutationStatus.Rejected, "A closed account cannot be reopened.");
            if (request.Status == NCAccountStatus.Closed && current.Snapshot.Balance != 0)
                return Reject(NCEconomyMutationStatus.Rejected, "An account with a non-zero balance cannot close.");

            var eventId = Guid.NewGuid();
            var snapshot = current.Snapshot with
            {
                Status = request.Status,
                UpdatedAt = now,
                DataVersion = current.Snapshot.DataVersion + 1,
                LastAppliedEventId = eventId,
            };
            var receipt = new EconomyReceipt(
                [new AccountReceipt(snapshot, request.ExpectedVersion + 1)],
                null,
                epoch,
                now);
            var persisted = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
                request.CommandId,
                "living-city.account.set-status",
                "account",
                AccountKey(request.AccountId),
                eventId,
                "living-city.account.status-changed",
                AccountAggregateType,
                AccountKey(request.AccountId),
                request.ExpectedVersion,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(snapshot, JsonOptions),
                JsonSerializer.Serialize(new AccountEvent(
                    request.AccountId,
                    current.Snapshot.Owner,
                    "living-city.account.status-changed",
                    request.AuditSource,
                    null,
                    current.Snapshot.Balance,
                    request.Status), JsonOptions),
                JsonSerializer.Serialize(receipt, JsonOptions),
                request.CorrelationId,
                request.CausationId,
                epoch,
                now,
                []));
            return ApplySingleResult(persisted, snapshot, epoch, now);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to set account {request.AccountId} status: {exception}");
            return Reject(NCEconomyMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCEconomyMutationResult> PostTransactionAsync(NCPostLedgerTransactionRequest request)
    {
        if (!TryValidateTransaction(request, out var error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCEconomyMutationStatus.Rejected :
                    NCEconomyMutationStatus.ClockUnavailable,
                error ?? "The ledger request or active-world clock is invalid.");
        }

        var replay = await TryReplay(request.CommandId, "living-city.ledger.post", "transaction",
            TransactionKey(request.TransactionId));
        if (replay != null)
            return replay;

        var initiator = await _parties.ResolveAsync(request.Initiator, true);
        if (!initiator.IsResolved)
            return Reject(NCEconomyMutationStatus.Rejected, initiator.Error ?? "The initiator is unavailable.");

        var gates = await AcquireGates(request.Legs.Select(static leg => leg.AccountId));
        try
        {
            var accounts = new List<NCAccountRecord>(request.Legs.Length);
            foreach (var leg in request.Legs)
            {
                var account = await GetAccountAsync(leg.AccountId);
                if (account == null)
                    return Reject(NCEconomyMutationStatus.NotFound, $"Account {leg.AccountId} does not exist.");
                accounts.Add(account);
            }

            var currency = accounts[0].Snapshot.CurrencyId;
            if (accounts.Any(account => account.Snapshot.CurrencyId != currency))
                return Reject(NCEconomyMutationStatus.Rejected, "All ledger legs must use one currency.");
            if (accounts.Any(account => account.Snapshot.Status != NCAccountStatus.Active))
                return Reject(NCEconomyMutationStatus.Rejected, "Frozen or closed accounts cannot post transactions.");

            var accountWrites = new List<NCPersistentAggregateWrite>(accounts.Count + 1);
            var accountReceipts = new List<AccountReceipt>(accounts.Count);
            var transactionLegs = new NCLedgerLeg[accounts.Count];
            for (var i = 0; i < accounts.Count; i++)
            {
                var current = accounts[i];
                var requestedLeg = request.Legs[i];
                var calculated = (decimal) current.Snapshot.Balance + requestedLeg.Amount;
                if (calculated is < long.MinValue or > long.MaxValue)
                    return Reject(NCEconomyMutationStatus.Rejected, "The transaction would overflow an account balance.");
                var balance = (long) calculated;
                if (balance < 0 && !current.Snapshot.AllowsNegativeBalance)
                    return Reject(NCEconomyMutationStatus.InsufficientFunds,
                        $"Account {current.Snapshot.AccountId} has insufficient funds.");

                var accountEventId = Guid.NewGuid();
                var next = current.Snapshot with
                {
                    Balance = balance,
                    UpdatedAt = now,
                    DataVersion = current.Snapshot.DataVersion + 1,
                    LastAppliedEventId = accountEventId,
                };
                transactionLegs[i] = new NCLedgerLeg(requestedLeg.AccountId, requestedLeg.Amount, balance);
                accountReceipts.Add(new AccountReceipt(next, requestedLeg.ExpectedVersion + 1));
                accountWrites.Add(new NCPersistentAggregateWrite(
                    accountEventId,
                    "living-city.account.ledger-posted",
                    AccountAggregateType,
                    AccountKey(requestedLeg.AccountId),
                    requestedLeg.ExpectedVersion,
                    SnapshotFormatVersion,
                    JsonSerializer.Serialize(next, JsonOptions),
                    JsonSerializer.Serialize(new AccountEvent(
                        requestedLeg.AccountId,
                        next.Owner,
                        "living-city.account.ledger-posted",
                        request.AuditSource,
                        request.TransactionId,
                        balance,
                        next.Status), JsonOptions)));
            }

            var transactionEventId = Guid.NewGuid();
            var transaction = new NCLedgerTransactionSnapshot(
                request.TransactionId,
                request.Kind,
                currency,
                request.Reference.Trim(),
                transactionLegs,
                request.Initiator,
                now,
                1,
                transactionEventId);
            accountWrites.Add(new NCPersistentAggregateWrite(
                transactionEventId,
                "living-city.ledger.transaction-posted",
                TransactionAggregateType,
                TransactionKey(request.TransactionId),
                0,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(transaction, JsonOptions),
                JsonSerializer.Serialize(new TransactionEvent(
                    request.TransactionId,
                    request.Kind,
                    request.AuditSource,
                    currency,
                    transactionLegs), JsonOptions)));

            var receipt = new EconomyReceipt(
                accountReceipts.ToArray(),
                new TransactionReceipt(transaction, 1),
                epoch,
                now);
            var persisted = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(
                request.CommandId,
                "living-city.ledger.post",
                "transaction",
                TransactionKey(request.TransactionId),
                accountWrites,
                JsonSerializer.Serialize(receipt, JsonOptions),
                request.CorrelationId,
                request.CausationId,
                epoch,
                now,
                []));
            return ApplyBatchResult(persisted, receipt);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to post ledger transaction {request.TransactionId}: {exception}");
            return Reject(NCEconomyMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    private async Task<NCEconomyMutationResult?> TryReplay(
        Guid commandId,
        string commandType,
        string scopeType,
        string scopeId)
    {
        var existing = await _database.GetNCPersistentCommandResultAsync(commandId);
        if (existing == null)
            return null;
        if (existing.CommandType != commandType || existing.ScopeType != scopeType || existing.ScopeId != scopeId)
            return Reject(NCEconomyMutationStatus.StorageFailure,
                "The command ID is already owned by another economy operation.");
        if (existing.Status != NCPersistentMutationStatus.DuplicateCompleted)
        {
            return Reject(existing.Status == NCPersistentMutationStatus.DuplicateFailed
                    ? NCEconomyMutationStatus.VersionConflict
                    : NCEconomyMutationStatus.StorageFailure,
                existing.Error ?? "The economy command is incomplete.");
        }

        var receipt = DeserializeReceipt(existing.ResultPayload);
        return receipt == null
            ? Reject(NCEconomyMutationStatus.StorageFailure, "The economy command receipt is invalid.")
            : ApplyReceipt(receipt, NCEconomyMutationStatus.Duplicate);
    }

    private NCEconomyMutationResult ApplySingleResult(
        NCPersistentMutationResult result,
        NCAccountSnapshot snapshot,
        NCSessionEpoch epoch,
        NCActiveWorldTime now)
    {
        if (result.Status == NCPersistentMutationStatus.Applied)
        {
            var record = CacheNewest(new NCAccountRecord(snapshot, result.AggregateVersion, epoch, now));
            Publish(record);
            return new NCEconomyMutationResult(NCEconomyMutationStatus.Applied, [record], null, null);
        }

        if (result.Status == NCPersistentMutationStatus.DuplicateCompleted)
        {
            var receipt = DeserializeReceipt(result.ResultPayload);
            return receipt == null
                ? Reject(NCEconomyMutationStatus.StorageFailure, "The economy command receipt is invalid.")
                : ApplyReceipt(receipt, NCEconomyMutationStatus.Duplicate);
        }

        return Reject(MapFailure(result.Status, result.Error), result.Error ?? "The account mutation was not committed.");
    }

    private NCEconomyMutationResult ApplyBatchResult(
        NCPersistentBatchMutationResult result,
        EconomyReceipt submitted)
    {
        if (result.Status == NCPersistentMutationStatus.Applied)
        {
            var versions = result.Aggregates.ToDictionary(
                aggregate => (aggregate.AggregateType, aggregate.AggregateId),
                aggregate => aggregate.AggregateVersion);
            var receipt = submitted with
            {
                Accounts = submitted.Accounts.Select(account => account with
                {
                    AggregateVersion = versions[(AccountAggregateType, AccountKey(account.Snapshot.AccountId))],
                }).ToArray(),
                Transaction = submitted.Transaction == null
                    ? null
                    : submitted.Transaction with
                    {
                        AggregateVersion = versions[(TransactionAggregateType,
                            TransactionKey(submitted.Transaction.Snapshot.TransactionId))],
                    },
            };
            return ApplyReceipt(receipt, NCEconomyMutationStatus.Applied);
        }

        if (result.Status == NCPersistentMutationStatus.DuplicateCompleted)
        {
            var receipt = DeserializeReceipt(result.ResultPayload);
            return receipt == null
                ? Reject(NCEconomyMutationStatus.StorageFailure, "The ledger receipt is invalid.")
                : ApplyReceipt(receipt, NCEconomyMutationStatus.Duplicate);
        }

        return Reject(MapFailure(result.Status, result.Error), result.Error ?? "The ledger transaction was not committed.");
    }

    private NCEconomyMutationResult ApplyReceipt(EconomyReceipt receipt, NCEconomyMutationStatus status)
    {
        var accounts = new NCAccountRecord[receipt.Accounts.Length];
        for (var i = 0; i < receipt.Accounts.Length; i++)
        {
            var item = receipt.Accounts[i];
            accounts[i] = CacheNewest(new NCAccountRecord(
                item.Snapshot,
                item.AggregateVersion,
                receipt.SessionEpoch,
                receipt.PersistedAt));
            Publish(accounts[i]);
        }

        NCLedgerTransactionRecord? transaction = null;
        if (receipt.Transaction != null)
        {
            transaction = CacheNewest(new NCLedgerTransactionRecord(
                receipt.Transaction.Snapshot,
                receipt.Transaction.AggregateVersion,
                receipt.SessionEpoch,
                receipt.PersistedAt));
        }

        return new NCEconomyMutationResult(status, accounts, transaction, null);
    }

    private void Publish(NCAccountRecord account)
    {
        _taskManager.RunOnMainThread(() => RaiseLocalEvent(new NCAccountPersistedEvent(
            account.Snapshot.AccountId,
            account.Snapshot.Owner,
            account.AggregateVersion,
            account.Snapshot.Balance,
            account.Snapshot.Status)));
    }

    private static bool TryValidateMetadata(Guid commandId, Guid correlationId, string auditSource, out string? error)
    {
        if (commandId == Guid.Empty || correlationId == Guid.Empty || string.IsNullOrWhiteSpace(auditSource) ||
            auditSource.Length > MaximumAuditSourceLength)
        {
            error = "The economy command metadata is invalid.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateTransaction(NCPostLedgerTransactionRequest request, out string? error)
    {
        if (!TryValidateMetadata(request.CommandId, request.CorrelationId, request.AuditSource, out error) ||
            !request.TransactionId.IsValid || !request.Initiator.IsValid || request.Legs == null ||
            request.Legs.Length is < 2 or > MaximumLegs || string.IsNullOrWhiteSpace(request.Reference) ||
            request.Reference.Trim().Length > MaximumReferenceLength || !Enum.IsDefined(request.Kind))
        {
            error ??= "The ledger transaction metadata is invalid.";
            return false;
        }

        var accounts = new HashSet<NCAccountId>();
        decimal sum = 0;
        foreach (var leg in request.Legs)
        {
            if (!leg.AccountId.IsValid || leg.ExpectedVersion < 1 || leg.Amount == 0 ||
                !accounts.Add(leg.AccountId))
            {
                error = "Ledger legs must reference distinct valid accounts and non-zero amounts.";
                return false;
            }
            sum += leg.Amount;
        }

        if (sum != 0)
        {
            error = "Double-entry ledger legs must sum to zero.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool CanOwnAccount(NCPartyRef owner, NCAccountKind kind)
    {
        return kind switch
        {
            NCAccountKind.Wallet or NCAccountKind.Bank => owner.Kind is NCPartyKind.Character or NCPartyKind.Actor,
            NCAccountKind.Organization => owner.Kind is NCPartyKind.Organization or NCPartyKind.CityService,
            NCAccountKind.Business => owner.Kind == NCPartyKind.Business,
            NCAccountKind.Estate => owner.Kind == NCPartyKind.Estate,
            NCAccountKind.SystemClearing => owner.Kind == NCPartyKind.CityService,
            NCAccountKind.Escrow => owner.Kind is NCPartyKind.Organization or NCPartyKind.Business or
                NCPartyKind.CityService or NCPartyKind.Estate,
            _ => false,
        };
    }

    private static string? NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return null;

        var value = currency.Trim().ToUpperInvariant();
        return value.Length is > 0 and <= MaximumCurrencyIdLength ? value : null;
    }

    private static NCAccountRecord? DeserializeAccount(NCAggregateSnapshotData? stored, NCAccountId accountId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCAccountSnapshot>(stored.Payload, JsonOptions);
            return ValidAccount(snapshot, accountId)
                ? new NCAccountRecord(snapshot!, stored.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NCLedgerTransactionRecord? DeserializeTransaction(
        NCAggregateSnapshotData? stored,
        NCTransactionId transactionId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCLedgerTransactionSnapshot>(stored.Payload, JsonOptions);
            return ValidTransaction(snapshot, transactionId)
                ? new NCLedgerTransactionRecord(
                    snapshot!,
                    stored.AggregateVersion,
                    stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static EconomyReceipt? DeserializeReceipt(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<EconomyReceipt>(payload, JsonOptions);
            if (receipt == null || !receipt.SessionEpoch.IsValid || !receipt.PersistedAt.IsValid ||
                receipt.Accounts == null || receipt.Accounts.Length is < 1 or > MaximumLegs ||
                receipt.Accounts.Any(account => account.AggregateVersion < 1 ||
                    !ValidAccount(account.Snapshot, account.Snapshot.AccountId)) ||
                receipt.Transaction is { } transaction &&
                (transaction.AggregateVersion < 1 ||
                 !ValidTransaction(transaction.Snapshot, transaction.Snapshot.TransactionId)))
            {
                return null;
            }
            return receipt;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ValidAccount(NCAccountSnapshot? snapshot, NCAccountId accountId)
    {
        return snapshot != null && accountId.IsValid && snapshot.AccountId == accountId && snapshot.Owner.IsValid &&
               NormalizeCurrency(snapshot.CurrencyId) == snapshot.CurrencyId &&
               (snapshot.Balance >= 0 || snapshot.AllowsNegativeBalance) &&
               (!snapshot.AllowsNegativeBalance || snapshot.Kind == NCAccountKind.SystemClearing) &&
               Enum.IsDefined(snapshot.Status) &&
               snapshot.CreatedAt.IsValid && snapshot.UpdatedAt.IsValid && snapshot.DataVersion >= 1 &&
               snapshot.LastAppliedEventId != Guid.Empty && CanOwnAccount(snapshot.Owner, snapshot.Kind);
    }

    private static bool ValidTransaction(NCLedgerTransactionSnapshot? snapshot, NCTransactionId transactionId)
    {
        if (snapshot == null || !transactionId.IsValid || snapshot.TransactionId != transactionId ||
            NormalizeCurrency(snapshot.CurrencyId) != snapshot.CurrencyId || snapshot.Legs == null ||
            snapshot.Legs.Length is < 2 or > MaximumLegs || !snapshot.Initiator.IsValid ||
            !snapshot.PostedAt.IsValid || snapshot.DataVersion != 1 || snapshot.LastAppliedEventId == Guid.Empty ||
            !Enum.IsDefined(snapshot.Kind) || string.IsNullOrWhiteSpace(snapshot.Reference) ||
            snapshot.Reference.Length > MaximumReferenceLength || snapshot.Reference.Trim() != snapshot.Reference ||
            snapshot.Legs.Select(static leg => leg.AccountId).Distinct().Count() != snapshot.Legs.Length)
        {
            return false;
        }

        decimal sum = 0;
        foreach (var leg in snapshot.Legs)
        {
            if (!leg.AccountId.IsValid || leg.Amount == 0)
                return false;
            sum += leg.Amount;
        }
        return sum == 0;
    }

    private NCAccountRecord CacheNewest(NCAccountRecord candidate)
    {
        return _accountCache.AddOrUpdate(candidate.Snapshot.AccountId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);
    }

    private NCLedgerTransactionRecord CacheNewest(NCLedgerTransactionRecord candidate)
    {
        return _transactionCache.AddOrUpdate(candidate.Snapshot.TransactionId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);
    }

    private SemaphoreSlim GetMutationGate(NCAccountId accountId)
    {
        return _mutationGates[GateIndex(accountId)];
    }

    private async Task<int[]> AcquireGates(IEnumerable<NCAccountId> accountIds)
    {
        var indices = accountIds.Select(GateIndex).Distinct().Order().ToArray();
        foreach (var index in indices)
            await _mutationGates[index].WaitAsync();
        return indices;
    }

    private void ReleaseGates(int[] indices)
    {
        for (var i = indices.Length - 1; i >= 0; i--)
            _mutationGates[indices[i]].Release();
    }

    private static int GateIndex(NCAccountId accountId)
    {
        return (accountId.Value.GetHashCode() & int.MaxValue) % MutationGateStripeCount;
    }

    private static SemaphoreSlim[] CreateMutationGates()
    {
        var gates = new SemaphoreSlim[MutationGateStripeCount];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static NCEconomyMutationStatus MapFailure(NCPersistentMutationStatus status, string? error)
    {
        if (status is NCPersistentMutationStatus.VersionConflict or NCPersistentMutationStatus.DuplicateFailed)
            return error?.StartsWith("The batch timestamp ") == true
                ? NCEconomyMutationStatus.ClockUnavailable
                : NCEconomyMutationStatus.VersionConflict;
        return NCEconomyMutationStatus.StorageFailure;
    }

    private static string AccountKey(NCAccountId accountId) => accountId.Value.ToString("D");
    private static string TransactionKey(NCTransactionId transactionId) => transactionId.Value.ToString("D");

    private static NCEconomyMutationResult Reject(NCEconomyMutationStatus status, string error)
    {
        return new NCEconomyMutationResult(status, [], null, error);
    }

    private sealed record AccountEvent(
        NCAccountId AccountId,
        NCPartyRef Owner,
        string EventType,
        string AuditSource,
        NCTransactionId? TransactionId,
        long BalanceAfter,
        NCAccountStatus Status);

    private sealed record TransactionEvent(
        NCTransactionId TransactionId,
        NCLedgerTransactionKind Kind,
        string AuditSource,
        string CurrencyId,
        NCLedgerLeg[] Legs);

    private sealed record AccountReceipt(NCAccountSnapshot Snapshot, long AggregateVersion);
    private sealed record TransactionReceipt(NCLedgerTransactionSnapshot Snapshot, long AggregateVersion);
    private sealed record EconomyReceipt(
        AccountReceipt[] Accounts,
        TransactionReceipt? Transaction,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);
}
