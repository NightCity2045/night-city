// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Content.Server.Database;

public enum NCBankAccountType : byte
{
    Personal,
    Organization,
    Business,
    Service,
    System,
}

public enum NCBankAccountStatus : byte
{
    Active,
    Frozen,
    Closed,
}

public enum NCBankTransactionType : byte
{
    Deposit,
    Withdrawal,
    Transfer,
    Payroll,
    Tax,
    AdministrativeAdjustment,
    AccountClosure,
}

/// <summary>
/// Durable account state. Gameplay components may cache it but never become its source of truth.
/// </summary>
[Table("nc_bank_account")]
public sealed class NCBankAccount
{
    [Key]
    public Guid BankAccountId { get; set; }

    [MaxLength(32)]
    public string AccountNumber { get; set; } = string.Empty;

    public NCBankAccountType AccountType { get; set; }
    public int? OwnerProfileId { get; set; }

    [MaxLength(64)]
    public string CurrencyPrototypeId { get; set; } = string.Empty;

    public long Balance { get; set; }
    public NCBankAccountStatus Status { get; set; }

    /// <summary>
    /// Salted credential material for optional ATM authentication. Plain-text PINs are forbidden.
    /// </summary>
    public byte[]? CredentialHash { get; set; }
    public byte[]? CredentialSalt { get; set; }

    /// <summary>
    /// Optimistic concurrency token incremented by every committed balance mutation.
    /// </summary>
    public long Version { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}

/// <summary>
/// Immutable double-entry compatible transaction record.
/// </summary>
[Table("nc_bank_transaction")]
public sealed class NCBankTransaction
{
    [Key]
    public Guid BankTransactionId { get; set; }

    public Guid RequestId { get; set; }
    public Guid? DebitAccountId { get; set; }
    public Guid? CreditAccountId { get; set; }
    public long Amount { get; set; }

    [MaxLength(64)]
    public string CurrencyPrototypeId { get; set; } = string.Empty;

    public NCBankTransactionType TransactionType { get; set; }

    [MaxLength(512)]
    public string Reason { get; set; } = string.Empty;

    public Guid? ActorAccountId { get; set; }
    public int? ActorProfileId { get; set; }
    public int? RoundId { get; set; }
    public long? DebitBalanceAfter { get; set; }
    public long? CreditBalanceAfter { get; set; }
    public DateTime Timestamp { get; set; }
}
