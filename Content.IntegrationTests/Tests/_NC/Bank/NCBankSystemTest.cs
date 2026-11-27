// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.Bank;
using Content.Server.Database;
using Content.Shared.Database._NC.Bank;

namespace Content.IntegrationTests.Tests._NC.Bank;

[TestFixture]
[TestOf(typeof(NCBankSystem))]
[NonParallelizable]
public sealed class NCBankSystemTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
        Dirty = true,
    };

    /// <summary>
    /// Department accounts open with their budgets; deposits lose the payer's fee, transfers charge it on top,
    /// admin adjustments cannot overdraw, and every change reaches the database journal.
    /// </summary>
    [Test]
    public async Task DepositsTransfersAndAdjustmentsFollowTheFeeRules()
    {
        var bank = Server.System<NCBankSystem>();
        var db = Server.ResolveDependency<IServerDbManager>();
        await PoolManager.WaitUntil(Server, () => bank.Ready);

        await Server.WaitAssertion(() =>
        {
            Assert.That(bank.TryGetDepartmentAccount("NCPD", out _), Is.True);
            Assert.That(bank.TryGetDepartmentAccount("Militech", out _), Is.True);
        });

        bank.TryGetDepartmentAccount("NCPD", out var ncpd);
        bank.TryGetDepartmentAccount("Militech", out var militech);
        var ncpdStart = ncpd.Balance;
        var militechStart = militech.Balance;

        await Server.WaitAssertion(() =>
        {
            Assert.That(bank.GetFeeBasisPoints(ncpd), Is.EqualTo(2000));
            Assert.That(bank.GetFeeBasisPoints(militech), Is.EqualTo(1000));
            Assert.That(bank.GetFeeBasisPoints(NCBankSystem.ResidentFeeCategory), Is.EqualTo(4000));

            Assert.That(bank.TryDeposit(ncpd, 1000, null, out var credited), Is.True);
            Assert.That(credited, Is.EqualTo(800), "NCPD loses 20% of deposited cash.");
            Assert.That(bank.TryWithdraw(ncpd, ncpd.Balance + 1, null), Is.False, "Cannot withdraw more than the balance.");
        });

        NCBankTransferResult? result = null;
        await Server.WaitPost(async () => result = await bank.TryTransferAsync(militech, ncpd.Number, 1000, null));
        await PoolManager.WaitUntil(Server, () => result != null);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(NCBankTransferResult.Success));
            Assert.That(militech.Balance, Is.EqualTo(militechStart - 1100), "Militech pays the amount plus its 10% fee.");
            Assert.That(ncpd.Balance, Is.EqualTo(ncpdStart + 800 + 1000), "NCPD receives the full amount.");
        });

        bool? overdrawn = null;
        await Server.WaitPost(async () =>
            overdrawn = await bank.TryAdminAdjustAsync(ncpd.Number, -(ncpd.Balance + 1), "test", "admin") != null);
        await PoolManager.WaitUntil(Server, () => overdrawn != null);
        Assert.That(overdrawn, Is.False, "An admin withdrawal cannot overdraw the account.");

        NCBankAccountRecord? adjusted = null;
        await Server.WaitPost(async () => adjusted = await bank.TryAdminAdjustAsync(ncpd.Number, 500, "test grant", "admin"));
        await PoolManager.WaitUntil(Server, () => adjusted != null);

        var flushed = false;
        await Server.WaitPost(async () =>
        {
            await bank.FlushAsync();
            flushed = true;
        });
        await PoolManager.WaitUntil(Server, () => flushed);
        var stored = await db.GetNCBankAccountAsync(ncpd.Id);
        var history = await db.GetNCBankHistoryAsync(ncpd.Id, 10);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.Balance, Is.EqualTo(ncpd.Balance), "The database balance follows the loaded account.");
            Assert.That(history.Select(line => line.Kind), Does.Contain(NCBankTransactionKind.CashDeposit));
            Assert.That(history.Select(line => line.Kind), Does.Contain(NCBankTransactionKind.TransferIn));
            Assert.That(history.Single(line => line.Kind == NCBankTransactionKind.AdminGrant).Note, Is.EqualTo("test grant"));
        });
    }

    [Test]
    public async Task SalariesAndFeeRatesArePersisted()
    {
        var bank = Server.System<NCBankSystem>();
        var db = Server.ResolveDependency<IServerDbManager>();
        await PoolManager.WaitUntil(Server, () => bank.Ready);

        await Server.WaitAssertion(() =>
        {
            Assert.That(bank.GetSalary("NCPD", "NCPDOfficer"), Is.EqualTo(250));
            Assert.That(bank.TrySetSalary("NCPD", "NCPDOfficer", 300), Is.True);
            Assert.That(bank.TrySetSalary("NCPD", "MilitechChief", 300), Is.False, "Only the department's own jobs.");
            Assert.That(bank.TrySetFeeBasisPoints(NCBankSystem.ResidentFeeCategory, 3500), Is.True);
            Assert.That(bank.TrySetFeeBasisPoints("NotADepartment", 100), Is.False);
            Assert.That(bank.GetSalary("NCPD", "NCPDOfficer"), Is.EqualTo(300));
        });

        await PoolManager.WaitUntil(Server, async () => (await db.GetNCBankSalariesAsync())
            .Any(salary => salary.DepartmentId == "NCPD" && salary.JobId == "NCPDOfficer" && salary.Amount == 300));
        await PoolManager.WaitUntil(Server, async () => (await db.GetNCBankFeeRatesAsync())
            .TryGetValue(NCBankSystem.ResidentFeeCategory, out var rate) && rate == 3500);
    }
}
