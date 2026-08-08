// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>
/// Persistent operating budget owned by one organization prototype.
/// </summary>
[Table("nc_organization_bank_account")]
public sealed class NCOrganizationBankAccount
{
    [Key, MaxLength(64)]
    public string OrganizationPrototypeId { get; set; } = string.Empty;

    public int Balance { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<NCOrganizationBankTransaction> Transactions { get; set; } = new();
}

public enum NCOrganizationBankTransactionType : byte
{
    Deposit,
    Withdrawal,
    Salary,
}

/// <summary>
/// Append-only audit entry for every organization budget mutation.
/// </summary>
[Table("nc_organization_bank_transaction")]
public sealed class NCOrganizationBankTransaction
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(64)]
    public string OrganizationPrototypeId { get; set; } = string.Empty;
    public NCOrganizationBankAccount Organization { get; set; } = null!;

    public NCOrganizationBankTransactionType Type { get; set; }
    public int Amount { get; set; }
    public int BalanceAfter { get; set; }
    public int? ActorProfileId { get; set; }

    [MaxLength(128)]
    public string ActorName { get; set; } = string.Empty;

    [MaxLength(512)]
    public string Reason { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

public abstract partial class ServerDbContext
{
    public DbSet<NCOrganizationBankAccount> NCOrganizationBankAccounts => Set<NCOrganizationBankAccount>();
    public DbSet<NCOrganizationBankTransaction> NCOrganizationBankTransactions => Set<NCOrganizationBankTransaction>();

    private static void ConfigureNCOrganizationBankModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCOrganizationBankAccount>()
            .Property(account => account.OrganizationPrototypeId)
            .HasMaxLength(64);

        modelBuilder.Entity<NCOrganizationBankTransaction>()
            .HasOne(transaction => transaction.Organization)
            .WithMany(account => account.Transactions)
            .HasForeignKey(transaction => transaction.OrganizationPrototypeId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCOrganizationBankTransaction>()
            .HasIndex(transaction => new { transaction.OrganizationPrototypeId, transaction.CreatedAt });
    }
}
