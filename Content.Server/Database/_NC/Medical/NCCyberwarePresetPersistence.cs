// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<bool> HasNCCyberwarePresetGrantAsync(int profileId, string presetId);
    Task AddNCCyberwarePresetGrantAsync(int profileId, string presetId);
}

public sealed partial class ServerDbManager
{
    public Task<bool> HasNCCyberwarePresetGrantAsync(int profileId, string presetId)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.HasNCCyberwarePresetGrantAsync(profileId, presetId));
    }

    public Task AddNCCyberwarePresetGrantAsync(int profileId, string presetId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.AddNCCyberwarePresetGrantAsync(profileId, presetId));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<bool> HasNCCyberwarePresetGrantAsync(int profileId, string presetId)
    {
        await using var db = await GetDb();
        return await db.DbContext.NCCyberwarePresetGrants.AsNoTracking()
            .AnyAsync(grant => grant.ProfileId == profileId && grant.PresetId == presetId);
    }

    public async Task AddNCCyberwarePresetGrantAsync(int profileId, string presetId)
    {
        await using var db = await GetDb();
        if (await db.DbContext.NCCyberwarePresetGrants
                .AnyAsync(grant => grant.ProfileId == profileId && grant.PresetId == presetId) ||
            !await db.DbContext.Profile.AnyAsync(profile => profile.Id == profileId))
        {
            return;
        }

        db.DbContext.NCCyberwarePresetGrants.Add(new NCCyberwarePresetGrant { ProfileId = profileId, PresetId = presetId });
        await db.DbContext.SaveChangesAsync();
    }
}
