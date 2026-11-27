// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCPropertyData(
    string PropertyId,
    int? TenantProfileId,
    long PaidUntil,
    int KeyVersion,
    long? ResalePrice = null,
    string? ResaleSeller = null,
    string? ResaleBuyer = null,
    bool IsHome = false);

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
            .Select(row => new NCPropertyData(
                row.PropertyId,
                row.TenantProfileId,
                row.PaidUntil,
                row.KeyVersion,
                row.ResalePrice,
                row.ResaleSeller,
                row.ResaleBuyer,
                row.IsHome))
            .ToListAsync();
    }

    public async Task SetNCPropertyAsync(NCPropertyData property)
    {
        await using var db = await GetDb();
        var tenant = property.TenantProfileId;
        // A character deleted since the change was made leaves the property vacant.
        var resale = property;
        if (tenant is { } profileId && !await db.DbContext.Profile.AnyAsync(profile => profile.Id == profileId))
        {
            tenant = null;
            resale = property with { ResalePrice = null, ResaleSeller = null, ResaleBuyer = null, IsHome = false };
        }

        var row = await db.DbContext.NCProperties.SingleOrDefaultAsync(value => value.PropertyId == property.PropertyId);
        if (row == null)
        {
            row = new NCProperty { PropertyId = property.PropertyId };
            db.DbContext.NCProperties.Add(row);
        }

        row.TenantProfileId = tenant;
        row.PaidUntil = property.PaidUntil;
        row.KeyVersion = property.KeyVersion;
        row.ResalePrice = resale.ResalePrice;
        row.ResaleSeller = resale.ResaleSeller;
        row.ResaleBuyer = resale.ResaleBuyer;
        row.IsHome = resale.IsHome;
        await db.DbContext.SaveChangesAsync();
    }
}
