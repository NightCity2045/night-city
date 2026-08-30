// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Security.Cryptography;
using System.Threading.Tasks;
using Content.Shared._NC.Identity;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCBankCredentialData(
    NCCharacterId CharacterId,
    string AccountNumber,
    string Pin);

public partial interface IServerDbManager
{
    Task<NCBankCredentialData?> GetOrCreateNCBankCredentialAsync(
        NCCharacterId characterId,
        string accountPrefix,
        int pinDigits);

    Task<NCBankCredentialData?> GetNCBankCredentialAsync(NCCharacterId characterId);
    Task<NCBankCredentialData?> AuthenticateNCBankCredentialAsync(string accountNumber, string pin);
}

public sealed partial class ServerDbManager
{
    public Task<NCBankCredentialData?> GetOrCreateNCBankCredentialAsync(
        NCCharacterId characterId,
        string accountPrefix,
        int pinDigits)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.GetOrCreateNCBankCredentialAsync(
            characterId,
            accountPrefix,
            pinDigits));
    }

    public Task<NCBankCredentialData?> GetNCBankCredentialAsync(NCCharacterId characterId)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCBankCredentialAsync(characterId));
    }

    public Task<NCBankCredentialData?> AuthenticateNCBankCredentialAsync(string accountNumber, string pin)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.AuthenticateNCBankCredentialAsync(accountNumber, pin));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<NCBankCredentialData?> GetOrCreateNCBankCredentialAsync(
        NCCharacterId characterId,
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
            // NC: This historical column is deliberately inert. Financial authority lives in the common ledger.
            Balance = 0,
            UpdatedAt = DateTime.UtcNow,
        };

        db.DbContext.NCCharacterBankAccounts.Add(account);
        await db.DbContext.SaveChangesAsync();
        return ToData(account);
    }

    public async Task<NCBankCredentialData?> GetNCBankCredentialAsync(NCCharacterId characterId)
    {
        if (!characterId.IsValid)
            return null;

        await using var db = await GetDb();
        var account = await db.DbContext.NCCharacterBankAccounts
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == characterId.Value);
        return account == null ? null : ToData(account);
    }

    public async Task<NCBankCredentialData?> AuthenticateNCBankCredentialAsync(string accountNumber, string pin)
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

    private static NCBankCredentialData ToData(NCCharacterBankAccount account)
    {
        return new NCBankCredentialData(
            new NCCharacterId(account.ProfileId),
            account.AccountNumber,
            account.Pin);
    }
}
