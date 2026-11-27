// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Content.Shared.Database._NC.Bank;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCBankAccountData(int Id, string Number, int? ProfileId, string? DepartmentId, long Balance);

public sealed record NCBankAccountOpenResult(NCBankAccountData Account, bool Created);

/// <summary>One balance change to write: <paramref name="Amount"/> is added to the account balance.</summary>
public sealed record NCBankEntry(
    int AccountId,
    NCBankTransactionKind Kind,
    long Amount,
    long Fee,
    string? Counterparty,
    string Note,
    DateTime CreatedAt);

public sealed record NCBankTransactionData(
    long Id,
    NCBankTransactionKind Kind,
    long Amount,
    long Fee,
    string? Counterparty,
    string Note,
    DateTime CreatedAt);

public sealed record NCBankSalaryData(string DepartmentId, string JobId, int Amount);

public partial interface IServerDbManager
{
    Task<NCBankAccountOpenResult?> GetOrCreateNCCharacterBankAccountAsync(int profileId, long openingBalance);
    Task<NCBankAccountOpenResult> GetOrCreateNCDepartmentBankAccountAsync(string departmentId, long openingBalance);
    Task<NCBankAccountData?> GetNCBankAccountByNumberAsync(string number);
    Task<NCBankAccountData?> GetNCBankAccountAsync(int accountId);
    Task ApplyNCBankEntriesAsync(IReadOnlyList<NCBankEntry> entries);
    Task<List<NCBankTransactionData>> GetNCBankHistoryAsync(int accountId, int limit);
    Task<Dictionary<string, int>> GetNCBankFeeRatesAsync();
    Task SetNCBankFeeRateAsync(string category, int basisPoints);
    Task<List<NCBankSalaryData>> GetNCBankSalariesAsync();
    Task SetNCBankSalaryAsync(string departmentId, string jobId, int amount);
}

public sealed partial class ServerDbManager
{
    public Task<NCBankAccountOpenResult?> GetOrCreateNCCharacterBankAccountAsync(int profileId, long openingBalance)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.GetOrCreateNCCharacterBankAccountAsync(profileId, openingBalance));
    }

    public Task<NCBankAccountOpenResult> GetOrCreateNCDepartmentBankAccountAsync(string departmentId, long openingBalance)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.GetOrCreateNCDepartmentBankAccountAsync(departmentId, openingBalance));
    }

    public Task<NCBankAccountData?> GetNCBankAccountByNumberAsync(string number)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCBankAccountByNumberAsync(number));
    }

    public Task<NCBankAccountData?> GetNCBankAccountAsync(int accountId)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCBankAccountAsync(accountId));
    }

    public Task ApplyNCBankEntriesAsync(IReadOnlyList<NCBankEntry> entries)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.ApplyNCBankEntriesAsync(entries));
    }

    public Task<List<NCBankTransactionData>> GetNCBankHistoryAsync(int accountId, int limit)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCBankHistoryAsync(accountId, limit));
    }

    public Task<Dictionary<string, int>> GetNCBankFeeRatesAsync()
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCBankFeeRatesAsync());
    }

    public Task SetNCBankFeeRateAsync(string category, int basisPoints)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.SetNCBankFeeRateAsync(category, basisPoints));
    }

    public Task<List<NCBankSalaryData>> GetNCBankSalariesAsync()
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCBankSalariesAsync());
    }

    public Task SetNCBankSalaryAsync(string departmentId, string jobId, int amount)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.SetNCBankSalaryAsync(departmentId, jobId, amount));
    }
}

public abstract partial class ServerDbBase
{
    private const string NCBankNumberPrefix = "NC";

    public async Task<NCBankAccountOpenResult?> GetOrCreateNCCharacterBankAccountAsync(int profileId, long openingBalance)
    {
        await using var db = await GetDb();
        var existing = await db.DbContext.NCBankAccounts.AsNoTracking()
            .SingleOrDefaultAsync(account => account.ProfileId == profileId);
        if (existing != null)
            return new NCBankAccountOpenResult(ToData(existing), false);

        if (!await db.DbContext.Profile.AnyAsync(profile => profile.Id == profileId))
            return null;

        var created = await CreateNCBankAccountAsync(db.DbContext, profileId, null, openingBalance);
        return new NCBankAccountOpenResult(created, true);
    }

    public async Task<NCBankAccountOpenResult> GetOrCreateNCDepartmentBankAccountAsync(string departmentId, long openingBalance)
    {
        await using var db = await GetDb();
        var existing = await db.DbContext.NCBankAccounts.AsNoTracking()
            .SingleOrDefaultAsync(account => account.DepartmentId == departmentId);
        if (existing != null)
            return new NCBankAccountOpenResult(ToData(existing), false);

        var created = await CreateNCBankAccountAsync(db.DbContext, null, departmentId, openingBalance);
        return new NCBankAccountOpenResult(created, true);
    }

