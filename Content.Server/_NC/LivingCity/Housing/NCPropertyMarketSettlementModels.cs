// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Housing;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Housing;

public sealed record NCSettlePropertyReservationRequest(
    Guid CommandId,
    NCTransactionId TransactionId,
    NCPropertyReservationId ReservationId,
    long ExpectedReservationVersion,
    NCPartyRef Buyer,
    Guid AccessGrantId,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "property-market-settlement");

public sealed record NCPropertyMarketSettlementResult(
    NCPropertySettlementStatus Status,
    NCPropertyAggregateRecord? Property,
    NCPropertyListingRecord? Listing,
    NCPropertyReservationRecord? Reservation,
    NCEconomyMutationResult? Economy,
    string? Error);
