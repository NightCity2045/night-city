// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Threading.Tasks;
using System.Linq;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.Bank;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Economy;

/// <summary>
/// Gives characters and persistent actors the same primary-account boundary. It never reads a runtime EntityUid and
/// never mutates a balance directly. A failed command receipt is immutable, so a genuine optimistic retry uses a new
/// attempt command while retaining the domain's stable transaction identity.
/// </summary>
public sealed partial class NCPartyFinanceAdapterSystem : EntitySystem
{
    private static readonly ProtoId<NCBankConfigurationPrototype> DefaultConfiguration = "Default";
    private const string MonetaryAuthorityId = "night-city-monetary-authority";
    private const string MonetaryAuthorityName = "Night City Monetary Authority";
    private const string IssuanceAccountId = "electronic-currency-issuance";

    [Dependency] private NCPartyDirectorySystem _directory = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public NCBankConfigurationPrototype Configuration => _prototypes.Index(DefaultConfiguration);

    public NCAccountId GetPrimaryAccountId(NCPartyRef owner)
    {
        return NCPersistentIdentity.PrimaryAccount(owner);
    }

    public Task<NCAccountRecord?> GetPrimaryAccountAsync(NCPartyRef owner, bool forceReload = false)
    {
        return owner.IsValid
            ? _ledger.GetAccountAsync(GetPrimaryAccountId(owner), forceReload)
            : Task.FromResult<NCAccountRecord?>(null);
    }

    /// <summary>
    /// Reconstructs a bounded newest-first account history from account journal events and immutable transactions.
    /// A missing or malformed transaction fails the entire page so an audit UI never presents a silently truncated log.
    /// </summary>
    public async Task<NCPartyTransactionHistoryPage> GetTransactionHistoryAsync(
        NCPartyRef owner,
        long? beforeAggregateVersion,
        int limit)
    {
        var accountId = GetPrimaryAccountId(owner);
        if (!owner.IsValid || limit is < 1 or > 200 || beforeAggregateVersion is < 1)
            return new(owner, accountId, [], null, "The account history request is invalid.");

        var account = await _ledger.GetAccountAsync(accountId);
        if (account == null || account.Snapshot.Owner != owner)
            return new(owner, accountId, [], null, "The party account does not exist.");

        var events = await _database.GetNCPersistentEventPageAsync(
            NCEconomyLedgerSystem.AccountAggregateType,
            accountId.Value.ToString("D"),
            NCEconomyLedgerSystem.AccountLedgerPostedEventType,
            beforeAggregateVersion,
            limit);
        var entries = new List<NCPartyTransactionHistoryEntry>(events.Count);
        foreach (var journalEvent in events)
        {
            AccountLedgerEventProjection? projection;
            try
            {
                projection = JsonSerializer.Deserialize<AccountLedgerEventProjection>(
                    journalEvent.Payload,
                    HistoryJsonOptions);
            }
            catch (JsonException)
            {
                return new(owner, accountId, [], null,
                    $"Account journal event {journalEvent.EventId:D} has an invalid payload.");
            }

            if (projection == null || !projection.TransactionId.IsValid)
            {
                return new(owner, accountId, [], null,
                    $"Account journal event {journalEvent.EventId:D} has no transaction identity.");
            }

            var transaction = await _ledger.GetTransactionAsync(projection.TransactionId);
            if (transaction == null || transaction.Snapshot.Legs.All(leg => leg.AccountId != accountId))
            {
                return new(owner, accountId, [], null,
                    $"Ledger transaction {projection.TransactionId} is missing or does not reference the account.");
            }

            entries.Add(new NCPartyTransactionHistoryEntry(
                transaction,
                journalEvent.CreatedAt,
                journalEvent.AggregateVersion));
        }

        long? next = events.Count == limit ? events[^1].AggregateVersion : null;
        return new(owner, accountId, entries, next, null);
    }

