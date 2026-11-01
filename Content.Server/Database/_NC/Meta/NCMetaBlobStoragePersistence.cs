// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Meta.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>An expected admission refusal, never a durable success or a database circuit failure.</summary>
public sealed class NCMetaBlobQuotaException : Exception
{
    public NCMetaBlobQuotaException() : base("META physical storage admission limit reached.") { }
}

public abstract partial class ServerDbBase
{
    private static async Task<NCMetaBlobStorage> LockNCMetaBlobStorageAsync(ServerDbContext context)
    {
        // Both SQLite and PostgreSQL acquire their write/row lock before the counters
        // are read. The seeded singleton survives restarts; independent DB clients
        // cannot each allocate their own burst or exceed the physical ceiling.
        if (await context.NCMetaBlobStorageState.Where(value => value.Id == 1)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.Id, (byte) 1)) != 1)
            throw new InvalidOperationException("Missing META storage admission singleton.");
        return await context.NCMetaBlobStorageState.SingleAsync(value => value.Id == 1);
    }

    private static async Task<bool> BackfillNCMetaBlobStorageAsync(ServerDbContext context, NCMetaBlobStorage storage)
    {
        if (storage.BackfillComplete) return true;
        var cursor = storage.BackfillCursor;
        var page = await context.NCMetaBlobs.AsNoTracking().Where(value => value.Digest.CompareTo(cursor) > 0)
            .OrderBy(value => value.Digest).Select(value => new { value.Digest, value.ByteCount }).Take(256).ToArrayAsync();
        if (page.Any(value => value.ByteCount is < 0 or > NCMetaMaximumBlobBytes))
            throw new InvalidOperationException("Invalid legacy META blob allocation.");
        storage.AllocatedCount = checked(storage.AllocatedCount + page.Length);
        storage.AllocatedBytes = checked(storage.AllocatedBytes + page.Sum(value => (long) value.ByteCount));
        if (page.Length != 0) storage.BackfillCursor = page[^1].Digest;
        storage.BackfillComplete = page.Length < 256;
        if (storage.RefilledAtUtcTicks == 0)
        {
            storage.CountTokens = MetaBlobStorageBudget.CountBurst;
            storage.ByteTokens = MetaBlobStorageBudget.ByteBurst;
            storage.RefilledAtUtcTicks = DateTime.UtcNow.Ticks;
        }
        // No new inserts or GC may run during this keyset scan. Existing deduplicated
        // content remains available, including when a legacy store exceeds the cap.
        return storage.BackfillComplete;
    }
}
