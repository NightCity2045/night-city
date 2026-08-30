// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Economy.Cash;

public readonly record struct NCCashHandoffId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public static NCCashHandoffId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString("D");
}

public enum NCCashHandoffDirection : byte
{
    Deposit,
    Withdrawal,
}

public enum NCCashHandoffStatus : byte
{
    PendingPhysical,
    Completed,
    Reversed,
}

/// <summary>
/// Durable bridge between the canonical electronic ledger and volatile round cash. LedgerDelta is signed from the
/// party's perspective: deposits are positive, withdrawals are negative.
/// </summary>
public sealed record NCCashHandoffSnapshot(
    NCCashHandoffId HandoffId,
    NCPartyRef Party,
    NCCashHandoffDirection Direction,
    long LedgerDelta,
    long PhysicalAmount,
    long TaxAmount,
    NCTransactionId TransactionId,
    NCTransactionId? ReversalTransactionId,
    NCCashHandoffStatus Status,
    NCPartyRef Initiator,
    string Reference,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    NCActiveWorldTime? ResolvedAt,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCCashHandoffRecord(
    NCCashHandoffSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCStartCashHandoffRequest(
    Guid CommandId,
    NCCashHandoffId HandoffId,
    NCTransactionId TransactionId,
    NCPartyRef Party,
    NCCashHandoffDirection Direction,
    long LedgerDelta,
    long PhysicalAmount,
    long TaxAmount,
    NCPartyRef Initiator,
    string Reference,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "cash-handoff");

public enum NCCashHandoffMutationStatus : byte
{
    Applied,
    Duplicate,
    NotFound,
    VersionConflict,
    InsufficientFunds,
    ClockUnavailable,
    OutcomeUnknown,
    Rejected,
    StorageFailure,
}

public sealed record NCCashHandoffMutationResult(
    NCCashHandoffMutationStatus Status,
    NCCashHandoffRecord? Handoff,
    string? Error);