    public async Task<NCPartyAccountResolution> EnsurePrimaryAccountAsync(
        NCPartyRef owner,
        string auditSource,
        Guid? causationId = null)
    {
        if (!owner.IsValid)
            return new(owner, default, null, "The account owner is invalid.");

        var kind = owner.Kind switch
        {
            NCPartyKind.Character or NCPartyKind.Actor => NCAccountKind.Bank,
            NCPartyKind.Organization or NCPartyKind.CityService => NCAccountKind.Organization,
            NCPartyKind.Business => NCAccountKind.Business,
            NCPartyKind.Estate => NCAccountKind.Estate,
            _ => (NCAccountKind?) null,
        };
        if (kind == null)
            return new(owner, default, null, "This party kind cannot own a primary account.");

        var accountId = GetPrimaryAccountId(owner);
        var stableKey = owner.ToString();
        var result = await _ledger.CreateAccountAsync(new NCCreateAccountRequest(
            NCPersistentIdentity.Command("economy", "ensure-primary-account", stableKey),
            accountId,
            owner,
            kind.Value,
            Configuration.CurrencyId,
            false,
            NCPersistentIdentity.Correlation("economy", "ensure-primary-account", stableKey),
            causationId,
            auditSource));
        if (result.Status == NCEconomyMutationStatus.Applied)
            return new(owner, accountId, result.Accounts.Single(), null);

        // A duplicate create receipt can contain the version-1 account snapshot even after later transactions.
        // Always reload current state before constructing a new mutation from its optimistic version.
        var existing = await _ledger.GetAccountAsync(accountId, true);
        if (existing != null && existing.Snapshot.Owner == owner && existing.Snapshot.Kind == kind &&
            existing.Snapshot.CurrencyId == Configuration.CurrencyId.Trim().ToUpperInvariant() &&
            !existing.Snapshot.AllowsNegativeBalance)
        {
            return new(owner, accountId, existing, null);
        }

        return new(owner, accountId, null, result.Error ?? "The primary account could not be created.");
    }

    public async Task<NCPartyAccountResolution> EnsureOrganizationAsync(
        string organizationPrototypeId,
        string displayName,
        string auditSource)
    {
        if (string.IsNullOrWhiteSpace(organizationPrototypeId) || string.IsNullOrWhiteSpace(displayName))
            return new(default, default, null, "The organization identity is invalid.");

        var owner = NCPersistentIdentity.Organization(organizationPrototypeId);
        var sourceKey = organizationPrototypeId.Trim();
        var registration = await _directory.RegisterAsync(new NCPartyDirectoryRegisterRequest(
            NCPersistentIdentity.Command("economy", "register-organization", sourceKey),
            owner,
            displayName.Trim(),
            NCPersistentIdentity.Correlation("economy", "register-organization", sourceKey),
            AuditSource: auditSource));
        if (registration.Status is not (NCPartyDirectoryMutationStatus.Applied or
            NCPartyDirectoryMutationStatus.Duplicate))
        {
            var existing = await _directory.GetAsync(owner, true);
            if (existing == null || existing.Snapshot.Lifecycle != NCPartyLifecycle.Active)
                return new(owner, default, null, registration.Error ?? "The organization could not be registered.");
        }

        return await EnsurePrimaryAccountAsync(owner, auditSource);
    }

