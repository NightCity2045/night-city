// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Fixers;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Fixers;

public sealed record NCFixerEscrowResult(NCFixerMutationStatus Status, NCFixerDealRecord? Deal,
    NCEconomyMutationResult? Economy, string? Error);

/// <summary>
/// Funds and releases deal escrow through real ledger accounts. The deal status and money movement are one database
/// transaction, so neither a player disconnect nor an NPC dematerialization can lose the obligation.
/// </summary>
public sealed partial class NCFixerEscrowSystem : EntitySystem
{
    private const string ServiceName = "Night City Brokerage Escrow";

    [Dependency] private NCPartyDirectorySystem _directory = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;
    [Dependency] private NCFixerRegistrySystem _fixers = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;

    public NCPartyRef EscrowParty => NCPersistentIdentity.CityService("night-city-brokerage-escrow");

    public async Task<NCFixerEscrowResult> FundAsync(NCFixerDealId dealId, long expectedVersion,
        NCPartyRef buyer)
    {
        if (!dealId.IsValid || expectedVersion <= 0 || !buyer.IsValid ||
            !await EnsureServiceAsync("fixer-escrow-funding") ||
            !_clock.TryGetCurrentTime(out _, out var now))
            return Reject("The escrow funding request is invalid or unavailable.");
        var deal = await _fixers.GetDealAsync(dealId, true);
        if (deal == null)
            return new(NCFixerMutationStatus.NotFound, null, null, "The deal does not exist.");
        if (deal.AggregateVersion != expectedVersion)
            return new(NCFixerMutationStatus.VersionConflict, deal, null, "The deal version changed.");
        if (deal.Snapshot.Status != NCFixerDealStatus.Reserved || deal.Snapshot.Buyer != buyer ||
            deal.Snapshot.ReservationExpiresAt is not { } expiry || expiry <= now)
            return new(NCFixerMutationStatus.NotAuthorized, deal, null,
                "The buyer does not own an active reservation.");
        if (deal.Snapshot.Plan.EscrowAmount == 0)
        {
            var stable = $"{dealId}.{expectedVersion}";
            return FromFixer(await _fixers.ChangeDealAsync(new NCChangeFixerDealRequest(
                NCPersistentIdentity.Command("fixer", "fund-zero-escrow", stable), dealId, expectedVersion,
                NCFixerDealStatus.EscrowFunded, buyer,
                NCPersistentIdentity.Correlation("fixer", "fund-zero-escrow", stable),
                deal.Snapshot.LastAppliedEventId, "fixer-zero-escrow-funding")));
        }
        var escrowId = NCPersistentIdentity.SystemAccount($"fixer-deal-escrow.{dealId}");
        var escrow = await EnsureEscrowAccountAsync(escrowId, deal.Snapshot.LastAppliedEventId);
        var payer = await _finance.EnsurePrimaryAccountAsync(buyer, "fixer-escrow-funding",
            deal.Snapshot.LastAppliedEventId);
        if (escrow == null || !payer.IsResolved || escrow.Snapshot.Balance != 0)
            return Reject("The escrow or buyer account is unavailable or inconsistent.", deal);
        var transactionId = NCPersistentIdentity.Transaction("fixer", "fund-escrow",
            $"{dealId}.{expectedVersion}");
        var eventId = Guid.NewGuid();
        var proposed = deal.Snapshot with
        {
            Status = NCFixerDealStatus.EscrowFunded,
            EscrowAccountId = escrowId,
            FundingTransactionId = transactionId,
            UpdatedAt = now,
            DataVersion = checked(deal.Snapshot.DataVersion + 1),
            LastAppliedEventId = eventId,
        };
        if (!_fixers.TryPrepareDealParticipant(deal, proposed, "living-city.fixer-deal.escrow-funded",
                "fixer-escrow-funding", out var participant))
            return Reject("The deal escrow participant is invalid.", deal);
        var commandId = NCPersistentIdentity.Command("fixer", "fund-escrow", $"{dealId}.{expectedVersion}");
        var economy = await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(commandId,
            transactionId, NCLedgerTransactionKind.Transfer, $"fixer deal escrow funding {dealId}",
            [
                new NCLedgerLegRequest(payer.AccountId, payer.Account!.AggregateVersion,
                    -deal.Snapshot.Plan.EscrowAmount),
                new NCLedgerLegRequest(escrowId, escrow.AggregateVersion, deal.Snapshot.Plan.EscrowAmount),
            ], buyer, NCPersistentIdentity.Correlation("fixer", "fund-escrow", $"{dealId}.{expectedVersion}"),
            deal.Snapshot.LastAppliedEventId, "fixer-escrow-funding", [participant!]));
        if (economy.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
            return new(Map(economy.Status), deal, economy,
                economy.Error ?? "The escrow funding transaction failed.");
        var loaded = await _fixers.GetDealAsync(dealId, true);
        return loaded == null ? Reject("The funded deal could not be reloaded.") : new(
            economy.Status == NCEconomyMutationStatus.Applied ? NCFixerMutationStatus.Applied :
                NCFixerMutationStatus.Duplicate, loaded, economy, null);
    }

