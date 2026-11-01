// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.GameObjects;

namespace Content.Server._NC.LivingCity.Economy;

public enum NCEconomyMutationStatus : byte
{
    Applied,
    Duplicate,
    NotFound,
    VersionConflict,
    InsufficientFunds,
    ClockUnavailable,
    Rejected,
    StorageFailure,
}

public sealed record NCAccountRecord(
    NCAccountSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCLedgerTransactionRecord(
    NCLedgerTransactionSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCCreateAccountRequest(
    Guid CommandId,
    NCAccountId AccountId,
    NCPartyRef Owner,
    NCAccountKind Kind,
    string CurrencyId,
    bool AllowsNegativeBalance,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCSetAccountStatusRequest(
    Guid CommandId,
    NCAccountId AccountId,
    long ExpectedVersion,
    NCAccountStatus Status,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCLedgerLegRequest(NCAccountId AccountId, long ExpectedVersion, long Amount);

/// <summary>
/// Already validated non-ledger aggregate mutation that must commit in the same database transaction as money.
/// The owning domain remains responsible for all gameplay rules and for reloading its snapshot on command replay.
/// </summary>
public sealed record NCAtomicLedgerParticipantWrite(
    Guid EventId,
    string EventType,
    string AggregateType,
    string AggregateId,
    long ExpectedAggregateVersion,
    int SnapshotFormatVersion,
    string SnapshotPayload,
    string EventPayload);

/// <summary>
/// Reliable message attached to one participant event in an atomic ledger settlement.
/// </summary>
public sealed record NCAtomicLedgerOutboxWrite(
    Guid MessageId,
    Guid EventId,
    string MessageType,
    string Payload);

public sealed record NCPostLedgerTransactionRequest(
    Guid CommandId,
    NCTransactionId TransactionId,
    NCLedgerTransactionKind Kind,
    string Reference,
    NCLedgerLegRequest[] Legs,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system",
    IReadOnlyList<NCAtomicLedgerParticipantWrite>? ParticipantWrites = null,
    IReadOnlyList<NCAtomicLedgerOutboxWrite>? ParticipantOutboxMessages = null,
    Content.Server.Database.NCMetaAuthorityConstraints? Authority = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? ParticipantResultPayload = null);

public sealed record NCEconomyMutationResult(
    NCEconomyMutationStatus Status,
    IReadOnlyList<NCAccountRecord> Accounts,
    NCLedgerTransactionRecord? Transaction,
    string? Error)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ParticipantResultPayload { get; init; }
}

public sealed class NCAccountPersistedEvent(
    NCAccountId accountId,
    NCPartyRef owner,
    long aggregateVersion,
    long balance,
    NCAccountStatus status) : EntityEventArgs
{
    public NCAccountId AccountId { get; } = accountId;
    public NCPartyRef Owner { get; } = owner;
    public long AggregateVersion { get; } = aggregateVersion;
    public long Balance { get; } = balance;
    public NCAccountStatus Status { get; } = status;
}
