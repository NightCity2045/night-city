// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Economy;

public sealed record NCPartyAccountResolution(
    NCPartyRef Owner,
    NCAccountId AccountId,
    NCAccountRecord? Account,
    string? Error)
{
    public bool IsResolved => Account != null && Error == null;
}

public sealed record NCPartyTransferRequest(
    Guid CommandId,
    NCTransactionId TransactionId,
    NCPartyRef Payer,
    NCPartyRef Payee,
    long Amount,
    NCLedgerTransactionKind Kind,
    string Reference,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

/// <summary>
/// Creates or destroys electronic currency against the canonical monetary-authority clearing account. The clearing
/// account is the only account allowed to go negative, keeping every opening balance and cash handoff double-entry.
/// </summary>
public sealed record NCPartyBalanceAdjustmentRequest(
    Guid CommandId,
    NCTransactionId TransactionId,
    NCPartyRef Party,
    long Amount,
    NCLedgerTransactionKind Kind,
    string Reference,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

/// <summary>
/// One immutable ledger transaction as seen through a party account, with its wall-clock audit timestamp.
/// Gameplay durations continue to use the transaction's active-world timestamp.
/// </summary>
public sealed record NCPartyTransactionHistoryEntry(
    NCLedgerTransactionRecord Transaction,
    DateTime RecordedAt,
    long AccountAggregateVersion);

public sealed record NCPartyTransactionHistoryPage(
    NCPartyRef Owner,
    NCAccountId AccountId,
    IReadOnlyList<NCPartyTransactionHistoryEntry> Entries,
    long? NextBeforeAggregateVersion,
    string? Error)
{
    public bool IsValid => Error == null;
}
