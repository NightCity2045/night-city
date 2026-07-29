// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using System.Collections.Generic;
using System.Linq;
using Content.Server.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Content.IntegrationTests.Tests._NC.Persistence;

[TestFixture]
public sealed class NCPersistenceSchemaTest
{
    [Test]
    public async Task MigrationCreatesNightCityTablesAndEnforcesBankInvariants()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new SqliteServerDbContext(options);
        await context.Database.MigrateAsync();

        var tableNames = new HashSet<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'nc_%';";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tableNames.Add(reader.GetString(0));
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(tableNames, Does.Contain("nc_character_progression"));
            Assert.That(tableNames, Does.Contain("nc_character_lifecycle"));
            Assert.That(tableNames, Does.Contain("nc_character_employment"));
            Assert.That(tableNames, Does.Contain("nc_bank_account"));
            Assert.That(tableNames, Does.Contain("nc_bank_transaction"));
            Assert.That(tableNames, Does.Contain("nc_business"));
            Assert.That(tableNames, Does.Contain("nc_character_document"));
            Assert.That(tableNames, Does.Contain("nc_inheritance_case"));
            Assert.That(tableNames, Does.Contain("nc_persistence_audit"));
            Assert.That(tableNames, Has.Count.EqualTo(21));
            Assert.That(context.Model.FindEntityType(typeof(NCCharacterEmployment))
                ?.FindProperty(nameof(NCCharacterEmployment.Version))
                ?.IsConcurrencyToken, Is.True);
        });

        var accountId = Guid.NewGuid();
        context.NCBankAccount.Add(new NCBankAccount
        {
            BankAccountId = accountId,
            AccountNumber = "NC-TEST-VALID",
            AccountType = NCBankAccountType.System,
            CurrencyPrototypeId = "EuroDollar",
            Balance = 100,
            Status = NCBankAccountStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        context.NCBankAccount.Add(new NCBankAccount
        {
            BankAccountId = Guid.NewGuid(),
            AccountNumber = "NC-TEST-NEGATIVE",
            AccountType = NCBankAccountType.System,
            CurrencyPrototypeId = "EuroDollar",
            Balance = -1,
            Status = NCBankAccountStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        Assert.ThrowsAsync<DbUpdateException>(async () => await context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        var requestId = Guid.NewGuid();
        context.NCBankTransaction.Add(CreateTransaction(Guid.NewGuid(), requestId, accountId));
        await context.SaveChangesAsync();

        context.NCBankTransaction.Add(CreateTransaction(Guid.NewGuid(), requestId, accountId));
        Assert.ThrowsAsync<DbUpdateException>(async () => await context.SaveChangesAsync());
    }

    [Test]
    public async Task CharacterDeletionCascadesRuntimeStateButPreservesAudit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new SqliteServerDbContext(options);
        await context.Database.MigrateAsync();

        var preference = new Preference
        {
            UserId = Guid.NewGuid(),
            SelectedCharacterSlot = 0,
            AdminOOCColor = "#ffffff",
        };

        var profile = CreateProfile(preference);
        preference.Profiles.Add(profile);
        context.Preference.Add(preference);
        await context.SaveChangesAsync();

        context.NCCharacterProgression.Add(new NCCharacterProgression
        {
            ProfileId = profile.Id,
            Level = 1,
            UpdatedAt = DateTime.UtcNow,
        });
        context.NCCharacterLifecycle.Add(new NCCharacterLifecycle
        {
            ProfileId = profile.Id,
            Status = NCCharacterLifecycleStatus.Alive,
        });
        context.NCCharacterLicense.Add(new NCCharacterLicense
        {
            ProfileId = profile.Id,
            LicensePrototypeId = "NCDriverLicense",
            Status = NCLegalRecordStatus.Active,
            IssuedAt = DateTime.UtcNow,
            Reason = "schema-test",
        });
        context.NCCharacterDocument.Add(new NCCharacterDocument
        {
            DocumentId = Guid.NewGuid(),
            ProfileId = profile.Id,
            DocumentPrototypeId = "NCCitizenIdentityDocument",
            SerialNumber = $"NCID{profile.Id:D10}",
            Status = NCLegalRecordStatus.Active,
            IssuedAt = DateTime.UtcNow,
            Reason = "schema-test",
        });
        context.NCPersistenceAudit.Add(new NCPersistenceAudit
        {
            Timestamp = DateTime.UtcNow,
            TargetProfileId = profile.Id,
            Action = "test-delete",
            Reason = "schema-test",
            RequestId = Guid.NewGuid(),
        });
        await context.SaveChangesAsync();

        context.Profile.Remove(profile);
        await context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(context.NCCharacterProgression.Any(entry => entry.ProfileId == profile.Id), Is.False);
            Assert.That(context.NCCharacterLifecycle.Any(entry => entry.ProfileId == profile.Id), Is.False);
            Assert.That(context.NCCharacterLicense.Any(entry => entry.ProfileId == profile.Id), Is.False);
            Assert.That(context.NCCharacterDocument.Any(entry => entry.ProfileId == profile.Id), Is.False);
            Assert.That(context.NCPersistenceAudit.Any(entry => entry.TargetProfileId == profile.Id), Is.True);
        });
    }

    [Test]
    public async Task CharacterDeletionRequiresPersonalBankCleanup()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new SqliteServerDbContext(options);
        await context.Database.MigrateAsync();

        var preference = new Preference
        {
            UserId = Guid.NewGuid(),
            SelectedCharacterSlot = 0,
            AdminOOCColor = "#ffffff",
        };

        var profile = CreateProfile(preference);
        preference.Profiles.Add(profile);
        context.Preference.Add(preference);
        await context.SaveChangesAsync();

        context.NCBankAccount.Add(new NCBankAccount
        {
            BankAccountId = Guid.NewGuid(),
            AccountNumber = "NC-TEST-PERSONAL",
            AccountType = NCBankAccountType.Personal,
            OwnerProfileId = profile.Id,
            CurrencyPrototypeId = "EuroDollar",
            Balance = 100,
            Status = NCBankAccountStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        context.Profile.Remove(profile);

        // Permadeath must explicitly settle and close personal accounts before deleting the profile.
        Assert.ThrowsAsync<DbUpdateException>(async () => await context.SaveChangesAsync());
    }

    [Test]
    public async Task ConcurrentEmploymentChangesRejectTheStaleWriter()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var setup = new SqliteServerDbContext(options))
        {
            await setup.Database.MigrateAsync();
            var preference = new Preference
            {
                UserId = Guid.NewGuid(),
                SelectedCharacterSlot = 0,
                AdminOOCColor = "#ffffff",
            };
            var profile = CreateProfile(preference);
            preference.Profiles.Add(profile);
            setup.Preference.Add(preference);

            var organizationId = Guid.NewGuid();
            var positionId = Guid.NewGuid();
            setup.NCOrganization.Add(new NCOrganization
            {
                OrganizationId = organizationId,
                PrototypeId = "ConcurrencyOrganization",
                Name = "Concurrency organization",
                Status = NCOrganizationStatus.Active,
            });
            setup.NCPosition.Add(new NCPosition
            {
                PositionId = positionId,
                OrganizationId = organizationId,
                PrototypeId = "ConcurrencyPosition",
                Name = "Concurrency position",
                PayIntervalSeconds = 900,
            });
            await setup.SaveChangesAsync();
            setup.NCCharacterEmployment.Add(new NCCharacterEmployment
            {
                ProfileId = profile.Id,
                OrganizationId = organizationId,
                PositionId = positionId,
                EmploymentState = NCEmploymentState.Active,
                HiredAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Version = 1,
            });
            await setup.SaveChangesAsync();
        }

        await using var first = new SqliteServerDbContext(options);
        await using var second = new SqliteServerDbContext(options);
        var firstEntry = await first.NCCharacterEmployment.SingleAsync();
        var staleEntry = await second.NCCharacterEmployment.SingleAsync();

        firstEntry.Version++;
        firstEntry.UpdatedAt = DateTime.UtcNow;
        await first.SaveChangesAsync();

        staleEntry.Version++;
        staleEntry.UpdatedAt = DateTime.UtcNow;
        Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            async () => await second.SaveChangesAsync());
    }

    private static NCBankTransaction CreateTransaction(Guid transactionId, Guid requestId, Guid accountId)
    {
        return new NCBankTransaction
        {
            BankTransactionId = transactionId,
            RequestId = requestId,
            CreditAccountId = accountId,
            Amount = 10,
            CurrencyPrototypeId = "EuroDollar",
            TransactionType = NCBankTransactionType.Deposit,
            Reason = "schema-test",
            Timestamp = DateTime.UtcNow,
        };
    }

    private static Profile CreateProfile(Preference preference)
    {
        return new Profile
        {
            Slot = 0,
            CharacterName = "Schema Test",
            FlavorText = string.Empty,
            Age = 30,
            Sex = "Male",
            Gender = "Male",
            Species = "Human",
            HairName = string.Empty,
            HairColor = "#000000",
            FacialHairName = string.Empty,
            FacialHairColor = "#000000",
            EyeColor = "#000000",
            SkinColor = "#ffffff",
            Preference = preference,
        };
    }
}