    public async Task<NCFixerEscrowResult> SettleAsync(NCFixerDealId dealId, long expectedVersion,
        NCPartyRef actor)
    {
        if (!dealId.IsValid || expectedVersion <= 0 || !actor.IsValid ||
            !_clock.TryGetCurrentTime(out _, out var now))
            return Reject("The escrow settlement request is invalid.");
        var deal = await _fixers.GetDealAsync(dealId, true);
        if (deal == null)
            return new(NCFixerMutationStatus.NotFound, null, null, "The deal does not exist.");
        if (deal.AggregateVersion != expectedVersion)
            return new(NCFixerMutationStatus.VersionConflict, deal, null, "The deal version changed.");
        if (actor != deal.Snapshot.Fixer || deal.Snapshot.Status is not (NCFixerDealStatus.AwaitingHandoff or
                NCFixerDealStatus.Arbitrated) || deal.Snapshot.Plan.EscrowAmount > 0 &&
            !deal.Snapshot.EscrowAccountId.IsValid)
            return new(NCFixerMutationStatus.NotAuthorized, deal, null,
                "Only the broker can settle a delivered or arbitrated deal.");
        if (deal.Snapshot.Plan.EscrowAmount == 0)
        {
            var zeroStable = $"{dealId}.{expectedVersion}";
            return FromFixer(await _fixers.ChangeDealAsync(new NCChangeFixerDealRequest(
                NCPersistentIdentity.Command("fixer", "settle-zero-escrow", zeroStable), dealId, expectedVersion,
                NCFixerDealStatus.Fulfilled, actor,
                NCPersistentIdentity.Correlation("fixer", "settle-zero-escrow", zeroStable),
                deal.Snapshot.LastAppliedEventId, "fixer-zero-escrow-settlement")));
        }
        var escrow = await _ledger.GetAccountAsync(deal.Snapshot.EscrowAccountId, true);
        var buyer = await _finance.EnsurePrimaryAccountAsync(deal.Snapshot.Buyer, "fixer-escrow-settlement");
        var seller = await _finance.EnsurePrimaryAccountAsync(deal.Snapshot.Seller, "fixer-escrow-settlement");
        var fixer = await _finance.EnsurePrimaryAccountAsync(deal.Snapshot.Fixer, "fixer-escrow-settlement");
        if (escrow == null || escrow.Snapshot.Balance != deal.Snapshot.Plan.EscrowAmount ||
            !buyer.IsResolved || !seller.IsResolved || !fixer.IsResolved)
            return Reject("The escrow settlement accounts are inconsistent.", deal);
        var distribution = Distribution(deal.Snapshot);
        var legs = new List<NCLedgerLegRequest>
        {
            new(escrow.Snapshot.AccountId, escrow.AggregateVersion, -escrow.Snapshot.Balance),
        };
        if (distribution.Buyer > 0)
            legs.Add(new NCLedgerLegRequest(buyer.AccountId, buyer.Account!.AggregateVersion, distribution.Buyer));
        if (distribution.Seller > 0)
            legs.Add(new NCLedgerLegRequest(seller.AccountId, seller.Account!.AggregateVersion, distribution.Seller));
        if (distribution.Fixer > 0)
            legs.Add(new NCLedgerLegRequest(fixer.AccountId, fixer.Account!.AggregateVersion, distribution.Fixer));
        var transactionId = NCPersistentIdentity.Transaction("fixer", "settle-escrow",
            $"{dealId}.{expectedVersion}");
        var eventId = Guid.NewGuid();
        var proposed = deal.Snapshot with
        {
            Status = NCFixerDealStatus.Fulfilled,
            SettlementTransactionId = transactionId,
            UpdatedAt = now,
            DataVersion = checked(deal.Snapshot.DataVersion + 1),
            LastAppliedEventId = eventId,
        };
        if (!_fixers.TryPrepareDealParticipant(deal, proposed, "living-city.fixer-deal.fulfilled",
                "fixer-escrow-settlement", out var participant))
            return Reject("The deal settlement participant is invalid.", deal);
        var stable = $"{dealId}.{expectedVersion}";
        var economy = await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
            NCPersistentIdentity.Command("fixer", "settle-escrow", stable), transactionId,
            NCLedgerTransactionKind.Purchase, $"fixer deal settlement {dealId}", legs.ToArray(), actor,
            NCPersistentIdentity.Correlation("fixer", "settle-escrow", stable),
            deal.Snapshot.LastAppliedEventId, "fixer-escrow-settlement", [participant!]));
        if (economy.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
            return new(Map(economy.Status), deal, economy,
                economy.Error ?? "The deal escrow settlement failed.");
        var loaded = await _fixers.GetDealAsync(dealId, true);
        var clearedEscrow = await _ledger.GetAccountAsync(deal.Snapshot.EscrowAccountId, true);
        if (clearedEscrow?.Snapshot.Balance == 0 && clearedEscrow.Snapshot.Status == NCAccountStatus.Active)
        {
            await _ledger.SetAccountStatusAsync(new NCSetAccountStatusRequest(
                NCPersistentIdentity.Command("fixer", "close-escrow", stable), clearedEscrow.Snapshot.AccountId,
                clearedEscrow.AggregateVersion, NCAccountStatus.Closed, EscrowParty,
                NCPersistentIdentity.Correlation("fixer", "close-escrow", stable),
                deal.Snapshot.LastAppliedEventId, "fixer-escrow-settlement"));
        }
        return loaded == null ? Reject("The settled deal could not be reloaded.") : new(
            economy.Status == NCEconomyMutationStatus.Applied ? NCFixerMutationStatus.Applied :
                NCFixerMutationStatus.Duplicate, loaded, economy, null);
    }

    private async Task<bool> EnsureServiceAsync(string auditSource)
    {
        var registration = await _directory.RegisterAsync(new NCPartyDirectoryRegisterRequest(
            NCPersistentIdentity.Command("fixer", "register-escrow-service", "default"), EscrowParty, ServiceName,
            NCPersistentIdentity.Correlation("fixer", "register-escrow-service", "default"),
            AuditSource: auditSource));
        return registration.Status is NCPartyDirectoryMutationStatus.Applied or NCPartyDirectoryMutationStatus.Duplicate ||
            await _directory.GetAsync(EscrowParty, true) != null;
    }

    private async Task<NCAccountRecord?> EnsureEscrowAccountAsync(NCAccountId accountId, Guid causationId)
    {
        var current = await _ledger.GetAccountAsync(accountId, true);
        if (current != null)
            return current.Snapshot.Owner == EscrowParty && current.Snapshot.Kind == NCAccountKind.Escrow &&
                current.Snapshot.Status == NCAccountStatus.Active ? current : null;
        var result = await _ledger.CreateAccountAsync(new NCCreateAccountRequest(
            NCPersistentIdentity.Command("fixer", "create-escrow", accountId.ToString()), accountId, EscrowParty,
            NCAccountKind.Escrow, _finance.Configuration.CurrencyId, false,
            NCPersistentIdentity.Correlation("fixer", "create-escrow", accountId.ToString()), causationId,
            "fixer-escrow-account"));
        return result.Status is NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate
            ? await _ledger.GetAccountAsync(accountId, true)
            : null;
    }

    private static (long Buyer, long Seller, long Fixer) Distribution(NCFixerDealSnapshot deal)
    {
        var fee = deal.Plan.EscrowAmount - deal.QuotedAmount;
        return deal.Dispute?.Outcome switch
        {
            NCFixerArbitrationOutcome.BuyerFavored or NCFixerArbitrationOutcome.DealVoided =>
                (deal.Plan.EscrowAmount, 0, 0),
            NCFixerArbitrationOutcome.Split =>
                (deal.QuotedAmount / 2, deal.QuotedAmount - deal.QuotedAmount / 2, fee),
            _ => (0, deal.QuotedAmount, fee),
        };
    }

    private static NCFixerMutationStatus Map(NCEconomyMutationStatus value) => value switch
    {
        NCEconomyMutationStatus.VersionConflict => NCFixerMutationStatus.VersionConflict,
        NCEconomyMutationStatus.InsufficientFunds => NCFixerMutationStatus.Rejected,
        _ => NCFixerMutationStatus.StorageFailure,
    };
    private static NCFixerEscrowResult FromFixer(NCFixerMutationResult result) =>
        new(result.Status, result.Deal, null, result.Error);
    private static NCFixerEscrowResult Reject(string error, NCFixerDealRecord? deal = null) =>
        new(NCFixerMutationStatus.Rejected, deal, null, error);
}
