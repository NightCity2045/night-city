// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Housing;
using Content.Shared._NC.Persistence.Identity;
using Content.Server._NC.LivingCity.Economy;

namespace Content.Server._NC.LivingCity.Housing;

public enum NCPropertySettlementStatus : byte
{
    Applied,
    Duplicate,
    NotFound,
    NotAvailable,
    NotAuthorized,
    InsufficientFunds,
    VersionConflict,
    Rejected,
    StorageFailure,
}

public sealed record NCPurchasePropertySettlementRequest(
    Guid CommandId,
    NCTransactionId TransactionId,
    NCApartmentId PropertyId,
    long ExpectedPropertyVersion,
    NCPartyRef Buyer,
    long Price,
    NCPropertyPermission Permissions,
    Guid AccessGrantId,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "property-purchase");

public sealed record NCRentPropertySettlementRequest(
    Guid CommandId,
    NCTransactionId TransactionId,
    NCApartmentId PropertyId,
    long ExpectedPropertyVersion,
    NCPartyRef Tenant,
    long InitialCharge,
    string RentPolicyId,
    long BillingIntervalTicks,
    string TemplateId,
    int TemplateRevision,
    NCPropertyPermission Permissions,
    Guid AccessGrantId,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "property-rent");

public sealed record NCRenewPropertyRentSettlementRequest(
    Guid CommandId,
    NCTransactionId TransactionId,
    NCApartmentId PropertyId,
    long ExpectedPropertyVersion,
    NCPartyRef Tenant,
    long Charge,
    long BillingIntervalTicks,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "property-rent-renewal");

public sealed record NCReleasePropertyLeaseSettlementRequest(
    Guid CommandId,
    NCTransactionId TransactionId,
    NCApartmentId PropertyId,
    long ExpectedPropertyVersion,
    NCPartyRef Tenant,
    NCPartyRef Initiator,
    NCPropertyReleaseReason Reason,
    int DepositRefundBasisPoints,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "property-lease-release");

public sealed record NCRenovatePropertySettlementRequest(
    Guid CommandId,
    NCTransactionId TransactionId,
    NCApartmentId PropertyId,
    long ExpectedPropertyVersion,
    NCPartyRef Actor,
    long Price,
    string TemplateId,
    int TemplateRevision,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "property-renovation");

public sealed record NCRollbackPropertyRenovationRequest(
    Guid CommandId,
    NCTransactionId RefundTransactionId,
    NCApartmentId PropertyId,
    long ExpectedPropertyVersion,
    NCPartyRef Actor,
    long RefundAmount,
    string? PreviousTemplateId,
    int? PreviousTemplateRevision,
    string PreviousInteriorState,
    int PreviousRenovationRevision,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "property-renovation-rollback");

public sealed record NCPropertySettlementResult(
    NCPropertySettlementStatus Status,
    NCPropertyAggregateRecord? Property,
    NCEconomyMutationResult? Economy,
    string? Error);
