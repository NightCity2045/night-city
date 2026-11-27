// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<int> GetNCStreetReputationAsync(int profileId);
    Task SetNCStreetReputationAsync(int profileId, int value);
}

public sealed partial class ServerDbManager
{
    public Task<int> GetNCStreetReputationAsync(int profileId)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCStreetReputationAsync(profileId));
    }

    public Task SetNCStreetReputationAsync(int profileId, int value)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.SetNCStreetReputationAsync(profileId, value));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<int> GetNCStreetReputationAsync(int profileId)
    {
        await using var db = await GetDb();
        var row = await db.DbContext.NCStreetReputations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == profileId);
        return row?.Value ?? 0;
    }

    public async Task SetNCStreetReputationAsync(int profileId, int value)
    {
        await using var db = await GetDb();
        var row = await db.DbContext.NCStreetReputations.SingleOrDefaultAsync(r => r.ProfileId == profileId);
        if (row == null)
        {
            if (!await db.DbContext.Profile.AnyAsync(profile => profile.Id == profileId))
                return;

            db.DbContext.NCStreetReputations.Add(new NCStreetReputation { ProfileId = profileId, Value = value });
        }
        else
        {
            row.Value = value;
        }

        await db.DbContext.SaveChangesAsync();
    }
}
