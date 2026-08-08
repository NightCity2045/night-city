// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>
/// Persistent bank account owned by exactly one character profile.
/// </summary>
[Table("nc_character_bank_account")]
public sealed class NCCharacterBankAccount
{
    [Key]
    public int ProfileId { get; set; }

    [MaxLength(20)]
    public string AccountNumber { get; set; } = string.Empty;

    [MaxLength(8)]
    public string Pin { get; set; } = string.Empty;

    public int Balance { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public abstract partial class ServerDbContext
{
    public DbSet<NCCharacterBankAccount> NCCharacterBankAccounts => Set<NCCharacterBankAccount>();

    private static void ConfigureNCBankModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCCharacterBankAccount>()
            .HasOne<Profile>()
            .WithOne()
            .HasForeignKey<NCCharacterBankAccount>(account => account.ProfileId)
            // Keep the constraint name deterministic so EF snapshots match on every provider.
            .HasConstraintName("FK_nc_character_bank_account_profile_profile_id")
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCCharacterBankAccount>()
            .HasIndex(account => account.AccountNumber)
            .IsUnique();
    }
}
