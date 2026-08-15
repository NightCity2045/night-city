// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
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
    Task<NCActiveWorldClockData> StartNCActiveWorldSessionAsync(NCSessionEpoch epoch);
    Task<NCActiveWorldClockData?> AdvanceNCActiveWorldTimeAsync(
        NCSessionEpoch epoch,
        NCActiveWorldTime expectedTime,
        int expectedVersion,
        TimeSpan elapsed);
    Task<NCActiveWorldClockData?> CloseNCActiveWorldSessionAsync(
        NCSessionEpoch epoch,
        NCActiveWorldTime expectedTime,
        int expectedVersion,
        TimeSpan elapsed);
}

public sealed partial class ServerDbManager
{
    public Task<NCActiveWorldClockData?> GetNCActiveWorldClockAsync()
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCActiveWorldClockAsync());
    }

    public Task<NCActiveWorldClockData> StartNCActiveWorldSessionAsync(NCSessionEpoch epoch)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.StartNCActiveWorldSessionAsync(epoch));
    }

    public Task<NCActiveWorldClockData?> AdvanceNCActiveWorldTimeAsync(
        NCSessionEpoch epoch,
        NCActiveWorldTime expectedTime,
        int expectedVersion,
        TimeSpan elapsed)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.AdvanceNCActiveWorldTimeAsync(
            epoch,
            expectedTime,
            expectedVersion,
            elapsed));
    }

    public Task<NCActiveWorldClockData?> CloseNCActiveWorldSessionAsync(
        NCSessionEpoch epoch,
        NCActiveWorldTime expectedTime,
        int expectedVersion,
        TimeSpan elapsed)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.CloseNCActiveWorldSessionAsync(
            epoch,
            expectedTime,
            expectedVersion,
            elapsed));
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

    public async Task<NCActiveWorldClockData> StartNCActiveWorldSessionAsync(NCSessionEpoch epoch)
    {
        if (!epoch.IsValid)
            throw new ArgumentException("A valid active-world session epoch is required.", nameof(epoch));

        await using var db = await GetDb();
        var record = await db.DbContext.NCActiveWorldClocks
            .SingleOrDefaultAsync(value => value.Id == NCActiveWorldClockId);
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
        else if (record.RoundActive && record.SessionEpoch == epoch.Value)
        {
            // A lost response may retry round activation. Returning the same row makes that retry idempotent.
            return ToData(record);
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
        NCActiveWorldTime expectedTime,
        int expectedVersion,
        TimeSpan elapsed)
    {
        if (!epoch.IsValid || !expectedTime.IsValid || expectedVersion < 1 || elapsed <= TimeSpan.Zero)
            return null;

        NCActiveWorldTime targetTime;
        try
        {
            targetTime = expectedTime.Advance(elapsed);
        }
        catch (OverflowException)
        {
            return null;
        }

        await using var db = await GetDb();
        var now = DateTime.UtcNow;
        var updated = await db.DbContext.NCActiveWorldClocks
            .Where(value =>
                value.Id == NCActiveWorldClockId &&
                value.SessionEpoch == epoch.Value &&
                value.RoundActive &&
                value.ActiveWorldTicks == expectedTime.Ticks &&
                value.Version == expectedVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.ActiveWorldTicks, targetTime.Ticks)
                .SetProperty(value => value.Version, expectedVersion + 1)
                .SetProperty(value => value.UpdatedAt, now));

        var record = await db.DbContext.NCActiveWorldClocks
            .AsNoTracking()
            .SingleAsync(value => value.Id == NCActiveWorldClockId);
        if (updated == 1 || IsExpectedResult(
                record,
                epoch,
                targetTime,
                expectedVersion + 1,
                roundActive: true))
        {
            return ToData(record);
        }

        return null;
    }

    public async Task<NCActiveWorldClockData?> CloseNCActiveWorldSessionAsync(
        NCSessionEpoch epoch,
        NCActiveWorldTime expectedTime,
        int expectedVersion,
        TimeSpan elapsed)
    {
        if (!epoch.IsValid || !expectedTime.IsValid || expectedVersion < 1 || elapsed < TimeSpan.Zero)
            return null;

        NCActiveWorldTime targetTime;
        try
        {
            targetTime = expectedTime.Advance(elapsed);
        }
        catch (OverflowException)
        {
            return null;
        }

        await using var db = await GetDb();
        var now = DateTime.UtcNow;
        var updated = await db.DbContext.NCActiveWorldClocks
            .Where(value =>
                value.Id == NCActiveWorldClockId &&
                value.SessionEpoch == epoch.Value &&
                value.RoundActive &&
                value.ActiveWorldTicks == expectedTime.Ticks &&
                value.Version == expectedVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.ActiveWorldTicks, targetTime.Ticks)
                .SetProperty(value => value.RoundActive, false)
                .SetProperty(value => value.Version, expectedVersion + 1)
                .SetProperty(value => value.UpdatedAt, now));

        var record = await db.DbContext.NCActiveWorldClocks
            .AsNoTracking()
            .SingleAsync(value => value.Id == NCActiveWorldClockId);
        if (updated == 1 || IsExpectedResult(
                record,
                epoch,
                targetTime,
                expectedVersion + 1,
                roundActive: false))
        {
            return ToData(record);
        }

        return null;
    }

    private static bool IsExpectedResult(
        NCActiveWorldClockRecord record,
        NCSessionEpoch epoch,
        NCActiveWorldTime time,
        int version,
        bool roundActive)
    {
        return record.SessionEpoch == epoch.Value &&
               record.ActiveWorldTicks == time.Ticks &&
               record.Version == version &&
               record.RoundActive == roundActive;
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
