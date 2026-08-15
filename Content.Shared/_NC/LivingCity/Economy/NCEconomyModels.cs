// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Economy;

[Serializable, NetSerializable]
public enum NCAccountKind : byte
{
    Wallet,
    Bank,
    Organization,
    Business,
    Escrow,
    Estate,
    SystemClearing,
}

[Serializable, NetSerializable]
public enum NCAccountStatus : byte
{
    Active,
    Frozen,
    Closed,
}

[Serializable, NetSerializable]
public enum NCLedgerTransactionKind : byte
{
    OpeningBalance,
    Transfer,
    Purchase,
    Sale,
    Salary,
    Rent,
    Fee,
    Tax,
    Refund,
    Adjustment,
}

/// <summary>
/// Authoritative balance projection for one party-owned account. The ledger is changed only by balanced transactions.
/// </summary>
public sealed record NCAccountSnapshot(
    NCAccountId AccountId,
    NCPartyRef Owner,
    NCAccountKind Kind,
    string CurrencyId,
    long Balance,
    bool AllowsNegativeBalance,
    NCAccountStatus Status,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);

/// <summary>
/// One signed double-entry leg. Positive values credit an account; negative values debit it.
/// </summary>
public sealed record NCLedgerLeg(NCAccountId AccountId, long Amount, long BalanceAfter);

/// <summary>
/// Immutable transaction aggregate. Its legs always sum to zero and retain no runtime identifiers.
/// </summary>
public sealed record NCLedgerTransactionSnapshot(
    NCTransactionId TransactionId,
    NCLedgerTransactionKind Kind,
    string CurrencyId,
    string Reference,
    NCLedgerLeg[] Legs,
    NCPartyRef Initiator,
    NCActiveWorldTime PostedAt,
    int DataVersion,
    Guid LastAppliedEventId);

