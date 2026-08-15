// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Employment;
using Content.Server._NC.LivingCity.Migration;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.LivingCity.Migration;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Migration;

[TestFixture]
[TestOf(typeof(NCLegacyBankJobsMigrationSystem))]
public sealed class NCLegacyBankJobsMigrationSystemTest : GameTest
{
    [Test]
    public async Task BackfillIsIdempotentDualReadSafeAndResumesSplitCommits()
    {
        var server = Pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var migration = server.System<NCLegacyBankJobsMigrationSystem>();
        var finance = server.System<NCPartyFinanceAdapterSystem>();
        var ledger = server.System<NCEconomyLedgerSystem>();
        var employment = server.System<NCEmploymentRegistrySystem>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();

        var workerUser = new NetUserId(Guid.NewGuid());
        var workerPreference = await database.InitPrefsAsync(
            workerUser,
            new HumanoidCharacterProfile { Name = "Legacy Worker" },
            CancellationToken.None);
        var workerId = new NCCharacterId(workerPreference.Profiles.Single().Id);
        Assert.That(await database.GetOrCreateNCBankAccountAsync(workerId, 250, "NC", 4), Is.Not.Null);
        Assert.That(await database.SetNCCharacterEmploymentAsync(
            workerUser,
            workerId.Value,
            new ProtoId<JobPrototype>("NCPDCadet")), Is.True);

        // This second account is projected manually before the binding write to model a server process dying between
        // the ledger commit and provenance commit.
        var interruptedUser = new NetUserId(Guid.NewGuid());
        var interruptedPreference = await database.InitPrefsAsync(
            interruptedUser,
            new HumanoidCharacterProfile { Name = "Interrupted Worker" },
            CancellationToken.None);
        var interruptedId = new NCCharacterId(interruptedPreference.Profiles.Single().Id);
        Assert.That(await database.GetOrCreateNCBankAccountAsync(interruptedId, 125, "NC", 4), Is.Not.Null);
        Assert.That(await database.GetOrCreateNCOrganizationBankAccountAsync("NCPD", 5_000), Is.Not.Null);

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world clock did not start.");

        var beforeApply = await migration.RehearseAsync(NCLegacyMigrationRunMode.Verify, "integration-test");
        Assert.Multiple(() =>
        {
            Assert.That(beforeApply.Status, Is.EqualTo(NCLegacyMigrationRunStatus.Completed));
            Assert.That(beforeApply.Processed, Is.EqualTo(4));
            Assert.That(beforeApply.Conflicts, Is.EqualTo(4));
            Assert.That(beforeApply.Changed, Is.Zero);
        });

        await CommitInterruptedOpeningBalance(
            database,
            finance,
            ledger,
            interruptedId,
            125);

        var applied = await migration.RehearseAsync(NCLegacyMigrationRunMode.Apply, "integration-test");
        var replayed = await migration.RehearseAsync(NCLegacyMigrationRunMode.Apply, "integration-test");
        var verified = await migration.RehearseAsync(NCLegacyMigrationRunMode.Verify, "integration-test");
        Assert.Multiple(() =>
        {
            Assert.That(applied.Status, Is.EqualTo(NCLegacyMigrationRunStatus.Completed));
            Assert.That(applied.Processed, Is.EqualTo(4));
            Assert.That(applied.Changed, Is.EqualTo(4));
            Assert.That(applied.Conflicts, Is.Zero);
            Assert.That(applied.Failed, Is.Zero);
            Assert.That(replayed.Aligned, Is.EqualTo(4));
            Assert.That(replayed.Changed, Is.Zero);
            Assert.That(verified.Aligned, Is.EqualTo(4));
            Assert.That(verified.Conflicts, Is.Zero);
        });

        var workerParty = NCPartyRef.ForCharacter(workerId);
        var workerAccountId = finance.GetPrimaryAccountId(workerParty);
        var workerAccount = await ledger.GetAccountAsync(workerAccountId, true);
        var currentEmployment = await employment.FindCurrentByEmployeeAsync(workerParty, true);
        Assert.Multiple(() =>
        {
            Assert.That(workerAccount?.Snapshot.Balance, Is.EqualTo(250));
            Assert.That(currentEmployment?.Snapshot.PositionId, Is.EqualTo("NCPDCadet"));
            Assert.That(currentEmployment?.Snapshot.Status, Is.EqualTo(NCEmploymentStatus.Active));
        });

        // Model the second split commit: legacy changed, the old tenure was deterministically released, then the
        // process died before the replacement hire and binding write.
        Assert.That(await database.SetNCCharacterEmploymentAsync(
            workerUser,
            workerId.Value,
            new ProtoId<JobPrototype>("NCPDOfficer")), Is.True);
        await CommitInterruptedEmploymentRelease(
            database,
            finance,
            employment,
            prototypes,
            workerId,
            currentEmployment!);

        var employmentRecovery = await migration.RehearseAsync(NCLegacyMigrationRunMode.Apply, "integration-test");
        var recoveredEmployment = await employment.FindCurrentByEmployeeAsync(workerParty, true);
        Assert.Multiple(() =>
        {
            Assert.That(employmentRecovery.Conflicts, Is.Zero);
            Assert.That(employmentRecovery.Failed, Is.Zero);
            Assert.That(employmentRecovery.Changed, Is.EqualTo(1));
            Assert.That(recoveredEmployment?.Snapshot.PositionId, Is.EqualTo("NCPDOfficer"));
            Assert.That(recoveredEmployment?.Snapshot.Status, Is.EqualTo(NCEmploymentStatus.Active));
        });

        // A non-migration common-domain transaction invalidates the saved projection fingerprint. A later legacy
        // change must report a conflict instead of overwriting that activity.
        var clearing = await finance.EnsureLegacyClearingAccountAsync("integration-test");
        workerAccount = await ledger.GetAccountAsync(workerAccountId, true);
        var clearingAccount = await ledger.GetAccountAsync(clearing.AccountId, true);
        Assert.That(workerAccount, Is.Not.Null);
        Assert.That(clearingAccount, Is.Not.Null);
        var nativeMutation = await ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
            Guid.NewGuid(),
            NCTransactionId.New(),
            NCLedgerTransactionKind.Transfer,
            "integration non-migration transaction",
            [
                new NCLedgerLegRequest(workerAccountId, workerAccount!.AggregateVersion, -10),
                new NCLedgerLegRequest(clearing.AccountId, clearingAccount!.AggregateVersion, 10),
            ],
            workerParty,
            Guid.NewGuid(),
            AuditSource: "integration-test"));
        Assert.That(nativeMutation.Status, Is.EqualTo(NCEconomyMutationStatus.Applied));
        Assert.That(await database.TryAdjustNCBankBalanceAsync(workerId, 20), Is.Not.Null);

        var conflict = await migration.RehearseAsync(NCLegacyMigrationRunMode.Apply, "integration-test");
        workerAccount = await ledger.GetAccountAsync(workerAccountId, true);
        Assert.Multiple(() =>
        {
            Assert.That(conflict.Conflicts, Is.EqualTo(1));
            Assert.That(conflict.Issues.Single().SourceKey, Is.EqualTo($"character:{workerId.Value}"));
            Assert.That(conflict.Issues.Single().Kind, Is.EqualTo(NCLegacyMigrationIssueKind.TargetConflict));
            Assert.That(workerAccount?.Snapshot.Balance, Is.EqualTo(240));
        });

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
    }

    private static async Task CommitInterruptedOpeningBalance(
        IServerDbManager database,
        NCPartyFinanceAdapterSystem finance,
        NCEconomyLedgerSystem ledger,
        NCCharacterId characterId,
        long balance)
    {
        var projection = (await database.GetNCLegacyCharacterBankPageAsync(0, 256))
            .Single(value => value.CharacterId == characterId);
        var sourceKey = $"character:{characterId.Value}";
        var sourceRevision = NCLegacyBankJobsIdentity.Revision(projection.Balance, projection.UpdatedAt.Ticks);
        var account = await finance.EnsurePrimaryAccountAsync(NCPartyRef.ForCharacter(characterId), "integration-test");
        var clearing = await finance.EnsureLegacyClearingAccountAsync("integration-test");
        var clearingRecord = await ledger.GetAccountAsync(clearing.AccountId, true);
        Assert.That(account.IsResolved, Is.True);
        Assert.That(clearingRecord, Is.Not.Null);

        var result = await ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
            NCLegacyBankJobsIdentity.Command("post-bank-projection", sourceKey, sourceRevision),
            NCLegacyBankJobsIdentity.Transaction("opening-balance", sourceKey, sourceRevision),
            NCLedgerTransactionKind.OpeningBalance,
            $"legacy {NCLegacySourceKind.CharacterBank} projection {sourceKey}",
            [
                new NCLedgerLegRequest(clearing.AccountId, clearingRecord!.AggregateVersion, -balance),
                new NCLedgerLegRequest(account.AccountId, account.Account!.AggregateVersion, balance),
            ],
            clearing.Owner,
            NCLegacyBankJobsIdentity.Command("correlation-bank-projection", sourceKey, sourceRevision),
            AuditSource: "integration-test"));
        Assert.That(result.Status, Is.EqualTo(NCEconomyMutationStatus.Applied));
    }

    private static async Task CommitInterruptedEmploymentRelease(
        IServerDbManager database,
        NCPartyFinanceAdapterSystem finance,
        NCEmploymentRegistrySystem employment,
        IPrototypeManager prototypes,
        NCCharacterId characterId,
        NCEmploymentRecord previous)
    {
        var row = (await database.GetNCLegacyEmploymentPageAsync(0, 256))
            .Single(value => value.CharacterId == characterId);
        var job = prototypes.Index(new ProtoId<JobPrototype>(row.JobPrototypeId));
        var intervalTicks = TimeSpan.FromSeconds(
            Math.Max(1f, finance.Configuration.PaydayIntervalSeconds)).Ticks;
        var sourceKey = $"character:{characterId.Value}";
        var sourceRevision = NCLegacyBankJobsIdentity.Revision(
            row.JobPrototypeId,
            row.State,
            row.StartedAt.Ticks,
            row.UpdatedAt.Ticks,
            "NCPD",
            job.Salary,
            intervalTicks);
        var employee = NCPartyRef.ForCharacter(characterId);
        var index = await employment.GetEmployeeIndexAsync(employee, true);
        Assert.That(index, Is.Not.Null);

        var result = await employment.ChangeAsync(new NCChangeEmploymentRequest(
            NCLegacyBankJobsIdentity.Command("terminate-previous-employment", sourceKey, sourceRevision),
            previous.Snapshot.EmploymentId,
            previous.AggregateVersion,
            index!.AggregateVersion,
            previous.Snapshot.PositionId,
            NCEmploymentStatus.Terminated,
            previous.Snapshot.EmployerAccountId,
            previous.Snapshot.PayeeAccountId,
            previous.Snapshot.SalaryAmount,
            previous.Snapshot.PayrollIntervalTicks,
            NCLegacyBankJobsIdentity.OrganizationParty("NCPD"),
            NCLegacyBankJobsIdentity.Command("correlation-terminate-employment", sourceKey, sourceRevision),
            AuditSource: "integration-test"));
        Assert.That(result.Status, Is.EqualTo(NCEmploymentMutationStatus.Applied));
    }

    private static async Task WaitUntil(
        RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate,
        string failure,
        int attempts = 180)
    {
        for (var i = 0; i < attempts; i++)
        {
            await server.WaitRunTicks(1);
            var complete = false;
            await server.WaitPost(() => complete = predicate());
            if (complete)
                return;
        }

        Assert.Fail(failure);
    }
}
