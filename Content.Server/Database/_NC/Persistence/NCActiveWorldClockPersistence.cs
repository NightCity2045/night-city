// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Shared._NC.Persistence.Time;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCActiveWorldClockData(
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime Time,
    bool RoundActive,
    int Version);

public partial interface IServerDbManager
{
    Task<NCActiveWorldClockData?> GetNCActiveWorldClockAsync();
    Task<NCActiveWorldClockData> StartNCActiveWorldSessionAsync();
    Task<NCActiveWorldClockData?> AdvanceNCActiveWorldTimeAsync(NCSessionEpoch epoch, TimeSpan elapsed);
    Task<NCActiveWorldClockData?> FreezeNCActiveWorldSessionAsync(NCSessionEpoch epoch);
}

public sealed partial class ServerDbManager
{
    public Task<NCActiveWorldClockData?> GetNCActiveWorldClockAsync()
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCActiveWorldClockAsync());
    }

    public Task<NCActiveWorldClockData> StartNCActiveWorldSessionAsync()
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.StartNCActiveWorldSessionAsync());
    }

    public Task<NCActiveWorldClockData?> AdvanceNCActiveWorldTimeAsync(NCSessionEpoch epoch, TimeSpan elapsed)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.AdvanceNCActiveWorldTimeAsync(epoch, elapsed));
    }

    public Task<NCActiveWorldClockData?> FreezeNCActiveWorldSessionAsync(NCSessionEpoch epoch)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.FreezeNCActiveWorldSessionAsync(epoch));
    }
}

public abstract partial class ServerDbBase
{
    private const byte NCActiveWorldClockId = 1;

    public async Task<NCActiveWorldClockData?> GetNCActiveWorldClockAsync()
    {
        await using var db = await GetDb();
        var record = await db.DbContext.NCActiveWorldClocks
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == NCActiveWorldClockId);
        return record == null ? null : ToData(record);
    }

    public async Task<NCActiveWorldClockData> StartNCActiveWorldSessionAsync()
    {
        await using var db = await GetDb();
        var record = await db.DbContext.NCActiveWorldClocks
            .SingleOrDefaultAsync(value => value.Id == NCActiveWorldClockId);
        var epoch = NCSessionEpoch.New();
        var now = DateTime.UtcNow;

        if (record == null)
        {
            record = new NCActiveWorldClockRecord
            {
                Id = NCActiveWorldClockId,
                SessionEpoch = epoch.Value,
                ActiveWorldTicks = 0,
                RoundActive = true,
                Version = 1,
                UpdatedAt = now,
            };
            db.DbContext.NCActiveWorldClocks.Add(record);
        }
        else
        {
            // A new epoch invalidates asynchronous work left by a clean stop or a crash.
            record.SessionEpoch = epoch.Value;
            record.RoundActive = true;
            record.Version++;
            record.UpdatedAt = now;
        }

        await db.DbContext.SaveChangesAsync();
        return ToData(record);
    }

    public async Task<NCActiveWorldClockData?> AdvanceNCActiveWorldTimeAsync(
        NCSessionEpoch epoch,
        TimeSpan elapsed)
    {
        if (!epoch.IsValid || elapsed <= TimeSpan.Zero)
            return null;

        await using var db = await GetDb();
        var ticks = elapsed.Ticks;
        var now = DateTime.UtcNow;
        var updated = await db.DbContext.NCActiveWorldClocks
            .Where(value =>
                value.Id == NCActiveWorldClockId &&
                value.SessionEpoch == epoch.Value &&
                value.RoundActive &&
                value.ActiveWorldTicks <= long.MaxValue - ticks)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.ActiveWorldTicks, value => value.ActiveWorldTicks + ticks)
                .SetProperty(value => value.Version, value => value.Version + 1)
                .SetProperty(value => value.UpdatedAt, now));
        if (updated != 1)
            return null;

        var record = await db.DbContext.NCActiveWorldClocks
            .AsNoTracking()
            .SingleAsync(value => value.Id == NCActiveWorldClockId);
        return ToData(record);
    }

    public async Task<NCActiveWorldClockData?> FreezeNCActiveWorldSessionAsync(NCSessionEpoch epoch)
    {
        if (!epoch.IsValid)
            return null;

        await using var db = await GetDb();
        var now = DateTime.UtcNow;
        var updated = await db.DbContext.NCActiveWorldClocks
            .Where(value =>
                value.Id == NCActiveWorldClockId &&
                value.SessionEpoch == epoch.Value &&
                value.RoundActive)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.RoundActive, false)
                .SetProperty(value => value.Version, value => value.Version + 1)
                .SetProperty(value => value.UpdatedAt, now));
        if (updated != 1)
            return null;

        var record = await db.DbContext.NCActiveWorldClocks
            .AsNoTracking()
            .SingleAsync(value => value.Id == NCActiveWorldClockId);
        return ToData(record);
    }

    private static NCActiveWorldClockData ToData(NCActiveWorldClockRecord record)
    {
        return new NCActiveWorldClockData(
            new NCSessionEpoch(record.SessionEpoch),
            new NCActiveWorldTime(record.ActiveWorldTicks),
            record.RoundActive,
            record.Version);
    }
}
