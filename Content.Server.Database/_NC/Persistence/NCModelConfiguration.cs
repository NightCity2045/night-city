// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public abstract partial class ServerDbContext
{
    public DbSet<NCCharacterProgression> NCCharacterProgression { get; set; } = null!;
    public DbSet<NCCharacterRoundParticipation> NCCharacterRoundParticipation { get; set; } = null!;
    public DbSet<NCRoundAccountCredit> NCRoundAccountCredit { get; set; } = null!;
    public DbSet<NCCharacterSkill> NCCharacterSkill { get; set; } = null!;
    public DbSet<NCOrganization> NCOrganization { get; set; } = null!;
    public DbSet<NCDepartment> NCDepartment { get; set; } = null!;
    public DbSet<NCPosition> NCPosition { get; set; } = null!;
    public DbSet<NCCharacterEmployment> NCCharacterEmployment { get; set; } = null!;
    public DbSet<NCEmploymentHistory> NCEmploymentHistory { get; set; } = null!;
    public DbSet<NCBankAccount> NCBankAccount { get; set; } = null!;
    public DbSet<NCBankTransaction> NCBankTransaction { get; set; } = null!;
    public DbSet<NCProperty> NCProperty { get; set; } = null!;
    public DbSet<NCPropertyOwnership> NCPropertyOwnership { get; set; } = null!;
    public DbSet<NCBusiness> NCBusiness { get; set; } = null!;
    public DbSet<NCBusinessOwnership> NCBusinessOwnership { get; set; } = null!;
    public DbSet<NCCharacterLifecycle> NCCharacterLifecycle { get; set; } = null!;
    public DbSet<NCPersistenceAudit> NCPersistenceAudit { get; set; } = null!;
    public DbSet<NCDeletedCharacterAudit> NCDeletedCharacterAudit { get; set; } = null!;
    public DbSet<NCCharacterLicense> NCCharacterLicense { get; set; } = null!;
    public DbSet<NCCharacterDocument> NCCharacterDocument { get; set; } = null!;
    public DbSet<NCInheritanceCase> NCInheritanceCase { get; set; } = null!;

    /// <summary>
    /// Configures Night City persistence without mixing the domain models into upstream database files.
    /// </summary>
    partial void OnModelCreatingNC(ModelBuilder modelBuilder)
    {
        ConfigureCharacterProgression(modelBuilder);
        ConfigureOrganizations(modelBuilder);
        ConfigureBank(modelBuilder);
        ConfigureProperty(modelBuilder);
        ConfigureLifecycleAndAudit(modelBuilder);
    }

    private static void ConfigureCharacterProgression(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCCharacterProgression>()
            .HasOne<Profile>()
            .WithOne()
            .HasForeignKey<NCCharacterProgression>(entry => entry.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCCharacterProgression>()
            .HasOne<Round>()
            .WithMany()
            .HasForeignKey(entry => entry.LastCountedRoundId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<NCCharacterProgression>().ToTable(table =>
        {
            table.HasCheckConstraint("CK_nc_character_progression_completed_rounds", "completed_rounds >= 0");
            table.HasCheckConstraint("CK_nc_character_progression_level", "level >= 1 AND level <= 10");
            table.HasCheckConstraint(
                "CK_nc_character_progression_spent_skill_points",
                // The upper budget is defined by the RED progression prototype and validated transactionally.
                "spent_skill_points >= 0");
        });

        modelBuilder.Entity<NCCharacterRoundParticipation>()
            .HasKey(entry => new { entry.ProfileId, entry.RoundId });

        modelBuilder.Entity<NCCharacterRoundParticipation>()
            .HasOne<Profile>()
            .WithMany()
            .HasForeignKey(entry => entry.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCCharacterRoundParticipation>()
            .HasOne<Round>()
            .WithMany()
            .HasForeignKey(entry => entry.RoundId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCCharacterRoundParticipation>()
            .HasIndex(entry => new { entry.AccountId, entry.RoundId });

        modelBuilder.Entity<NCCharacterRoundParticipation>().ToTable(table =>
            table.HasCheckConstraint(
                "CK_nc_character_round_participation_active_seconds",
                "active_seconds >= 0"));

        modelBuilder.Entity<NCRoundAccountCredit>()
            .HasKey(entry => new { entry.AccountId, entry.RoundId });

        modelBuilder.Entity<NCRoundAccountCredit>()
            .HasOne<Round>()
            .WithMany()
            .HasForeignKey(entry => entry.RoundId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCRoundAccountCredit>()
            .HasIndex(entry => entry.CreditedProfileId);

        modelBuilder.Entity<NCCharacterSkill>()
            .HasKey(entry => new { entry.ProfileId, entry.SkillPrototypeId });

        modelBuilder.Entity<NCCharacterSkill>()
            .HasOne<Profile>()
            .WithMany()
            .HasForeignKey(entry => entry.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCCharacterSkill>().ToTable(table =>
        {
            table.HasCheckConstraint("CK_nc_character_skill_rank", "rank >= 0");
            table.HasCheckConstraint("CK_nc_character_skill_spent_points", "spent_points >= 0");
        });
    }

    private static void ConfigureOrganizations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCOrganization>()
            .HasIndex(entry => entry.PrototypeId)
            .IsUnique();

        modelBuilder.Entity<NCOrganization>()
            .HasOne<NCPosition>()
            .WithMany()
            .HasForeignKey(entry => entry.DefaultEntryPositionId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<NCOrganization>()
            .HasOne<NCBankAccount>()
            .WithMany()
            .HasForeignKey(entry => entry.BankAccountId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<NCDepartment>()
            .HasOne<NCOrganization>()
            .WithMany()
            .HasForeignKey(entry => entry.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<NCDepartment>()
            .HasIndex(entry => new { entry.OrganizationId, entry.PrototypeId })
            .IsUnique();

        modelBuilder.Entity<NCPosition>()
            .HasOne<NCOrganization>()
            .WithMany()
            .HasForeignKey(entry => entry.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<NCPosition>()
            .HasOne<NCDepartment>()
            .WithMany()
            .HasForeignKey(entry => entry.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<NCPosition>()
            .HasOne<NCBankAccount>()
            .WithMany()
            .HasForeignKey(entry => entry.PayrollAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<NCPosition>()
            .HasIndex(entry => new { entry.OrganizationId, entry.PrototypeId })
            .IsUnique();

        modelBuilder.Entity<NCPosition>().ToTable(table =>
        {
            table.HasCheckConstraint("CK_nc_position_base_salary", "base_salary >= 0");
            table.HasCheckConstraint("CK_nc_position_pay_interval", "pay_interval_seconds >= 0");
        });

        modelBuilder.Entity<NCCharacterEmployment>()
            .HasOne<Profile>()
            .WithOne()
            .HasForeignKey<NCCharacterEmployment>(entry => entry.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCCharacterEmployment>()
            .HasOne<NCOrganization>()
            .WithMany()
            .HasForeignKey(entry => entry.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<NCCharacterEmployment>()
            .HasOne<NCDepartment>()
            .WithMany()
            .HasForeignKey(entry => entry.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<NCCharacterEmployment>()
            .HasOne<NCPosition>()
            .WithMany()
            .HasForeignKey(entry => entry.PositionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<NCCharacterEmployment>()
            .Property(entry => entry.Version)
            .IsConcurrencyToken();

        modelBuilder.Entity<NCCharacterEmployment>().ToTable(table =>
            table.HasCheckConstraint("CK_nc_character_employment_version", "version >= 0"));

        modelBuilder.Entity<NCEmploymentHistory>()
            .HasIndex(entry => entry.ProfileId);

        modelBuilder.Entity<NCEmploymentHistory>()
            .HasIndex(entry => entry.RequestId)
            .IsUnique();

        modelBuilder.Entity<NCEmploymentHistory>()
            .HasOne<NCOrganization>()
            .WithMany()
            .HasForeignKey(entry => entry.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureBank(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCBankAccount>()
            .HasIndex(entry => entry.AccountNumber)
            .IsUnique();

        modelBuilder.Entity<NCBankAccount>()
            .HasIndex(entry => entry.OwnerProfileId);

        modelBuilder.Entity<NCBankAccount>()
            .HasOne<Profile>()
            .WithMany()
            .HasForeignKey(entry => entry.OwnerProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<NCBankAccount>()
            .Property(entry => entry.Version)
            .IsConcurrencyToken();

        modelBuilder.Entity<NCBankAccount>().ToTable(table =>
        {
            table.HasCheckConstraint("CK_nc_bank_account_balance", "balance >= 0");
            table.HasCheckConstraint("CK_nc_bank_account_version", "version >= 0");
            table.HasCheckConstraint(
                "CK_nc_bank_account_personal_owner",
                "(account_type IN (0, 3) AND owner_profile_id IS NOT NULL) OR " +
                "(account_type NOT IN (0, 3) AND owner_profile_id IS NULL)");
        });

        modelBuilder.Entity<NCBankTransaction>()
            .HasIndex(entry => entry.RequestId)
            .IsUnique();

        modelBuilder.Entity<NCBankTransaction>()
            .HasIndex(entry => entry.DebitAccountId);

        modelBuilder.Entity<NCBankTransaction>()
            .HasIndex(entry => entry.CreditAccountId);

        modelBuilder.Entity<NCBankTransaction>()
            .HasOne<NCBankAccount>()
            .WithMany()
            .HasForeignKey(entry => entry.DebitAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<NCBankTransaction>()
            .HasOne<NCBankAccount>()
            .WithMany()
            .HasForeignKey(entry => entry.CreditAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<NCBankTransaction>().ToTable(table =>
        {
            table.HasCheckConstraint("CK_nc_bank_transaction_amount", "amount > 0");
            table.HasCheckConstraint(
                "CK_nc_bank_transaction_accounts",
                "debit_account_id IS NOT NULL OR credit_account_id IS NOT NULL");
        });
    }

    private static void ConfigureProperty(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCProperty>()
            .HasIndex(entry => entry.MapEntityId)
            .IsUnique();

        modelBuilder.Entity<NCPropertyOwnership>()
            .HasKey(entry => new { entry.PropertyId, entry.OwnerType, entry.OwnerId });

        modelBuilder.Entity<NCPropertyOwnership>()
            .HasOne<NCProperty>()
            .WithMany()
            .HasForeignKey(entry => entry.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCPropertyOwnership>().ToTable(table =>
            table.HasCheckConstraint(
                "CK_nc_property_ownership_share",
                "share_basis_points > 0 AND share_basis_points <= 10000"));

        modelBuilder.Entity<NCBusiness>()
            .HasOne<NCProperty>()
            .WithMany()
            .HasForeignKey(entry => entry.PropertyId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<NCBusiness>()
            .HasOne<NCBankAccount>()
            .WithMany()
            .HasForeignKey(entry => entry.BankAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<NCBusiness>()
            .HasIndex(entry => entry.BankAccountId)
            .IsUnique();

        modelBuilder.Entity<NCBusinessOwnership>()
            .HasKey(entry => new { entry.BusinessId, entry.OwnerProfileId });

        modelBuilder.Entity<NCBusinessOwnership>()
            .HasOne<NCBusiness>()
            .WithMany()
            .HasForeignKey(entry => entry.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCBusinessOwnership>()
            .HasOne<Profile>()
            .WithMany()
            .HasForeignKey(entry => entry.OwnerProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<NCBusinessOwnership>().ToTable(table =>
            table.HasCheckConstraint(
                "CK_nc_business_ownership_share",
                "share_basis_points > 0 AND share_basis_points <= 10000"));
    }

    private static void ConfigureLifecycleAndAudit(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCCharacterLifecycle>()
            .HasOne<Profile>()
            .WithOne()
            .HasForeignKey<NCCharacterLifecycle>(entry => entry.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCCharacterLifecycle>()
            .HasIndex(entry => entry.RequestId)
            .IsUnique();

        modelBuilder.Entity<NCPersistenceAudit>()
            .HasIndex(entry => entry.RequestId)
            .IsUnique();

        modelBuilder.Entity<NCPersistenceAudit>()
            .HasIndex(entry => entry.TargetProfileId);

        modelBuilder.Entity<NCPersistenceAudit>()
            .HasIndex(entry => entry.Timestamp);

        modelBuilder.Entity<NCDeletedCharacterAudit>()
            .HasIndex(entry => entry.RequestId)
            .IsUnique();

        modelBuilder.Entity<NCDeletedCharacterAudit>()
            .HasIndex(entry => entry.DeletedProfileId);

        modelBuilder.Entity<NCCharacterLicense>()
            .HasKey(entry => new { entry.ProfileId, entry.LicensePrototypeId });

        modelBuilder.Entity<NCCharacterLicense>()
            .HasOne<Profile>()
            .WithMany()
            .HasForeignKey(entry => entry.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCCharacterDocument>()
            .HasIndex(entry => entry.SerialNumber)
            .IsUnique();

        modelBuilder.Entity<NCCharacterDocument>()
            .HasIndex(entry => new { entry.ProfileId, entry.DocumentPrototypeId });

        modelBuilder.Entity<NCCharacterDocument>()
            .HasOne<Profile>()
            .WithMany()
            .HasForeignKey(entry => entry.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCInheritanceCase>()
            .HasIndex(entry => new { entry.AssetType, entry.AssetId, entry.Status });

        modelBuilder.Entity<NCInheritanceCase>().ToTable(table =>
            table.HasCheckConstraint(
                "CK_nc_inheritance_case_share",
                "share_basis_points > 0 AND share_basis_points <= 10000"));
    }
}
