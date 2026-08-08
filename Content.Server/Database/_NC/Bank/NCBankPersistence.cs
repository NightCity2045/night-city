// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Security.Cryptography;
using System.Threading.Tasks;
using Content.Shared._NC.Identity;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCBankAccountData(
    NCCharacterId CharacterId,
    string AccountNumber,
    string Pin,
    int Balance);

public partial interface IServerDbManager
{
    Task<NCBankAccountData?> GetOrCreateNCBankAccountAsync(
        NCCharacterId characterId,
        int startingBalance,
        string accountPrefix,
        int pinDigits);

    Task<NCBankAccountData?> GetNCBankAccountAsync(NCCharacterId characterId);
    Task<NCBankAccountData?> AuthenticateNCBankAccountAsync(string accountNumber, string pin);
    Task<NCBankAccountData?> TryAdjustNCBankBalanceAsync(NCCharacterId characterId, int delta);
}

public sealed partial class ServerDbManager
{
    public Task<NCBankAccountData?> GetOrCreateNCBankAccountAsync(
        NCCharacterId characterId,
        int startingBalance,
        string accountPrefix,
        int pinDigits)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.GetOrCreateNCBankAccountAsync(
            characterId,
            startingBalance,
            accountPrefix,
            pinDigits));
    }

    public Task<NCBankAccountData?> GetNCBankAccountAsync(NCCharacterId characterId)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCBankAccountAsync(characterId));
    }

    public Task<NCBankAccountData?> AuthenticateNCBankAccountAsync(string accountNumber, string pin)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.AuthenticateNCBankAccountAsync(accountNumber, pin));
    }

    public Task<NCBankAccountData?> TryAdjustNCBankBalanceAsync(NCCharacterId characterId, int delta)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TryAdjustNCBankBalanceAsync(characterId, delta));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<NCBankAccountData?> GetOrCreateNCBankAccountAsync(
        NCCharacterId characterId,
        int startingBalance,
        string accountPrefix,
        int pinDigits)
    {
        if (!characterId.IsValid)
            return null;

        await using var db = await GetDb();
        var existing = await db.DbContext.NCCharacterBankAccounts
            .SingleOrDefaultAsync(account => account.ProfileId == characterId.Value);
        if (existing != null)
            return ToData(existing);

        if (!await db.DbContext.Profile.AnyAsync(profile => profile.Id == characterId.Value))
            return null;

        var normalizedPrefix = accountPrefix.Trim().ToUpperInvariant();
        if (normalizedPrefix.Length == 0)
            return null;

        normalizedPrefix = normalizedPrefix[..Math.Min(normalizedPrefix.Length, 13)];
        var digits = Math.Clamp(pinDigits, 4, 8);
        var pinUpperBound = checked((int) Math.Pow(10, digits));
        string accountNumber;
        do
        {
            accountNumber = $"{normalizedPrefix}-{RandomNumberGenerator.GetInt32(100000, 1000000)}";
        }
        while (await db.DbContext.NCCharacterBankAccounts.AnyAsync(account => account.AccountNumber == accountNumber));

        var account = new NCCharacterBankAccount
        {
            ProfileId = characterId.Value,
            AccountNumber = accountNumber.ToUpperInvariant(),
            Pin = RandomNumberGenerator.GetInt32(pinUpperBound / 10, pinUpperBound).ToString(),
            Balance = Math.Max(0, startingBalance),
            UpdatedAt = DateTime.UtcNow,
        };

        db.DbContext.NCCharacterBankAccounts.Add(account);
        await db.DbContext.SaveChangesAsync();
        return ToData(account);
    }

    public async Task<NCBankAccountData?> GetNCBankAccountAsync(NCCharacterId characterId)
    {
        if (!characterId.IsValid)
            return null;

        await using var db = await GetDb();
        var account = await db.DbContext.NCCharacterBankAccounts
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == characterId.Value);
        return account == null ? null : ToData(account);
    }

    public async Task<NCBankAccountData?> AuthenticateNCBankAccountAsync(string accountNumber, string pin)
    {
        var normalizedAccount = accountNumber.Trim().ToUpperInvariant();
        var normalizedPin = pin.Trim();
        if (normalizedAccount.Length == 0 || normalizedPin.Length == 0)
            return null;

        await using var db = await GetDb();
        var account = await db.DbContext.NCCharacterBankAccounts
            .AsNoTracking()
            .SingleOrDefaultAsync(value =>
                value.AccountNumber == normalizedAccount &&
                value.Pin == normalizedPin);
        return account == null ? null : ToData(account);
    }

    public async Task<NCBankAccountData?> TryAdjustNCBankBalanceAsync(NCCharacterId characterId, int delta)
    {
        if (!characterId.IsValid || delta == 0)
            return null;

        await using var db = await GetDb();
        var account = await db.DbContext.NCCharacterBankAccounts
            .SingleOrDefaultAsync(value => value.ProfileId == characterId.Value);
        if (account == null)
            return null;

        var newBalance = (long) account.Balance + delta;
        if (newBalance < 0 || newBalance > int.MaxValue)
            return null;

        account.Balance = (int) newBalance;
        account.UpdatedAt = DateTime.UtcNow;
        await db.DbContext.SaveChangesAsync();
        return ToData(account);
    }

    private static NCBankAccountData ToData(NCCharacterBankAccount account)
    {
        return new NCBankAccountData(
            new NCCharacterId(account.ProfileId),
            account.AccountNumber,
            account.Pin,
            account.Balance);
    }
}