    public async Task<NCPartyAccountResolution> EnsureIssuanceAccountAsync(string auditSource)
    {
        var owner = NCPersistentIdentity.CityService(MonetaryAuthorityId);
        var accountId = NCPersistentIdentity.SystemAccount(IssuanceAccountId);
        var registration = await _directory.RegisterAsync(new NCPartyDirectoryRegisterRequest(
            NCPersistentIdentity.Command("economy", "register-monetary-authority", MonetaryAuthorityId),
            owner,
            MonetaryAuthorityName,
            NCPersistentIdentity.Correlation("economy", "register-monetary-authority", MonetaryAuthorityId),
            AuditSource: auditSource));
        if (registration.Status is not (NCPartyDirectoryMutationStatus.Applied or
            NCPartyDirectoryMutationStatus.Duplicate))
        {
            var existing = await _directory.GetAsync(owner, true);
            if (existing == null || existing.Snapshot.Lifecycle != NCPartyLifecycle.Active)
                return new(owner, accountId, null,
                    registration.Error ?? "The monetary authority could not be registered.");
        }

        var result = await _ledger.CreateAccountAsync(new NCCreateAccountRequest(
            NCPersistentIdentity.Command("economy", "ensure-issuance-account", IssuanceAccountId),
            accountId,
            owner,
            NCAccountKind.SystemClearing,
            Configuration.CurrencyId,
            true,
            NCPersistentIdentity.Correlation("economy", "ensure-issuance-account", IssuanceAccountId),
            AuditSource: auditSource));
        if (result.Status == NCEconomyMutationStatus.Applied)
            return new(owner, accountId, result.Accounts.Single(), null);

        // See EnsurePrimaryAccountAsync: replayed create receipts are historical and cannot supply a current version.
        var account = await _ledger.GetAccountAsync(accountId, true);
        return account != null && account.Snapshot.Owner == owner && account.Snapshot.Kind == NCAccountKind.SystemClearing &&
               account.Snapshot.AllowsNegativeBalance
            ? new(owner, account.Snapshot.AccountId, account, null)
            : new(owner, accountId, null,
                result.Error ?? "The issuance account could not be created.");
    }

