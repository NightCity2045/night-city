// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>
/// A bank account owned by one character profile or by one department. Exactly one owner column is set.
/// </summary>
[Table("nc_bank_account")]
public sealed class NCBankAccount
{
    [Key]
    public int Id { get; set; }

    [MaxLength(16)]
    public string Number { get; set; } = string.Empty;

    /// <summary>Owning character profile, or null for a department account.</summary>
    public int? ProfileId { get; set; }

    /// <summary>Owning department prototype id, or null for a character account.</summary>
    [MaxLength(64)]
    public string? DepartmentId { get; set; }

    public long Balance { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>Bank fee rate of a payer category, in basis points (1/100 of a percent).</summary>
[Table("nc_bank_fee_rate")]
public sealed class NCBankFeeRate
{
    [Key]
    [MaxLength(64)]
    public string Category { get; set; } = string.Empty;

    public int BasisPoints { get; set; }
}

/// <summary>Salary a department pays one job each payday.</summary>
[Table("nc_bank_salary")]
public sealed class NCBankSalary
{
    [MaxLength(64)]
    public string DepartmentId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string JobId { get; set; } = string.Empty;

    public int Amount { get; set; }
}

public abstract partial class ServerDbContext
{
    public DbSet<NCBankAccount> NCBankAccounts => Set<NCBankAccount>();
    public DbSet<NCBankFeeRate> NCBankFeeRates => Set<NCBankFeeRate>();
    public DbSet<NCBankSalary> NCBankSalaries => Set<NCBankSalary>();

    private static void ConfigureNCBankModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCBankAccount>()
            .HasIndex(account => account.Number)
            .IsUnique();
        modelBuilder.Entity<NCBankAccount>()
            .HasIndex(account => account.ProfileId)
            .IsUnique();
        modelBuilder.Entity<NCBankAccount>()
            .HasIndex(account => account.DepartmentId)
            .IsUnique();
        modelBuilder.Entity<NCBankAccount>()
            .HasOne<Profile>()
            .WithOne()
            .HasForeignKey<NCBankAccount>(account => account.ProfileId)
            .HasConstraintName("FK_nc_bank_account_profile_profile_id")
            .OnDelete(DeleteBehavior.Cascade);


        modelBuilder.Entity<NCBankSalary>()
            .HasKey(salary => new { salary.DepartmentId, salary.JobId });
    }
}
