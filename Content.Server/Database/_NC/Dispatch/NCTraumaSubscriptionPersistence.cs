// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<string?> GetNCTraumaSubscriptionAsync(int profileId);

    /// <summary>Stores the character's plan; null ends the subscription.</summary>
    Task SetNCTraumaSubscriptionAsync(int profileId, string? planId);
}

public sealed partial class ServerDbManager
{
    public Task<string?> GetNCTraumaSubscriptionAsync(int profileId)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCTraumaSubscriptionAsync(profileId));
    }

    public Task SetNCTraumaSubscriptionAsync(int profileId, string? planId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.SetNCTraumaSubscriptionAsync(profileId, planId));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<string?> GetNCTraumaSubscriptionAsync(int profileId)
    {
        await using var db = await GetDb();
        var row = await db.DbContext.NCTraumaSubscriptions.AsNoTracking()
            .SingleOrDefaultAsync(subscription => subscription.ProfileId == profileId);
        return row?.PlanId;
    }

    public async Task SetNCTraumaSubscriptionAsync(int profileId, string? planId)
    {
        await using var db = await GetDb();
        var row = await db.DbContext.NCTraumaSubscriptions.SingleOrDefaultAsync(s => s.ProfileId == profileId);
        if (planId == null)
        {
            if (row == null)
                return;

            db.DbContext.NCTraumaSubscriptions.Remove(row);
        }
        else if (row == null)
        {
            if (!await db.DbContext.Profile.AnyAsync(profile => profile.Id == profileId))
                return;

            db.DbContext.NCTraumaSubscriptions.Add(new NCTraumaSubscription { ProfileId = profileId, PlanId = planId });
        }
        else
        {
            row.PlanId = planId;
        }

        await db.DbContext.SaveChangesAsync();
    }
}