    /// <summary>
    /// Applies one balanced party/issuance adjustment. Positive values issue currency to the party; negative values
    /// retire it. Callers own the physical or gameplay-side validation and supply a durable idempotency key.
    /// </summary>
    public async Task<NCEconomyMutationResult> AdjustWithIssuanceAsync(NCPartyBalanceAdjustmentRequest request)
    {
        if (!request.Party.IsValid || !request.Initiator.IsValid || request.Amount == 0)
            return new(NCEconomyMutationStatus.Rejected, [], null, "The party balance adjustment is invalid.");

        var party = await EnsurePrimaryAccountAsync(request.Party, request.AuditSource, request.CausationId);
        var issuance = await EnsureIssuanceAccountAsync(request.AuditSource);
        if (!party.IsResolved || !issuance.IsResolved)
        {
            return new(NCEconomyMutationStatus.Rejected, [], null,
                party.Error ?? issuance.Error ?? "An adjustment account is unavailable.");
        }

        return await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
            request.CommandId,
            request.TransactionId,
            request.Kind,
            request.Reference,
            [
                new NCLedgerLegRequest(issuance.AccountId, issuance.Account!.AggregateVersion, -request.Amount),
                new NCLedgerLegRequest(party.AccountId, party.Account!.AggregateVersion, request.Amount),
            ],
            request.Initiator,
            request.CorrelationId,
            request.CausationId,
            request.AuditSource));
    }

    /// <summary>
    /// Opens a primary account with one deterministic opening balance. Replays can never issue the balance twice.
    /// </summary>
    public async Task<NCPartyAccountResolution> EnsurePrimaryAccountWithOpeningBalanceAsync(
        NCPartyRef owner,
        long openingBalance,
        string auditSource)
    {
        var account = await EnsurePrimaryAccountAsync(owner, auditSource);
        if (!account.IsResolved || openingBalance <= 0)
            return openingBalance < 0
                ? new(owner, account.AccountId, null, "The opening balance cannot be negative.")
                : account;

        var stableKey = owner.ToString();
        var transactionId = NCPersistentIdentity.Transaction("economy", "opening-balance", stableKey);
        var existingTransaction = await _ledger.GetTransactionAsync(transactionId, true);
        if (existingTransaction != null && !IsOpeningBalance(existingTransaction, account.AccountId, openingBalance))
            return new(owner, account.AccountId, null, "The opening transaction identity has incompatible data.");

        NCEconomyMutationResult? mutation = null;
        for (var attempt = 0; existingTransaction == null && attempt < 4; attempt++)
        {
            var attemptAccount = await GetPrimaryAccountAsync(owner, true);
            var issuance = await EnsureIssuanceAccountAsync(auditSource);
            if (attemptAccount == null || !issuance.IsResolved)
            {
                return new(owner, account.AccountId, null,
                    issuance.Error ?? "An opening-balance account is unavailable.");
            }

            // A conflict receipt owns this exact attempt key forever. Current aggregate versions make the next key
            // distinct while the stable transaction aggregate still prevents double issuance.
            var attemptKey = $"{stableKey}/{attemptAccount.AggregateVersion}/{issuance.Account!.AggregateVersion}";
            mutation = await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
                NCPersistentIdentity.Command("economy", "opening-balance-attempt", attemptKey),
                transactionId,
                NCLedgerTransactionKind.OpeningBalance,
                $"opening balance for {owner}",
                [
                    new NCLedgerLegRequest(
                        issuance.AccountId,
                        issuance.Account.AggregateVersion,
                        -openingBalance),
                    new NCLedgerLegRequest(account.AccountId, attemptAccount.AggregateVersion, openingBalance),
                ],
                NCPersistentIdentity.CityService(MonetaryAuthorityId),
                NCPersistentIdentity.Correlation("economy", "opening-balance", stableKey),
                AuditSource: auditSource));
            if (mutation.Status is NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate)
                break;
            if (mutation.Status != NCEconomyMutationStatus.VersionConflict)
                return new(owner, account.AccountId, null, mutation.Error ?? "The opening balance was not committed.");

            existingTransaction = await _ledger.GetTransactionAsync(transactionId, true);
            if (existingTransaction != null &&
                !IsOpeningBalance(existingTransaction, account.AccountId, openingBalance))
            {
                return new(owner, account.AccountId, null, "The opening transaction identity has incompatible data.");
            }
        }

        if (existingTransaction == null &&
            mutation?.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
        {
            return new(owner, account.AccountId, null,
                mutation?.Error ?? "The opening balance exceeded its optimistic retry limit.");
        }

        var current = await GetPrimaryAccountAsync(owner, true);
        return current == null
            ? new(owner, account.AccountId, null, "The opened account could not be reloaded.")
            : new(owner, current.Snapshot.AccountId, current, null);
    }

    private static bool IsOpeningBalance(
        NCLedgerTransactionRecord transaction,
        NCAccountId accountId,
        long openingBalance)
    {
        return transaction.Snapshot.Kind == NCLedgerTransactionKind.OpeningBalance &&
               transaction.Snapshot.Legs.Any(leg => leg.AccountId == accountId && leg.Amount == openingBalance) &&
               transaction.Snapshot.Legs.Sum(leg => (decimal) leg.Amount) == 0;
    }

    public async Task<NCEconomyMutationResult> TransferAsync(NCPartyTransferRequest request)
    {
        if (request.Amount <= 0 || request.Payer == request.Payee)
            return new(NCEconomyMutationStatus.Rejected, [], null, "The party transfer is invalid.");

        var payer = await EnsurePrimaryAccountAsync(request.Payer, request.AuditSource, request.CausationId);
        var payee = await EnsurePrimaryAccountAsync(request.Payee, request.AuditSource, request.CausationId);
        if (!payer.IsResolved || !payee.IsResolved)
        {
            return new(NCEconomyMutationStatus.Rejected, [], null,
                payer.Error ?? payee.Error ?? "A party account is unavailable.");
        }

        return await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
            request.CommandId,
            request.TransactionId,
            request.Kind,
            request.Reference,
            [
                new NCLedgerLegRequest(payer.AccountId, payer.Account!.AggregateVersion, -request.Amount),
                new NCLedgerLegRequest(payee.AccountId, payee.Account!.AggregateVersion, request.Amount),
            ],
            request.Initiator,
            request.CorrelationId,
            request.CausationId,
            request.AuditSource));
    }

    private static readonly JsonSerializerOptions HistoryJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed record AccountLedgerEventProjection(NCTransactionId TransactionId);
}
