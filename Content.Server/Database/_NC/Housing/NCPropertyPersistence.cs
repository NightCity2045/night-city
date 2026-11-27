// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCPropertyData(string PropertyId, int? TenantProfileId, long PaidUntil, int KeyVersion);

public partial interface IServerDbManager
{
    Task<List<NCPropertyData>> GetNCPropertiesAsync();
    Task SetNCPropertyAsync(NCPropertyData property);
}

public sealed partial class ServerDbManager
{
    public Task<List<NCPropertyData>> GetNCPropertiesAsync()
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCPropertiesAsync());
    }

    public Task SetNCPropertyAsync(NCPropertyData property)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.SetNCPropertyAsync(property));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<List<NCPropertyData>> GetNCPropertiesAsync()
    {
        await using var db = await GetDb();
        return await db.DbContext.NCProperties.AsNoTracking()
            .Select(row => new NCPropertyData(row.PropertyId, row.TenantProfileId, row.PaidUntil, row.KeyVersion))
            .ToListAsync();
    }

    public async Task SetNCPropertyAsync(NCPropertyData property)
    {
        await using var db = await GetDb();
        var tenant = property.TenantProfileId;
        // A character deleted since the change was made leaves the property vacant.
        if (tenant is { } profileId && !await db.DbContext.Profile.AnyAsync(profile => profile.Id == profileId))
            tenant = null;

        var row = await db.DbContext.NCProperties.SingleOrDefaultAsync(value => value.PropertyId == property.PropertyId);
        if (row == null)
        {
            row = new NCProperty { PropertyId = property.PropertyId };
            db.DbContext.NCProperties.Add(row);
        }

        row.TenantProfileId = tenant;
        row.PaidUntil = property.PaidUntil;
        row.KeyVersion = property.KeyVersion;
        await db.DbContext.SaveChangesAsync();
    }
}
