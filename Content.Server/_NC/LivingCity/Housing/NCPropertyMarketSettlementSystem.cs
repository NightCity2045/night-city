// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Housing;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Housing;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Housing;

/// <summary>
/// Settles a reserved sale or lease as one ledger transaction with property, listing, quote and reservation participant
/// writes. No client-supplied price crosses this boundary.
/// </summary>
public sealed partial class NCPropertyMarketSettlementSystem : EntitySystem
{
    private const int GateStripeCount = 64;

    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPropertyRegistrySystem _properties = default!;
    [Dependency] private NCPropertyMarketRegistrySystem _market = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;
    [Dependency] private NCHousingServiceSystem _housingService = default!;

    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, GateStripeCount)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<NCPropertyMarketSettlementResult> SettleAsync(
        NCSettlePropertyReservationRequest request)
    {
        if (!Valid(request) || !_clock.TryGetCurrentTime(out _, out var now))
            return Reject(_clock.IsReady ? NCPropertySettlementStatus.Rejected :
                NCPropertySettlementStatus.StorageFailure, "The property settlement request is invalid.");
        var gate = _gates[(request.ReservationId.Value.GetHashCode() & int.MaxValue) % _gates.Length];
        await gate.WaitAsync();
        try
        {
            var reservation = await _market.GetReservationAsync(request.ReservationId, true);
            if (reservation == null)
                return Reject(NCPropertySettlementStatus.NotFound, "The property reservation does not exist.");
            if (reservation.AggregateVersion != request.ExpectedReservationVersion)
                return Reject(NCPropertySettlementStatus.VersionConflict, "The reservation version changed.");
            if (reservation.Snapshot.Status == NCPropertyReservationStatus.Settled &&
                reservation.Snapshot.SettlementTransactionId == request.TransactionId)
                return await ReplayResultAsync(reservation, request.TransactionId);
            if (reservation.Snapshot.Status != NCPropertyReservationStatus.Active ||
                reservation.Snapshot.ExpiresAt <= now || reservation.Snapshot.Buyer != request.Buyer)
                return Reject(NCPropertySettlementStatus.NotAvailable,
                    "The property reservation is expired, inactive, or belongs to another buyer.");
            var quote = await _market.GetQuoteAsync(reservation.Snapshot.QuoteId, true);
            var listing = await _market.GetListingAsync(reservation.Snapshot.ListingId, true);
            var property = await _properties.GetAsync(reservation.Snapshot.PropertyId, true);
            if (quote == null || listing == null || property == null)
                return Reject(NCPropertySettlementStatus.NotFound,
                    "A property settlement aggregate is missing.", property, listing, reservation);
            if (quote.Snapshot.Status != NCPropertyQuoteStatus.Reserved ||
                listing.Snapshot.Status != NCPropertyListingStatus.Reserved ||
                quote.Snapshot.PropertyVersion != property.AggregateVersion ||
                property.Snapshot.Owner != listing.Snapshot.Seller ||
                property.Snapshot.LegalState != NCPropertyLegalState.Clear)
                return Reject(NCPropertySettlementStatus.VersionConflict,
                    "The listing or property changed after reservation.", property, listing, reservation);
            if (listing.Snapshot.OfferKind == NCPropertyOfferKind.Rent &&
                property.Snapshot.Status != NCPropertyStatus.Available)
                return Reject(NCPropertySettlementStatus.NotAvailable,
                    "The rental property is no longer vacant.", property, listing, reservation);

            var payer = await _finance.EnsurePrimaryAccountAsync(request.Buyer, request.AuditSource,
                request.CausationId);
            var payee = await _finance.EnsurePrimaryAccountAsync(listing.Snapshot.Seller, request.AuditSource,
                request.CausationId);
            if (!payer.IsResolved || !payee.IsResolved)
                return Reject(NCPropertySettlementStatus.Rejected,
                    payer.Error ?? payee.Error ?? "A property settlement account is unavailable.",
                    property, listing, reservation);
            NCAccountRecord? depositAccount = null;
            if (quote.Snapshot.SecurityDeposit > 0)
            {
                depositAccount = await EnsureDepositAccountAsync(property.Snapshot.PropertyId,
                    payer.Account!.Snapshot.CurrencyId, request);
                if (depositAccount == null)
                    return Reject(NCPropertySettlementStatus.StorageFailure,
                        "The security-deposit escrow account is unavailable.", property, listing, reservation);
            }

            var accessRevision = checked(property.Snapshot.AccessRevision + 1);
            var proposed = quote.Snapshot.OfferKind == NCPropertyOfferKind.Sale
                ? property.Snapshot with
                {
                    Owner = request.Buyer,
                    Occupants =
                    [
                        new NCPropertyOccupantSnapshot(request.Buyer, NCPropertyOccupancyKind.Ownership, now, null),
                    ],
                    AccessGrants =
                    [
                        new NCPropertyAccessGrantSnapshot(request.AccessGrantId, request.Buyer,
                            NCPropertyPermission.All, accessRevision, now, null),
                    ],
                    Status = NCPropertyStatus.OwnerOccupied,
                    PurchasePrice = quote.Snapshot.Price,
                    AcquiredAt = now,
                    AccessRevision = accessRevision,
                    RentPolicyId = string.Empty,
                    NextRentDueAt = null,
                    SecurityDepositAmount = 0,
                    SecurityDepositAccountId = default,
                    DelinquentSince = null,
                    EvictionAt = null,
                }
                : property.Snapshot with
                {
                    Occupants = property.Snapshot.Occupants
                        .Where(occupant => occupant.Kind != NCPropertyOccupancyKind.Tenancy)
                        .Append(new NCPropertyOccupantSnapshot(request.Buyer, NCPropertyOccupancyKind.Tenancy,
                            now, null)).ToArray(),
                    AccessGrants =
                    [
                        new NCPropertyAccessGrantSnapshot(request.AccessGrantId, request.Buyer,
                            quote.Snapshot.OccupantPermissions, accessRevision, now, null),
                    ],
                    Status = NCPropertyStatus.Rented,
                    AcquiredAt = now,
                    AccessRevision = accessRevision,
                    RentPolicyId = quote.Snapshot.RentPolicyId,
                    CurrentTemplateId = quote.Snapshot.TemplateId,
                    CurrentTemplateRevision = quote.Snapshot.TemplateRevision,
                    InteriorState = "ready",
                    NextRentDueAt = now.Advance(TimeSpan.FromTicks(quote.Snapshot.BillingIntervalTicks)),
                    SecurityDepositAmount = quote.Snapshot.SecurityDeposit,
                    SecurityDepositAccountId = depositAccount?.Snapshot.AccountId ?? default,
                    DelinquentSince = null,
                    EvictionAt = null,
                };
            if (!_properties.TryPrepareSettlementWrite(property, proposed, request.TransactionId,
                    quote.Snapshot.OfferKind == NCPropertyOfferKind.Sale
                        ? "living-city.property.purchased-from-listing"
                        : "living-city.property.rented-from-listing",
                    request.AuditSource, out var propertyWrite, out _, out var propertyError) ||
                !_market.TryPrepareSettlementWrites(listing, quote, reservation, request.TransactionId, now,
                    request.AuditSource, out var marketWrites))
                return Reject(NCPropertySettlementStatus.Rejected,
                    propertyError ?? "The property market participants are invalid.", property, listing, reservation);

            var legs = new List<NCLedgerLegRequest>();
            var total = checked(quote.Snapshot.Price + quote.Snapshot.SecurityDeposit);
            if (total > 0)
                legs.Add(new NCLedgerLegRequest(payer.AccountId, payer.Account!.AggregateVersion, -total));
            if (quote.Snapshot.Price > 0)
                legs.Add(new NCLedgerLegRequest(payee.AccountId, payee.Account!.AggregateVersion,
                    quote.Snapshot.Price));
            if (quote.Snapshot.SecurityDeposit > 0)
                legs.Add(new NCLedgerLegRequest(depositAccount!.Snapshot.AccountId,
                    depositAccount.AggregateVersion, quote.Snapshot.SecurityDeposit));
            if (legs.Count == 0)
                return Reject(NCPropertySettlementStatus.Rejected,
                    "Zero-value property offers must use an explicitly subsidized settlement path.",
                    property, listing, reservation);
            var participants = new List<NCAtomicLedgerParticipantWrite>(marketWrites.Count + 1) { propertyWrite! };
            participants.AddRange(marketWrites);
            var economy = await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
                request.CommandId, request.TransactionId,
                quote.Snapshot.OfferKind == NCPropertyOfferKind.Sale
                    ? NCLedgerTransactionKind.Purchase
                    : NCLedgerTransactionKind.Rent,
                $"property reservation {request.ReservationId}", legs.ToArray(), request.Buyer,
                request.CorrelationId, request.CausationId, request.AuditSource, participants));
            if (economy.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
                return Reject(Map(economy.Status), economy.Error ?? "The property settlement was rejected.",
                    property, listing, reservation, economy);
            var loadedProperty = await _properties.GetAsync(property.Snapshot.PropertyId, true);
            var loadedListing = await _market.GetListingAsync(listing.Snapshot.ListingId, true);
            var loadedReservation = await _market.GetReservationAsync(reservation.Snapshot.ReservationId, true);
            return loadedProperty == null || loadedListing == null || loadedReservation == null
                ? Reject(NCPropertySettlementStatus.StorageFailure,
                    "The committed property settlement could not be reloaded.", loadedProperty, loadedListing,
                    loadedReservation, economy)
                : new(economy.Status == NCEconomyMutationStatus.Applied
                        ? NCPropertySettlementStatus.Applied
                        : NCPropertySettlementStatus.Duplicate,
                    loadedProperty, loadedListing, loadedReservation, economy, null);
        }
        catch (Exception exception)
        {
            Log.Error($"Property market settlement failed for {request.ReservationId}: {exception}");
            return Reject(NCPropertySettlementStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<NCAccountRecord?> EnsureDepositAccountAsync(
        Content.Shared._NC.City.Zones.NCApartmentId propertyId,
        string currencyId,
        NCSettlePropertyReservationRequest request)
    {
        if (!await _housingService.EnsureReadyAsync(request.AuditSource))
            return null;
        var id = NCPersistentIdentity.SystemAccount($"property-deposit.{propertyId}");
        var existing = await _ledger.GetAccountAsync(id, true);
        if (existing != null)
            return existing.Snapshot.Owner == _housingService.Party && existing.Snapshot.Kind == NCAccountKind.Escrow &&
                   existing.Snapshot.CurrencyId == currencyId ? existing : null;
        var created = await _ledger.CreateAccountAsync(new NCCreateAccountRequest(
            NCPersistentIdentity.Command("housing", "deposit-account", propertyId.ToString()), id,
            _housingService.Party, NCAccountKind.Escrow, currencyId, false,
            NCPersistentIdentity.Correlation("housing", "deposit-account", propertyId.ToString()),
            request.CausationId, request.AuditSource));
        return created.Status is NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate
            ? await _ledger.GetAccountAsync(id, true)
            : null;
    }

    private async Task<NCPropertyMarketSettlementResult> ReplayResultAsync(
        NCPropertyReservationRecord reservation,
        NCTransactionId transactionId)
    {
        var property = await _properties.GetAsync(reservation.Snapshot.PropertyId, true);
        var listing = await _market.GetListingAsync(reservation.Snapshot.ListingId, true);
        var transaction = await _ledger.GetTransactionAsync(transactionId, true);
        return property == null || listing == null || transaction == null
            ? Reject(NCPropertySettlementStatus.StorageFailure,
                "The completed property settlement cannot be reloaded.", property, listing, reservation)
            : new(NCPropertySettlementStatus.Duplicate, property, listing, reservation,
                new NCEconomyMutationResult(NCEconomyMutationStatus.Duplicate, [], transaction, null), null);
    }

    private static bool Valid(NCSettlePropertyReservationRequest value) => value.CommandId != Guid.Empty &&
        value.TransactionId.IsValid && value.ReservationId.IsValid && value.ExpectedReservationVersion > 0 &&
        value.Buyer.IsValid && value.AccessGrantId != Guid.Empty && value.CorrelationId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(value.AuditSource) && value.AuditSource.Length <= 128;
    private static NCPropertySettlementStatus Map(NCEconomyMutationStatus status) => status switch
    {
        NCEconomyMutationStatus.InsufficientFunds => NCPropertySettlementStatus.InsufficientFunds,
        NCEconomyMutationStatus.VersionConflict => NCPropertySettlementStatus.VersionConflict,
        NCEconomyMutationStatus.NotFound => NCPropertySettlementStatus.NotFound,
        NCEconomyMutationStatus.StorageFailure => NCPropertySettlementStatus.StorageFailure,
        _ => NCPropertySettlementStatus.Rejected,
    };
    private static NCPropertyMarketSettlementResult Reject(
        NCPropertySettlementStatus status,
        string error,
        NCPropertyAggregateRecord? property = null,
        NCPropertyListingRecord? listing = null,
        NCPropertyReservationRecord? reservation = null,
        NCEconomyMutationResult? economy = null) => new(status, property, listing, reservation, economy, error);
}
