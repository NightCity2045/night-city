// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Commerce;
using Content.Server.Database;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.Meta.Persistence;

public sealed record NCMetaQuotePreparation(int Slot, long PreviousSlotRevision, NCPersistentAggregateWrite PoolWrite)
{
    public string StorageKey => MetaQuotePoolPolicy.SlotKey(Slot);
}
public sealed record NCMetaQuoteRead(NCAggregateSnapshotData Row, MetaQuotePool? Pool);

public sealed partial class NCMetaPersistenceSystem
{
    public const string QuotePoolType = "meta.quote-pool";
    private static string QuoteType(string kind) => kind switch
    { "software" => "meta.software.quote", "hosting" => "meta.hosting-quote", _ => throw new ArgumentException("Invalid quote pool.", nameof(kind)) };

    private async Task<MetaQuotePool> ReadQuotePoolAsync(string kind)
    {
        _ = QuoteType(kind);
        var row = await ReadSnapshotAsync(QuotePoolType, kind);
        if (row == null) return MetaQuotePoolPolicy.Empty;
        var pool = row is { FormatVersion: 1, Payload.Length: <= 262144 } ? JsonSerializer.Deserialize<MetaQuotePool>(row.Payload, Json) : null;
        if (pool == null || pool.Revision != row.AggregateVersion || !MetaQuotePoolPolicy.Valid(pool) ||
            pool.Leases.Any(lease => !NCPartyRef.TryParse(lease.Buyer, out _)))
            throw new System.IO.InvalidDataException("Invalid durable quote pool.");
        return pool;
    }
    private static NCPersistentAggregateWrite QuotePoolWrite(string kind, MetaQuotePool previous, MetaQuotePool next) =>
        new(Guid.NewGuid(), "meta.quote-pool.changed", QuotePoolType, kind, previous.Revision, 1, JsonSerializer.Serialize(next, Json), "{}");

    public async Task<NCMetaQuotePreparation?> PrepareQuoteAsync(string kind, string buyer, Guid quoteId, long now, long expires)
    {
        if (!NCPartyRef.TryParse(buyer, out _)) return null;
        var previous = await ReadQuotePoolAsync(kind);
        var allocation = MetaQuotePoolPolicy.Reserve(previous, buyer, quoteId, now, expires);
        if (allocation == null) return null;
        var row = await ReadSnapshotAsync(QuoteType(kind), MetaQuotePoolPolicy.SlotKey(allocation.Slot));
        return new(allocation.Slot, row?.AggregateVersion ?? 0, QuotePoolWrite(kind, previous, allocation.Next));
    }

    public async Task<NCMetaQuoteRead?> ReadQuoteAsync(string kind, string buyer, Guid quoteId)
    {
        if (quoteId == Guid.Empty || !NCPartyRef.TryParse(buyer, out _)) return null;
        var pool = await ReadQuotePoolAsync(kind);
        var lease = pool.Leases.SingleOrDefault(lease => lease.QuoteId == quoteId);
        if (lease != null)
        {
            if (lease.Buyer != buyer) return null;
            var row = await ReadSnapshotAsync(QuoteType(kind), MetaQuotePoolPolicy.SlotKey(lease.Slot));
            return row == null ? null : new(row, pool);
        }
        // Existing immutable GUID-keyed releases remain readable, but no new GUID-keyed rows are created.
        var legacy = await ReadSnapshotAsync(QuoteType(kind), quoteId.ToString("D"));
        return legacy == null ? null : new(legacy, null);
    }

    public NCPersistentAggregateWrite? PrepareQuoteRelease(string kind, string buyer, Guid quoteId, long now, NCMetaQuoteRead read)
    {
        if (read.Pool == null) return null;
        var next = MetaQuotePoolPolicy.Release(read.Pool, buyer, quoteId, now);
        return next == null ? throw new System.IO.InvalidDataException("Quote slot ownership changed.") : QuotePoolWrite(kind, read.Pool, next);
    }
}
