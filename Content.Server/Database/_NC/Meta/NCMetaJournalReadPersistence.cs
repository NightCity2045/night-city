// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared._NC.Persistence.Time;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>Resource limits for one META proof page, independent of the aggregate's history length.</summary>
public static class NCMetaJournalReadLimits
{
    // SQL length counts characters on SQLite/Postgres. UTF-16/UTF-8 are bounded by two/four times this count.
    public const int PageCharacters = 256 * 1024;
    public const int EventCharacters = 8 * 1024 * 1024;
}

public partial interface IServerDbManager
{
    Task<IReadOnlyList<NCPersistentEventData>> GetNCMetaJournalPageAsync(string type, string id, long beforeVersion,
        int limit = NCMetaJournalIntegrity.MaximumPageEvents, CancellationToken cancellationToken = default);
}

public sealed partial class ServerDbManager
{
    public Task<IReadOnlyList<NCPersistentEventData>> GetNCMetaJournalPageAsync(string type, string id, long beforeVersion,
        int limit = NCMetaJournalIntegrity.MaximumPageEvents, CancellationToken cancellationToken = default) =>
        RunNCPersistentDbCommand(false, null, () => _db.GetNCMetaJournalPageAsync(type, id, beforeVersion, limit, cancellationToken));
}

public abstract partial class ServerDbBase
{
    private sealed class MetaJournalMetadata
    {
        [Column(nameof(EventId))] public Guid EventId { get; set; }
        [Column(nameof(EventType))] public string EventType { get; set; } = string.Empty;
        [Column(nameof(AggregateVersion))] public long AggregateVersion { get; set; }
        [Column(nameof(CorrelationId))] public Guid CorrelationId { get; set; }
        [Column(nameof(CausationId))] public Guid? CausationId { get; set; }
        [Column(nameof(SessionEpoch))] public Guid SessionEpoch { get; set; }
        [Column(nameof(ActiveWorldTicks))] public long ActiveWorldTicks { get; set; }
        [Column(nameof(CreatedAt))] public DateTime CreatedAt { get; set; }
        [Column(nameof(Characters))] public int Characters { get; set; }
        [Column(nameof(Bytes))] public int Bytes { get; set; }
    }

