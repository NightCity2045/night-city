// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Content.Shared._NC.Identity;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCOrganizationBankAccountData(string OrganizationPrototypeId, int Balance);

public sealed record NCOrganizationBankTransactionData(
    long Id,
    NCOrganizationBankTransactionType Type,
    int Amount,
    int BalanceAfter,
    int? ActorProfileId,
    string ActorName,
    string Reason,
    DateTime CreatedAt);

public enum NCOrganizationBudgetMutationResult : byte
{
    Success,
    InvalidRequest,
    InsufficientFunds,
    BalanceOverflow,
}

public sealed record NCOrganizationBudgetMutationData(
    NCOrganizationBudgetMutationResult Result,
    int OrganizationBalance);

public enum NCSalaryPaymentResult : byte
{
    Success,
    InvalidRequest,
    CharacterAccountNotFound,
    InsufficientOrganizationFunds,
    CharacterBalanceOverflow,
}

public sealed record NCSalaryPaymentData(
    NCSalaryPaymentResult Result,
    int OrganizationBalance,
    int? CharacterBalance);

public partial interface IServerDbManager
{
    Task<NCOrganizationBankAccountData?> GetOrCreateNCOrganizationBankAccountAsync(
        string organizationPrototypeId,
        int startingBalance);

    Task<IReadOnlyList<NCOrganizationBankTransactionData>> GetNCOrganizationBankTransactionsAsync(
        string organizationPrototypeId,
        int limit);

    Task<NCOrganizationBudgetMutationData> TryChangeNCOrganizationBudgetAsync(
        string organizationPrototypeId,
        int startingBalance,
        int delta,
        NCCharacterId actorCharacterId,
        string actorName,
        string reason);

    Task<NCSalaryPaymentData> TryPayNCSalaryAsync(
        NCCharacterId characterId,
        string organizationPrototypeId,
        int startingBalance,
        int salary,
        string jobPrototypeId);
}