    private static async Task<NCBankAccountData> CreateNCBankAccountAsync(
        ServerDbContext context,
        int? profileId,
        string? departmentId,
        long openingBalance)
    {
        string number;
        do
        {
            number = $"{NCBankNumberPrefix}-{RandomNumberGenerator.GetInt32(100000, 1000000)}";
        }
        while (await context.NCBankAccounts.AnyAsync(account => account.Number == number));

        var now = DateTime.UtcNow;
        var account = new NCBankAccount
        {
            Number = number,
            ProfileId = profileId,
            DepartmentId = departmentId,
            Balance = openingBalance,
            CreatedAt = now,
        };
        context.NCBankAccounts.Add(account);
        await context.SaveChangesAsync();

        if (openingBalance != 0)
        {
            context.NCBankTransactions.Add(new NCBankTransaction
            {
                AccountId = account.Id,
                Kind = (byte) NCBankTransactionKind.OpeningBalance,
                Amount = openingBalance,
                CreatedAt = now,
            });
            await context.SaveChangesAsync();
        }

        return ToData(account);
    }

    public async Task<NCBankAccountData?> GetNCBankAccountByNumberAsync(string number)
    {
        var normalized = number.Trim().ToUpperInvariant();
        await using var db = await GetDb();
        var account = await db.DbContext.NCBankAccounts.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Number == normalized);
        return account == null ? null : ToData(account);
    }

    public async Task<NCBankAccountData?> GetNCBankAccountAsync(int accountId)
    {
        await using var db = await GetDb();
        var account = await db.DbContext.NCBankAccounts.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == accountId);
        return account == null ? null : ToData(account);
    }

    public async Task ApplyNCBankEntriesAsync(IReadOnlyList<NCBankEntry> entries)
    {
        if (entries.Count == 0)
            return;

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        var ids = entries.Select(entry => entry.AccountId).Distinct().ToList();
        var accounts = await db.DbContext.NCBankAccounts
            .Where(account => ids.Contains(account.Id))
            .ToDictionaryAsync(account => account.Id);

        foreach (var entry in entries)
        {
            // An account deleted with its character since the change was made simply drops the line.
            if (!accounts.TryGetValue(entry.AccountId, out var account))
                continue;

            account.Balance += entry.Amount;
            db.DbContext.NCBankTransactions.Add(new NCBankTransaction
            {
                AccountId = entry.AccountId,
                Kind = (byte) entry.Kind,
                Amount = entry.Amount,
                Fee = entry.Fee,
                Counterparty = entry.Counterparty,
                Note = entry.Note.Length > 200 ? entry.Note[..200] : entry.Note,
                CreatedAt = entry.CreatedAt,
            });
        }

        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task<List<NCBankTransactionData>> GetNCBankHistoryAsync(int accountId, int limit)
    {
        await using var db = await GetDb();
        var rows = await db.DbContext.NCBankTransactions.AsNoTracking()
            .Where(transaction => transaction.AccountId == accountId)
            .OrderByDescending(transaction => transaction.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync();
        return rows.Select(row => new NCBankTransactionData(
                row.Id,
                (NCBankTransactionKind) row.Kind,
                row.Amount,
                row.Fee,
                row.Counterparty,
                row.Note,
                row.CreatedAt))
            .ToList();
    }

    public async Task<Dictionary<string, int>> GetNCBankFeeRatesAsync()
    {
        await using var db = await GetDb();
        return await db.DbContext.NCBankFeeRates.AsNoTracking()
            .ToDictionaryAsync(rate => rate.Category, rate => rate.BasisPoints);
    }

    public async Task SetNCBankFeeRateAsync(string category, int basisPoints)
    {
        await using var db = await GetDb();
        var rate = await db.DbContext.NCBankFeeRates.SingleOrDefaultAsync(value => value.Category == category);
        if (rate == null)
            db.DbContext.NCBankFeeRates.Add(new NCBankFeeRate { Category = category, BasisPoints = basisPoints });
        else
            rate.BasisPoints = basisPoints;

        await db.DbContext.SaveChangesAsync();
    }

    public async Task<List<NCBankSalaryData>> GetNCBankSalariesAsync()
    {
        await using var db = await GetDb();
        return await db.DbContext.NCBankSalaries.AsNoTracking()
            .Select(salary => new NCBankSalaryData(salary.DepartmentId, salary.JobId, salary.Amount))
            .ToListAsync();
    }

    public async Task SetNCBankSalaryAsync(string departmentId, string jobId, int amount)
    {
        await using var db = await GetDb();
        var salary = await db.DbContext.NCBankSalaries
            .SingleOrDefaultAsync(value => value.DepartmentId == departmentId && value.JobId == jobId);
        if (salary == null)
            db.DbContext.NCBankSalaries.Add(new NCBankSalary { DepartmentId = departmentId, JobId = jobId, Amount = amount });
        else
            salary.Amount = amount;

        await db.DbContext.SaveChangesAsync();
    }

    private static NCBankAccountData ToData(NCBankAccount account)
    {
        return new NCBankAccountData(account.Id, account.Number, account.ProfileId, account.DepartmentId, account.Balance);
    }
}
