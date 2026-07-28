using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<IReadOnlyList<NCPersistenceAudit>> GetNCPersistenceAuditAsync(int profileId, int limit);
}

public sealed partial class ServerDbManager
{
    public Task<IReadOnlyList<NCPersistenceAudit>> GetNCPersistenceAuditAsync(int profileId, int limit)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCPersistenceAuditAsync(profileId, limit));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<IReadOnlyList<NCPersistenceAudit>> GetNCPersistenceAuditAsync(
        int profileId,
        int limit)
    {
        await using var db = await GetDb();
        return await db.DbContext.NCPersistenceAudit
            .AsNoTracking()
            .Where(entry => entry.TargetProfileId == profileId)
            .OrderByDescending(entry => entry.Timestamp)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync();
    }
}
