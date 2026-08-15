// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Shared._NC.Identity;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>
/// Credential-free legacy bank projection used only by bounded migration and reconciliation jobs.
/// </summary>
public sealed record NCLegacyCharacterBankProjection(
    NCCharacterId CharacterId,
    string CharacterName,
    int Balance,
    DateTime UpdatedAt);

public sealed record NCLegacyOrganizationBankProjection(
    string OrganizationPrototypeId,
    int Balance,
    DateTime UpdatedAt);

public sealed record NCLegacyCharacterEmploymentProjection(
    NCCharacterId CharacterId,
    string CharacterName,
    string JobPrototypeId,
    NCEmploymentState State,
    DateTime StartedAt,
    DateTime UpdatedAt);

public partial interface IServerDbManager
{
    Task<IReadOnlyList<NCLegacyCharacterBankProjection>> GetNCLegacyCharacterBankPageAsync(
        int afterProfileId,
        int limit);

    Task<IReadOnlyList<NCLegacyOrganizationBankProjection>> GetNCLegacyOrganizationBankPageAsync(
        string? afterOrganizationPrototypeId,
        int limit);

    Task<IReadOnlyList<NCLegacyCharacterEmploymentProjection>> GetNCLegacyEmploymentPageAsync(
        int afterProfileId,
        int limit);
}

public sealed partial class ServerDbManager
{
    public Task<IReadOnlyList<NCLegacyCharacterBankProjection>> GetNCLegacyCharacterBankPageAsync(
        int afterProfileId,
        int limit)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCLegacyCharacterBankPageAsync(afterProfileId, limit));
    }

    public Task<IReadOnlyList<NCLegacyOrganizationBankProjection>> GetNCLegacyOrganizationBankPageAsync(
        string? afterOrganizationPrototypeId,
        int limit)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() =>
            _db.GetNCLegacyOrganizationBankPageAsync(afterOrganizationPrototypeId, limit));
    }

    public Task<IReadOnlyList<NCLegacyCharacterEmploymentProjection>> GetNCLegacyEmploymentPageAsync(
        int afterProfileId,
        int limit)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCLegacyEmploymentPageAsync(afterProfileId, limit));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<IReadOnlyList<NCLegacyCharacterBankProjection>> GetNCLegacyCharacterBankPageAsync(
        int afterProfileId,
        int limit)
    {
        await using var db = await GetDb();
        return await db.DbContext.NCCharacterBankAccounts
            .AsNoTracking()
            .Where(account => account.ProfileId > Math.Max(0, afterProfileId))
            .OrderBy(account => account.ProfileId)
            .Take(Math.Clamp(limit, 1, 256))
            .Join(
                db.DbContext.Profile.AsNoTracking(),
                account => account.ProfileId,
                profile => profile.Id,
                (account, profile) => new NCLegacyCharacterBankProjection(
                    new NCCharacterId(account.ProfileId),
                    profile.CharacterName,
                    account.Balance,
                    account.UpdatedAt))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<NCLegacyOrganizationBankProjection>> GetNCLegacyOrganizationBankPageAsync(
        string? afterOrganizationPrototypeId,
        int limit)
    {
        var after = string.IsNullOrWhiteSpace(afterOrganizationPrototypeId)
            ? null
            : afterOrganizationPrototypeId.Trim();
        await using var db = await GetDb();
        var query = db.DbContext.NCOrganizationBankAccounts.AsNoTracking();
        if (after != null)
            query = query.Where(account => string.Compare(account.OrganizationPrototypeId, after) > 0);

        return await query
            .OrderBy(account => account.OrganizationPrototypeId)
            .Take(Math.Clamp(limit, 1, 256))
            .Select(account => new NCLegacyOrganizationBankProjection(
                account.OrganizationPrototypeId,
                account.Balance,
                account.UpdatedAt))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<NCLegacyCharacterEmploymentProjection>> GetNCLegacyEmploymentPageAsync(
        int afterProfileId,
        int limit)
    {
        await using var db = await GetDb();
        return await db.DbContext.NCCharacterEmployment
            .AsNoTracking()
            .Where(employment => employment.ProfileId > Math.Max(0, afterProfileId))
            .OrderBy(employment => employment.ProfileId)
            .Take(Math.Clamp(limit, 1, 256))
            .Select(employment => new NCLegacyCharacterEmploymentProjection(
                new NCCharacterId(employment.ProfileId),
                employment.Profile.CharacterName,
                employment.JobPrototypeId,
                employment.State,
                employment.StartedAt,
                employment.UpdatedAt))
            .ToListAsync();
    }
}