public sealed partial class ServerDbManager
{
    public Task<NCOrganizationBankAccountData?> GetOrCreateNCOrganizationBankAccountAsync(
        string organizationPrototypeId,
        int startingBalance)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() =>
            _db.GetOrCreateNCOrganizationBankAccountAsync(organizationPrototypeId, startingBalance));
    }

    public Task<IReadOnlyList<NCOrganizationBankTransactionData>> GetNCOrganizationBankTransactionsAsync(
        string organizationPrototypeId,
        int limit)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCOrganizationBankTransactionsAsync(organizationPrototypeId, limit));
    }

    public Task<NCOrganizationBudgetMutationData> TryChangeNCOrganizationBudgetAsync(
        string organizationPrototypeId,
        int startingBalance,
        int delta,
        NCCharacterId actorCharacterId,
        string actorName,
        string reason)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TryChangeNCOrganizationBudgetAsync(
            organizationPrototypeId, startingBalance, delta, actorCharacterId, actorName, reason));
    }

    public Task<NCSalaryPaymentData> TryPayNCSalaryAsync(
        NCCharacterId characterId,
        string organizationPrototypeId,
        int startingBalance,
        int salary,
        string jobPrototypeId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() =>
            _db.TryPayNCSalaryAsync(
                characterId, organizationPrototypeId, startingBalance, salary, jobPrototypeId));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<NCOrganizationBankAccountData?> GetOrCreateNCOrganizationBankAccountAsync(
        string organizationPrototypeId,
        int startingBalance)
    {
        var organizationId = NormalizeOrganizationId(organizationPrototypeId);
        if (organizationId == null || startingBalance < 0)
            return null;

        await using var db = await GetDb();
        var account = await db.DbContext.NCOrganizationBankAccounts
            .SingleOrDefaultAsync(value => value.OrganizationPrototypeId == organizationId);
        if (account == null)
        {
            account = new NCOrganizationBankAccount
            {
                OrganizationPrototypeId = organizationId,
                Balance = startingBalance,
                UpdatedAt = DateTime.UtcNow,
            };
            db.DbContext.NCOrganizationBankAccounts.Add(account);
            await db.DbContext.SaveChangesAsync();
        }

        return ToData(account);
    }

    public async Task<IReadOnlyList<NCOrganizationBankTransactionData>> GetNCOrganizationBankTransactionsAsync(
        string organizationPrototypeId,
        int limit)
    {
        var organizationId = NormalizeOrganizationId(organizationPrototypeId);
        if (organizationId == null)
            return Array.Empty<NCOrganizationBankTransactionData>();

        await using var db = await GetDb();
        return await db.DbContext.NCOrganizationBankTransactions
            .AsNoTracking()
            .Where(value => value.OrganizationPrototypeId == organizationId)
            .OrderByDescending(value => value.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(value => new NCOrganizationBankTransactionData(
                value.Id,
                value.Type,
                value.Amount,
                value.BalanceAfter,
                value.ActorProfileId,
                value.ActorName,
                value.Reason,
                value.CreatedAt))
            .ToListAsync();
    }

    /// <summary>
    /// Changes one organization budget and appends its audit entry in the same transaction.
    /// Positive deltas are cash deposits; negative deltas are cash withdrawals.
    /// </summary>
    public async Task<NCOrganizationBudgetMutationData> TryChangeNCOrganizationBudgetAsync(
        string organizationPrototypeId,
        int startingBalance,
        int delta,
        NCCharacterId actorCharacterId,
        string actorName,
        string reason)
    {
        var organizationId = NormalizeOrganizationId(organizationPrototypeId);
        actorName = actorName.Trim();
        reason = reason.Trim();
        if (organizationId == null || startingBalance < 0 || delta is 0 or int.MinValue ||
            !actorCharacterId.IsValid || actorName.Length is < 1 or > 128 || reason.Length is < 1 or > 512)
        {
            return new(NCOrganizationBudgetMutationResult.InvalidRequest, 0);
        }

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var organization = await db.DbContext.NCOrganizationBankAccounts
            .SingleOrDefaultAsync(value => value.OrganizationPrototypeId == organizationId);
        if (organization == null)
        {
            organization = new NCOrganizationBankAccount
            {
                OrganizationPrototypeId = organizationId,
                Balance = startingBalance,
                UpdatedAt = DateTime.UtcNow,
            };
            db.DbContext.NCOrganizationBankAccounts.Add(organization);
        }

        var newBalance = (long) organization.Balance + delta;
        if (newBalance < 0)
            return new(NCOrganizationBudgetMutationResult.InsufficientFunds, organization.Balance);
        if (newBalance > int.MaxValue)
            return new(NCOrganizationBudgetMutationResult.BalanceOverflow, organization.Balance);

        var now = DateTime.UtcNow;
        organization.Balance = (int) newBalance;
        organization.UpdatedAt = now;
        db.DbContext.NCOrganizationBankTransactions.Add(new NCOrganizationBankTransaction
        {
            OrganizationPrototypeId = organizationId,
            Type = delta > 0
                ? NCOrganizationBankTransactionType.Deposit
                : NCOrganizationBankTransactionType.Withdrawal,
            Amount = Math.Abs(delta),
            BalanceAfter = organization.Balance,
            ActorProfileId = actorCharacterId.Value,
            ActorName = actorName,
            Reason = reason,
            CreatedAt = now,
        });

        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(NCOrganizationBudgetMutationResult.Success, organization.Balance);
    }

    /// <summary>
    /// Debits the organization and credits the character in one serializable database transaction.
    /// A failed payment changes neither account.
    /// </summary>
    public async Task<NCSalaryPaymentData> TryPayNCSalaryAsync(
        NCCharacterId characterId,
        string organizationPrototypeId,
        int startingBalance,
        int salary,
        string jobPrototypeId)
    {
        var organizationId = NormalizeOrganizationId(organizationPrototypeId);
        jobPrototypeId = jobPrototypeId.Trim();
        if (!characterId.IsValid || organizationId == null || startingBalance < 0 || salary <= 0 ||
            jobPrototypeId.Length is < 1 or > 64)
            return new(NCSalaryPaymentResult.InvalidRequest, 0, null);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var organization = await db.DbContext.NCOrganizationBankAccounts
            .SingleOrDefaultAsync(value => value.OrganizationPrototypeId == organizationId);
        if (organization == null)
        {
            organization = new NCOrganizationBankAccount
            {
                OrganizationPrototypeId = organizationId,
                Balance = startingBalance,
                UpdatedAt = DateTime.UtcNow,
            };
            db.DbContext.NCOrganizationBankAccounts.Add(organization);
        }

        var character = await db.DbContext.NCCharacterBankAccounts
            .SingleOrDefaultAsync(value => value.ProfileId == characterId.Value);
        if (character == null)
            return new(NCSalaryPaymentResult.CharacterAccountNotFound, organization.Balance, null);

        if (organization.Balance < salary)
            return new(NCSalaryPaymentResult.InsufficientOrganizationFunds, organization.Balance, character.Balance);

        var newCharacterBalance = (long) character.Balance + salary;
        if (newCharacterBalance > int.MaxValue)
            return new(NCSalaryPaymentResult.CharacterBalanceOverflow, organization.Balance, character.Balance);

        var now = DateTime.UtcNow;
        organization.Balance -= salary;
        organization.UpdatedAt = now;
        character.Balance = (int) newCharacterBalance;
        character.UpdatedAt = now;
        var characterName = await db.DbContext.Profile
            .Where(value => value.Id == characterId.Value)
            .Select(value => value.CharacterName)
            .SingleOrDefaultAsync() ?? $"Character #{characterId.Value}";
        db.DbContext.NCOrganizationBankTransactions.Add(new NCOrganizationBankTransaction
        {
            OrganizationPrototypeId = organizationId,
            Type = NCOrganizationBankTransactionType.Salary,
            Amount = salary,
            BalanceAfter = organization.Balance,
            ActorProfileId = characterId.Value,
            ActorName = characterName,
            Reason = jobPrototypeId,
            CreatedAt = now,
        });

        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(NCSalaryPaymentResult.Success, organization.Balance, character.Balance);
    }

    private static string? NormalizeOrganizationId(string organizationPrototypeId)
    {
        var normalized = organizationPrototypeId.Trim();
        return normalized.Length is > 0 and <= 64 ? normalized : null;
    }

    private static NCOrganizationBankAccountData ToData(NCOrganizationBankAccount account)
    {
        return new(account.OrganizationPrototypeId, account.Balance);
    }
}
