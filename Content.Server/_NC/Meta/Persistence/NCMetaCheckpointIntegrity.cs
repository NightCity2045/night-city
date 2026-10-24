// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using Content.Server.Database;

namespace Content.Server._NC.Meta.Persistence;

/// <summary>Distinct from an absent aggregate: quota and ownership readers must never substitute empty defaults.</summary>
public sealed class NCMetaSnapshotIntegrityException(string message) : InvalidOperationException(message);

public sealed record NCMetaCheckpointChunk(int Index, int Count, int Bytes, string Checksum);
public sealed record NCMetaCheckpointDescriptor(int Version, int Records, long Bytes, NCMetaCheckpointChunk[] Chunks);

public static class NCMetaCheckpointIntegrity
{
    public const string DescriptorType = "meta-aggregate-manifest";
    public const string SectionType = "meta-aggregates";
    public const int MaximumChunks = 4096;
    public const int MaximumRecords = 131072;
    public const int MaximumBytes = 96 * 1024 * 1024;
    public const int MaximumSectionBytes = 4 * 1024 * 1024;

    public static NCMetaCheckpointDescriptor Describe(IReadOnlyList<(string Payload, int Count)> sections) => new(1,
        sections.Sum(value => value.Count), sections.Sum(value => (long) Encoding.UTF8.GetByteCount(value.Payload)),
        sections.Select((value, index) => new NCMetaCheckpointChunk(index, value.Count,
            Encoding.UTF8.GetByteCount(value.Payload), NCMetaJournalIntegrity.HashPayload(value.Payload))).ToArray());

    public static bool CanValidate(NCMetaCheckpointDescriptor descriptor, IReadOnlyList<NCRoundSnapshotSectionData> sections)
    {
        if (descriptor is not { Version: 1, Records: >= 0 and <= MaximumRecords, Bytes: >= 0 and <= MaximumBytes,
                Chunks.Length: > 0 and <= MaximumChunks } || sections.Count != descriptor.Chunks.Length) return false;
        long bytes = 0;
        long records = 0;
        for (var index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            var chunk = descriptor.Chunks[index];
            var size = Encoding.UTF8.GetByteCount(section.Payload);
            if (chunk == null || chunk.Index != index || section.ChunkIndex != index || section.SectionType != SectionType ||
                section.OwnerType != "world" || section.OwnerId != "city" || section.FormatVersion != 2 ||
                size > MaximumSectionBytes || size != chunk.Bytes || chunk.Count != section.EntityCount || chunk.Count < 0 ||
                chunk.Checksum != section.Checksum || NCMetaJournalIntegrity.HashPayload(section.Payload) != chunk.Checksum) return false;
            bytes += size; records += chunk.Count;
        }
        return bytes == descriptor.Bytes && records == descriptor.Records;
    }
}