    public async Task<IReadOnlyList<NCPersistentEventData>> GetNCMetaJournalPageAsync(string type, string id, long beforeVersion,
        int limit = NCMetaJournalIntegrity.MaximumPageEvents, CancellationToken cancellationToken = default)
    {
        ValidateIdentityPart(type, nameof(type), 64);
        ValidateIdentityPart(id, nameof(id), 128);
        if (!type.StartsWith("meta.", StringComparison.Ordinal)) throw new ArgumentException("Only META journals support proof paging.", nameof(type));
        if (beforeVersion < 1) throw new ArgumentOutOfRangeException(nameof(beforeVersion));
        if (limit is < 1 or > NCMetaJournalIntegrity.MaximumPageEvents) throw new ArgumentOutOfRangeException(nameof(limit));
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await GetDb();
        // SQL character counts differ from UTF-16. Also project bytes: SQLite length(text) stops at an embedded NUL,
        // so a character-only guard would allow a corrupt row to materialize an arbitrarily larger suffix.
        var sqlite = db.DbContext is SqliteServerDbContext;
        if (!sqlite && db.DbContext is not PostgresServerDbContext) throw new NotSupportedException("Unsupported META journal provider.");
        var characterCount = sqlite ? "length(payload)" : "char_length(payload)";
        var byteCount = sqlite ? "length(CAST(payload AS BLOB))" : "octet_length(payload)";
        var metadataSql = $$"""
            SELECT event_id AS "EventId", event_type AS "EventType", aggregate_version AS "AggregateVersion",
                correlation_id AS "CorrelationId", causation_id AS "CausationId", session_epoch AS "SessionEpoch",
                active_world_ticks AS "ActiveWorldTicks", created_at AS "CreatedAt",
                {{characterCount}} AS "Characters", {{byteCount}} AS "Bytes"
            FROM nc_event_journal
            WHERE aggregate_type = {0} AND aggregate_id = {1} AND aggregate_version < {2}
            ORDER BY aggregate_version DESC LIMIT {3}
            """;
        // Fixed SQL structure and parameterized identities; no journal bodies are materialized by this query.
        var metadata = await db.DbContext.Database.SqlQueryRaw<MetaJournalMetadata>(metadataSql, type, id, beforeVersion, limit)
            .ToListAsync(cancellationToken);
        if (metadata.Count == 0) return [];
        var selected = 0;
        long characters = 0; long bytes = 0;
        foreach (var row in metadata)
        {
            if (row.Characters < 0 || row.Characters > NCMetaJournalReadLimits.EventCharacters ||
                row.Bytes < 0 || row.Bytes > (long) NCMetaJournalReadLimits.EventCharacters * 4)
                throw new InvalidDataException("META journal event exceeds the supported 8 Mi-character resource limit.");
            if (selected > 0 && (characters + row.Characters > NCMetaJournalReadLimits.PageCharacters ||
                bytes + row.Bytes > (long) NCMetaJournalReadLimits.PageCharacters * 4)) break;
            selected++;
            characters += row.Characters;
            bytes += row.Bytes;
            if (characters > NCMetaJournalReadLimits.PageCharacters || bytes > (long) NCMetaJournalReadLimits.PageCharacters * 4) break;
        }
        var parameters = new List<object> { type, id, beforeVersion };
        var allowed = new List<string>(selected);
        for (var index = 0; index < selected; index++)
        {
            var parameter = parameters.Count;
            allowed.Add($"(event_id = {{{parameter}}} AND {characterCount} = {{{parameter + 1}}} AND {byteCount} = {{{parameter + 2}}})");
            parameters.Add(metadata[index].EventId);
            parameters.Add(metadata[index].Characters);
            parameters.Add(metadata[index].Bytes);
        }
        // Repeat each exact character AND byte count in SQL; changing a row between queries cannot exceed the projected budget.
        var bodySql = "SELECT * FROM nc_event_journal WHERE aggregate_type = {0} AND aggregate_id = {1} AND aggregate_version < {2} AND (" +
            string.Join(" OR ", allowed) + ")";
        var page = await db.DbContext.NCEventJournal.FromSqlRaw(bodySql, parameters.ToArray()).AsNoTracking()
            .OrderByDescending(row => row.AggregateVersion).Select(row => new NCPersistentEventData(row.EventId, row.EventType,
                row.AggregateType, row.AggregateId, row.AggregateVersion, row.CorrelationId, row.CausationId,
                new NCSessionEpoch(row.SessionEpoch), new NCActiveWorldTime(row.ActiveWorldTicks), row.Payload, row.CreatedAt))
            .ToListAsync(cancellationToken);
        if (page.Count != selected) throw new InvalidDataException("META journal page changed while reading its bounded content.");
        for (var index = 0; index < page.Count; index++)
        {
            var row = page[index]; var expected = metadata[index];
            if (row.EventId != expected.EventId || row.EventType != expected.EventType || row.AggregateType != type ||
                row.AggregateId != id || row.AggregateVersion != expected.AggregateVersion ||
                row.CorrelationId != expected.CorrelationId || row.CausationId != expected.CausationId ||
                row.SessionEpoch.Value != expected.SessionEpoch || row.ActiveWorldTime.Ticks != expected.ActiveWorldTicks ||
                row.CreatedAt != expected.CreatedAt || row.Payload == null || row.Payload.Length > (long) expected.Characters * 2 ||
                Encoding.UTF8.GetByteCount(row.Payload) != expected.Bytes)
                throw new InvalidDataException("META journal page metadata changed or its character accounting is invalid.");
        }
        return page;
    }
}
