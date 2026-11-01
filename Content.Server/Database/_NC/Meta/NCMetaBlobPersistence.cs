// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Content.Meta.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<string> StoreNCMetaBlobAsync(byte[] bytes);
    Task<byte[]?> ReadNCMetaBlobAsync(string digest);
}

public sealed partial class ServerDbManager
{
    public Task<string> StoreNCMetaBlobAsync(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > ServerDbBase.NCMetaMaximumBlobBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        // Copy before scheduling: the caller cannot mutate the bytes being committed on the DB thread.
        var immutable = bytes.ToArray();
        DbWriteOpsMetric.Inc();
        return RunNCPersistentDbCommand(true, null, () => _db.StoreNCMetaBlobAsync(immutable));
    }

    public Task<byte[]?> ReadNCMetaBlobAsync(string digest)
    {
        DbReadOpsMetric.Inc();
        return RunNCPersistentDbCommand(false, null, () => _db.ReadNCMetaBlobAsync(digest));
    }
}

public abstract partial class ServerDbBase
{
    public const int NCMetaMaximumBlobBytes = 1024 * 1024;

    public async Task<string> StoreNCMetaBlobAsync(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > NCMetaMaximumBlobBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var storage = await LockNCMetaBlobStorageAsync(db.DbContext);
        var now = DateTime.UtcNow;
        var admittedUntil = now.AddHours(NCMetaOrphanGraceHours).Ticks;
        var existing = await db.DbContext.NCMetaBlobs.AsNoTracking().SingleOrDefaultAsync(value => value.Digest == digest);
        if (existing != null)
        {
            ValidateMetaBlob(existing);
            // A successful deduplicated upload reserves the same pre-reference grace
            // as a new insert. GC uses this lock too, so the following aggregate
            // transaction cannot lose old unreferenced bytes immediately after ACK.
            if (existing.AdmittedUntilUtcTicks < admittedUntil)
                await db.DbContext.NCMetaBlobs.Where(value => value.Digest == digest)
                    .ExecuteUpdateAsync(update => update.SetProperty(value => value.AdmittedUntilUtcTicks, admittedUntil));
            await transaction.CommitAsync();
            return digest;
        }
        if (!await BackfillNCMetaBlobStorageAsync(db.DbContext, storage))
        {
            await db.DbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            throw new NCMetaBlobQuotaException();
        }
        var previous = new MetaBlobStorageAllocation(storage.AllocatedCount, storage.AllocatedBytes,
            storage.CountTokens, storage.ByteTokens, storage.RefilledAtUtcTicks);
        if (!MetaBlobStorageBudget.TryReserve(previous, bytes.Length, now.Ticks, out var reserved))
            throw new NCMetaBlobQuotaException();
        storage.AllocatedCount = reserved.Count;
        storage.AllocatedBytes = reserved.Bytes;
        storage.CountTokens = reserved.CountTokens;
        storage.ByteTokens = reserved.ByteTokens;
        storage.RefilledAtUtcTicks = reserved.RefilledAtUtcTicks;
        db.DbContext.NCMetaBlobs.Add(new NCMetaBlob
        {
            Digest = digest,
            ByteCount = bytes.Length,
            Bytes = bytes.ToArray(),
            CreatedAt = now,
            AdmittedUntilUtcTicks = admittedUntil,
        });
        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return digest;
    }

    public async Task<byte[]?> ReadNCMetaBlobAsync(string digest)
    {
        if (digest.Length != 64 || digest.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Invalid content digest.", nameof(digest));
        await using var db = await GetDb();
        var blob = await db.DbContext.NCMetaBlobs.AsNoTracking().SingleOrDefaultAsync(value => value.Digest == digest &&
            value.ByteCount >= 0 && value.ByteCount <= NCMetaMaximumBlobBytes && value.Bytes.Length <= NCMetaMaximumBlobBytes);
        if (blob == null)
            return null;
        ValidateMetaBlob(blob);
        return blob.Bytes;
    }

    private static void ValidateMetaBlob(NCMetaBlob blob)
    {
        if (blob.ByteCount is < 0 or > NCMetaMaximumBlobBytes || blob.Bytes.Length != blob.ByteCount ||
            Convert.ToHexStringLower(SHA256.HashData(blob.Bytes)) != blob.Digest)
            throw new InvalidOperationException("Corrupt META content blob.");
    }
}
