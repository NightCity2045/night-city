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

    Task<NCBankMutationResult> CreditNCPayrollAsync(
        int profileId,
        long amount,
        string reason,
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

    public Task<NCBankMutationResult> CreditNCPayrollAsync(
        int profileId,
        long amount,
        string reason,
        int roundId,
        Guid requestId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.CreditNCPayrollAsync(
            profileId, amount, reason, roundId, requestId));
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
        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new NCBankMutationResult(true, null, debit.Balance, credit.Balance);
    }

    public async Task<NCBankMutationResult> CreditNCPayrollAsync(
        int profileId,
        long amount,
        string reason,
        int roundId,
        Guid requestId)
    {
        if (amount <= 0)
            return new NCBankMutationResult(false, "nc-bank-error-invalid-transfer", 0, 0);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        if (await db.DbContext.NCBankTransaction.AnyAsync(entry => entry.RequestId == requestId))
            return new NCBankMutationResult(false, "nc-bank-error-duplicate-request", 0, 0);

        var account = await db.DbContext.NCBankAccount.SingleOrDefaultAsync(entry =>
            entry.OwnerProfileId == profileId &&
            entry.AccountType == NCBankAccountType.Personal &&
            entry.Status == NCBankAccountStatus.Active);
        if (account == null)
            return new NCBankMutationResult(false, "nc-bank-error-unavailable-account", 0, 0);

        account.Balance += amount;
        account.Version++;
        account.UpdatedAt = DateTime.UtcNow;
        AddTransaction(db.DbContext, requestId, null, account, amount,
            NCBankTransactionType.Payroll, reason, null, profileId, roundId);
        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new NCBankMutationResult(true, null, 0, account.Balance);
    }

    private static void AddTransaction(
        ServerDbContext context,
        Guid requestId,
        NCBankAccount? debit,
        NCBankAccount credit,
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
            CreditAccountId = credit.BankAccountId,
            Amount = amount,
            CurrencyPrototypeId = credit.CurrencyPrototypeId,
            TransactionType = type,
            Reason = reason,
            ActorAccountId = actorAccountId,
            ActorProfileId = actorProfileId,
            RoundId = roundId,
            DebitBalanceAfter = debit?.Balance,
            CreditBalanceAfter = credit.Balance,
            Timestamp = DateTime.UtcNow,
        });
    }
}
