// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<NCBankMutationResult> TransferNCFundsAsync(
        Guid debitAccountId,
        Guid creditAccountId,
        long amount,
        NCBankTransactionType type,
        string reason,
        Guid? actorAccountId,
        int? actorProfileId,
        int? roundId,
        Guid requestId);

    Task<NCBankMutationResult> PayNCPositionSalaryAsync(
        int profileId,
        Guid positionId,
        string reason,
        int roundId,
        Guid requestId);

    Task<NCBankMutationResult> ChangeNCCashBalanceAsync(
        Guid accountId,
        long amount,
        bool deposit,
        Guid actorAccountId,
        int actorProfileId,
        int roundId,
        Guid requestId);
}

public sealed partial class ServerDbManager
{
    public Task<NCBankMutationResult> TransferNCFundsAsync(
        Guid debitAccountId,
        Guid creditAccountId,
        long amount,
        NCBankTransactionType type,
        string reason,
        Guid? actorAccountId,
        int? actorProfileId,
        int? roundId,
        Guid requestId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TransferNCFundsAsync(
            debitAccountId, creditAccountId, amount, type, reason,
            actorAccountId, actorProfileId, roundId, requestId));
    }

    public Task<NCBankMutationResult> PayNCPositionSalaryAsync(
        int profileId,
        Guid positionId,
        string reason,
        int roundId,
        Guid requestId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.PayNCPositionSalaryAsync(
            profileId, positionId, reason, roundId, requestId));
    }

    public Task<NCBankMutationResult> ChangeNCCashBalanceAsync(
        Guid accountId,
        long amount,
        bool deposit,
        Guid actorAccountId,
        int actorProfileId,
        int roundId,
        Guid requestId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.ChangeNCCashBalanceAsync(
            accountId, amount, deposit, actorAccountId, actorProfileId, roundId, requestId));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<NCBankMutationResult> TransferNCFundsAsync(
        Guid debitAccountId,
        Guid creditAccountId,
        long amount,
        NCBankTransactionType type,
        string reason,
        Guid? actorAccountId,
        int? actorProfileId,
        int? roundId,
        Guid requestId)
    {
        if (amount <= 0 || debitAccountId == creditAccountId)
            return new NCBankMutationResult(false, "nc-bank-error-invalid-transfer", 0, 0);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        if (await db.DbContext.NCBankTransaction.AnyAsync(entry => entry.RequestId == requestId))
            return new NCBankMutationResult(false, "nc-bank-error-duplicate-request", 0, 0);

        var debit = await db.DbContext.NCBankAccount.FindAsync(debitAccountId);
        var credit = await db.DbContext.NCBankAccount.FindAsync(creditAccountId);
        if (debit == null || credit == null ||
            debit.Status != NCBankAccountStatus.Active ||
            credit.Status != NCBankAccountStatus.Active ||
            debit.CurrencyPrototypeId != credit.CurrencyPrototypeId)
            return new NCBankMutationResult(false, "nc-bank-error-unavailable-account", 0, 0);
        if (debit.Balance < amount)
            return new NCBankMutationResult(false, "nc-bank-error-insufficient-funds", debit.Balance, credit.Balance);

        debit.Balance -= amount;
        credit.Balance += amount;
        debit.Version++;
        credit.Version++;
        debit.UpdatedAt = credit.UpdatedAt = DateTime.UtcNow;
        AddTransaction(db.DbContext, requestId, debit, credit, amount, type, reason,
            actorAccountId, actorProfileId, roundId);
        try
        {
            await db.DbContext.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another transaction changed one of the balances after this request read it.
            return new NCBankMutationResult(false, "nc-bank-error-concurrent-update", 0, 0);
        }
        await transaction.CommitAsync();
        return new NCBankMutationResult(true, null, debit.Balance, credit.Balance);
    }

    public async Task<NCBankMutationResult> PayNCPositionSalaryAsync(
        int profileId,
        Guid positionId,
        string reason,
        int roundId,
        Guid requestId)
    {
        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        if (await db.DbContext.NCBankTransaction.AnyAsync(entry => entry.RequestId == requestId))
            return new NCBankMutationResult(false, "nc-bank-error-duplicate-request", 0, 0);

        var employment = await db.DbContext.NCCharacterEmployment
            .SingleOrDefaultAsync(entry =>
                entry.ProfileId == profileId &&
                entry.PositionId == positionId &&
                (entry.EmploymentState == NCEmploymentState.Active ||
                 entry.EmploymentState == NCEmploymentState.SuspendedPaid));
        var position = employment == null
            ? null
            : await db.DbContext.NCPosition.FindAsync(positionId);
        var personalAccount = await db.DbContext.NCBankAccount.SingleOrDefaultAsync(entry =>
            entry.OwnerProfileId == profileId &&
            entry.AccountType == NCBankAccountType.Personal &&
            entry.Status == NCBankAccountStatus.Active);
        var payrollAccount = position?.PayrollAccountId == null
            ? null
            : await db.DbContext.NCBankAccount.FindAsync(position.PayrollAccountId.Value);
        if (employment == null ||
            position == null ||
            position.BaseSalary <= 0 ||
            personalAccount == null ||
            payrollAccount is not { Status: NCBankAccountStatus.Active } ||
            payrollAccount.CurrencyPrototypeId != personalAccount.CurrencyPrototypeId)
            return new NCBankMutationResult(false, "nc-bank-error-unavailable-account", 0, 0);
        if (payrollAccount.Balance < position.BaseSalary)
        {
            return new NCBankMutationResult(
                false,
                "nc-bank-error-payroll-insufficient-funds",
                payrollAccount.Balance,
                personalAccount.Balance);
        }

        payrollAccount.Balance -= position.BaseSalary;
        personalAccount.Balance += position.BaseSalary;
        payrollAccount.Version++;
        personalAccount.Version++;
        payrollAccount.UpdatedAt = personalAccount.UpdatedAt = DateTime.UtcNow;
        AddTransaction(
            db.DbContext,
            requestId,
            payrollAccount,
            personalAccount,
            position.BaseSalary,
            NCBankTransactionType.Payroll,
            reason,
            null,
            profileId,
            roundId);
        try
        {
            await db.DbContext.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return new NCBankMutationResult(false, "nc-bank-error-concurrent-update", 0, 0);
        }
        await transaction.CommitAsync();
        return new NCBankMutationResult(
            true,
            null,
            payrollAccount.Balance,
            personalAccount.Balance);
    }

    public async Task<NCBankMutationResult> ChangeNCCashBalanceAsync(
        Guid accountId,
        long amount,
        bool deposit,
        Guid actorAccountId,
        int actorProfileId,
        int roundId,
        Guid requestId)
    {
        if (amount <= 0)
            return new NCBankMutationResult(false, "nc-bank-error-invalid-transfer", 0, 0);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        if (await db.DbContext.NCBankTransaction.AnyAsync(entry => entry.RequestId == requestId))
            return new NCBankMutationResult(false, "nc-bank-error-duplicate-request", 0, 0);

        var account = await db.DbContext.NCBankAccount.FindAsync(accountId);
        if (account is not
            {
                Status: NCBankAccountStatus.Active,
                AccountType: NCBankAccountType.Personal,
                OwnerProfileId: not null,
            } ||
            account.OwnerProfileId != actorProfileId)
        {
            return new NCBankMutationResult(false, "nc-bank-error-unavailable-account", 0, 0);
        }
        if (!deposit && account.Balance < amount)
            return new NCBankMutationResult(false, "nc-bank-error-insufficient-funds", account.Balance, 0);

        account.Balance += deposit ? amount : -amount;
        account.Version++;
        account.UpdatedAt = DateTime.UtcNow;
        AddTransaction(
            db.DbContext,
            requestId,
            deposit ? null : account,
            deposit ? account : null,
            amount,
            deposit ? NCBankTransactionType.Deposit : NCBankTransactionType.Withdrawal,
            deposit ? "atm-cash-deposit" : "atm-cash-withdrawal",
            actorAccountId,
            actorProfileId,
            roundId);
        try
        {
            await db.DbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return new NCBankMutationResult(true, null, account.Balance, account.Balance);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new NCBankMutationResult(false, "nc-bank-error-concurrent-update", 0, 0);
        }
    }

    private static void AddTransaction(
        ServerDbContext context,
        Guid requestId,
        NCBankAccount? debit,
        NCBankAccount? credit,
        long amount,
        NCBankTransactionType type,
        string reason,
        Guid? actorAccountId,
        int? actorProfileId,
        int? roundId)
    {
        context.NCBankTransaction.Add(new NCBankTransaction
        {
            BankTransactionId = Guid.NewGuid(),
            RequestId = requestId,
            DebitAccountId = debit?.BankAccountId,
            CreditAccountId = credit?.BankAccountId,
            Amount = amount,
            CurrencyPrototypeId = (credit ?? debit)!.CurrencyPrototypeId,
            TransactionType = type,
            Reason = reason,
            ActorAccountId = actorAccountId,
            ActorProfileId = actorProfileId,
            RoundId = roundId,
            DebitBalanceAfter = debit?.Balance,
            CreditBalanceAfter = credit?.Balance,
            Timestamp = DateTime.UtcNow,
        });
    }
}
