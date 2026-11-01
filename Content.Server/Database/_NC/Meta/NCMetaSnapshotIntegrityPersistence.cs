// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCRoundSnapshotInventorySection(string SectionType, string OwnerType, string OwnerId,
    int ChunkIndex, int FormatVersion, int EntityCount, int PayloadBytes, string Checksum);
public sealed record NCRoundSnapshotInventory(NCRoundSnapshotManifestData Manifest, IReadOnlyList<NCRoundSnapshotInventorySection> Sections);

public static class NCRoundSnapshotInventoryIntegrity
{
    public const int MaximumSections = 16384;
    public const long MaximumBytes = 256L * 1024 * 1024;
    public static string Checksum(IEnumerable<NCRoundSnapshotInventorySection> sections) => NCMetaJournalIntegrity.HashPayload(
        string.Join('\n', sections.OrderBy(value => value.SectionType, StringComparer.Ordinal)
            .ThenBy(value => value.OwnerType, StringComparer.Ordinal).ThenBy(value => value.OwnerId, StringComparer.Ordinal)
            .ThenBy(value => value.ChunkIndex).Select(value => FormattableString.Invariant(
                $"{value.SectionType}\u001f{value.OwnerType}\u001f{value.OwnerId}\u001f{value.ChunkIndex}\u001f{value.FormatVersion}\u001f{value.EntityCount}\u001f{value.Checksum}"))));

    public static bool CanValidate(NCRoundSnapshotInventory inventory, NCRoundSnapshotManifestData selected)
    {
        if (inventory.Manifest != selected || inventory.Sections.Count != selected.SectionCount ||
            selected.SectionCount is < 0 or > MaximumSections || selected.PayloadBytes is < 0 or > MaximumBytes) return false;
        var identities = new HashSet<(string, string, string, int)>();
        long bytes = 0;
        foreach (var section in inventory.Sections)
        {
            if (section.SectionType.Length is < 1 or > 64 || section.OwnerType.Length is < 1 or > 64 ||
                section.OwnerId.Length is < 1 or > 128 || section.ChunkIndex < 0 || section.FormatVersion < 1 ||
                section.EntityCount < 0 || section.PayloadBytes < 0 || section.Checksum.Length != 64 ||
                !identities.Add((section.SectionType, section.OwnerType, section.OwnerId, section.ChunkIndex))) return false;
            bytes += section.PayloadBytes;
            if (bytes > MaximumBytes) return false;
        }
        return bytes == selected.PayloadBytes && Checksum(inventory.Sections) == selected.Checksum;
    }
}

public partial interface IServerDbManager
{
    Task<NCRoundSnapshotInventory?> GetNCRoundSnapshotInventoryAsync(Guid snapshotId);
}

public sealed partial class ServerDbManager
{
    public Task<NCRoundSnapshotInventory?> GetNCRoundSnapshotInventoryAsync(Guid snapshotId)
    {
        DbReadOpsMetric.Inc();
        return RunNCPersistentDbCommand(false, null, () => _db.GetNCRoundSnapshotInventoryAsync(snapshotId));
    }
}

public abstract partial class ServerDbBase
{
    /// <summary>Reads a bounded inventory of exactly one committed manifest, streaming payloads rather than retaining the world.</summary>
    public async Task<NCRoundSnapshotInventory?> GetNCRoundSnapshotInventoryAsync(Guid snapshotId)
    {
        if (snapshotId == Guid.Empty) return null;
        await using var db = await GetDb();
        var row = await db.DbContext.NCRoundSnapshots.AsNoTracking().SingleOrDefaultAsync(value =>
            value.SnapshotId == snapshotId && value.Status == NCRoundSnapshotStatus.Committed);
        if (row?.CommittedAt is not { } committed || row.SectionCount is < 0 or > NCRoundSnapshotInventoryIntegrity.MaximumSections ||
            row.PayloadBytes is < 0 or > NCRoundSnapshotInventoryIntegrity.MaximumBytes) return null;
        var manifest = new NCRoundSnapshotManifestData(row.SnapshotId, row.SessionEpoch, row.ActiveWorldTicks,
            row.SchemaVersion, row.MapId, row.MapRevision, row.MapHash, row.SectionCount, row.PayloadBytes,
            row.Checksum, row.CapturedAt, committed);
        var sections = new List<NCRoundSnapshotInventorySection>();
        long bytes = 0;
        await foreach (var section in db.DbContext.NCRoundSnapshotSections.AsNoTracking()
                           .Where(value => value.SnapshotId == snapshotId)
                           .OrderBy(value => value.SectionType).ThenBy(value => value.OwnerType).ThenBy(value => value.OwnerId)
                           .ThenBy(value => value.ChunkIndex).Take(NCRoundSnapshotInventoryIntegrity.MaximumSections + 1).AsAsyncEnumerable())
        {
            var size = Encoding.UTF8.GetByteCount(section.Payload);
            if (sections.Count >= NCRoundSnapshotInventoryIntegrity.MaximumSections) return null;
            bytes += size;
            if (bytes > NCRoundSnapshotInventoryIntegrity.MaximumBytes || NCMetaJournalIntegrity.HashPayload(section.Payload) != section.Checksum) return null;
            sections.Add(new(section.SectionType, section.OwnerType, section.OwnerId, section.ChunkIndex,
                section.FormatVersion, section.EntityCount, size, section.Checksum));
        }
        var inventory = new NCRoundSnapshotInventory(manifest, sections);
        return NCRoundSnapshotInventoryIntegrity.CanValidate(inventory, manifest) ? inventory : null;
    }
}
