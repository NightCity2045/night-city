// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
using System;
using System.Threading.Tasks;
using Content.Shared.CCVar;

namespace Content.Server.Database;

public sealed partial class ServerDbManager
{
    /// <summary>
    /// Trusted integration fault injection into this manager's actual memory DB.
    /// It is absent from ordinary builds, the DB interface, guest APIs and diagnostics routes.
    /// The callback must use only synchronous EF operations, never call native systems.
    /// </summary>
    internal Task ApplyMetaIsolatedSqliteFaultAsync(Action<SqliteServerDbContext> fault)
    {
        if (Environment.GetEnvironmentVariable("NC_DIAGNOSTICS_ISOLATED") != "1" ||
            _res.UserData.RootDir != null || !_cfg.GetCVar(CCVars.DatabaseSynchronous) || _db is not ServerDbSqlite)
            throw new InvalidOperationException("Fault injection requires an isolated synchronous in-memory integration database.");
        ArgumentNullException.ThrowIfNull(fault);
        return _db.ApplyMetaIsolatedSqliteFaultAsync(fault);
    }
}

public abstract partial class ServerDbBase
{
    internal async Task ApplyMetaIsolatedSqliteFaultAsync(Action<SqliteServerDbContext> fault)
    {
        if (Environment.GetEnvironmentVariable("NC_DIAGNOSTICS_ISOLATED") != "1" || this is not ServerDbSqlite)
            throw new InvalidOperationException("Fault injection is unavailable for this database.");
        // Use the real provider's ready task and semaphore. Exposing its shared
        // connection directly would race ordinary DB operations and corrupt tests.
        await using var lease = await GetDb();
        if (lease.DbContext is not SqliteServerDbContext context)
            throw new InvalidOperationException("Fault injection requires SQLite.");
        fault(context);
    }
}
#endif
