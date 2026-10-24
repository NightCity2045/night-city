// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

[Table("nc_meta_revision"), PrimaryKey(nameof(AggregateType), nameof(AggregateId), nameof(AggregateVersion)), Index(nameof(CreatedAt))]
public sealed class NCMetaAggregateRevision
{
    [MaxLength(64)] public string AggregateType { get; set; } = string.Empty;
    [MaxLength(128)] public string AggregateId { get; set; } = string.Empty;
    public long AggregateVersion { get; set; }
    public int FormatVersion { get; set; }
    public Guid SessionEpoch { get; set; }
    public long ActiveWorldTicks { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int ReferencePolicyVersion { get; set; }
}

[Table("nc_meta_blob_reference"), PrimaryKey(nameof(AggregateType), nameof(AggregateId), nameof(AggregateVersion), nameof(Digest)), Index(nameof(Digest))]
public sealed class NCMetaAggregateBlobReference
{
    [MaxLength(64)] public string AggregateType { get; set; } = string.Empty;
    [MaxLength(128)] public string AggregateId { get; set; } = string.Empty;
    public long AggregateVersion { get; set; }
    [MaxLength(64)] public string Digest { get; set; } = string.Empty;
}

[Table("nc_meta_checkpoint_blob_reference"), PrimaryKey(nameof(SnapshotId), nameof(Digest)), Index(nameof(Digest))]
public sealed class NCMetaCheckpointBlobReference
{
    public Guid SnapshotId { get; set; }
    [MaxLength(64)] public string Digest { get; set; } = string.Empty;
}

[Table("nc_meta_checkpoint_blob_backfill")]
public sealed class NCMetaCheckpointBlobBackfill
{
    [Key] public Guid SnapshotId { get; set; }
    public int PolicyVersion { get; set; }
    public int NextChunk { get; set; }
    [MaxLength(64)] public string DigestCursor { get; set; } = string.Empty;
    public bool Complete { get; set; }
}

[Table("nc_meta_blob_maintenance")]
public sealed class NCMetaBlobMaintenance
{
    [Key] public byte Id { get; set; }
    public int PolicyVersion { get; set; }
    [MaxLength(64)] public string AggregateTypeCursor { get; set; } = string.Empty;
    [MaxLength(128)] public string AggregateIdCursor { get; set; } = string.Empty;
    public bool AggregatesComplete { get; set; }
}

/// <summary>Global physical allocation and ingress accounting, changed in the blob insert/delete transaction.</summary>
[Table("nc_meta_blob_storage")]
public sealed class NCMetaBlobStorage
{
    [Key] public byte Id { get; set; }
    public long AllocatedCount { get; set; }
    public long AllocatedBytes { get; set; }
    [MaxLength(64)] public string BackfillCursor { get; set; } = string.Empty;
    public bool BackfillComplete { get; set; }
    public double CountTokens { get; set; }
    public double ByteTokens { get; set; }
    public long RefilledAtUtcTicks { get; set; }
}

public abstract partial class ServerDbContext
{
    public DbSet<NCMetaAggregateRevision> NCMetaAggregateRevisions => Set<NCMetaAggregateRevision>();
    public DbSet<NCMetaAggregateBlobReference> NCMetaAggregateBlobReferences => Set<NCMetaAggregateBlobReference>();
    public DbSet<NCMetaCheckpointBlobReference> NCMetaCheckpointBlobReferences => Set<NCMetaCheckpointBlobReference>();
    public DbSet<NCMetaCheckpointBlobBackfill> NCMetaCheckpointBlobBackfills => Set<NCMetaCheckpointBlobBackfill>();
    public DbSet<NCMetaBlobMaintenance> NCMetaBlobMaintenanceState => Set<NCMetaBlobMaintenance>();
    public DbSet<NCMetaBlobStorage> NCMetaBlobStorageState => Set<NCMetaBlobStorage>();

    private static void ConfigureNCMetaBlobModels(ModelBuilder modelBuilder)
    {
        // A seeded singleton lets both providers serialize admission and GC before reading counters.
        modelBuilder.Entity<NCMetaBlobStorage>().HasData(new NCMetaBlobStorage { Id = 1 });
        modelBuilder.Entity<NCAggregateSnapshot>().HasIndex(value => new
            { value.AggregateType, value.AggregateVersion, value.CreatedAt, value.AggregateId });
        modelBuilder.Entity<NCMetaAggregateRevision>().HasIndex(value => new
            { value.ReferencePolicyVersion, value.AggregateType, value.AggregateId, value.AggregateVersion });
        modelBuilder.Entity<NCMetaAggregateBlobReference>().HasOne<NCMetaAggregateRevision>().WithMany()
            .HasForeignKey(value => new { value.AggregateType, value.AggregateId, value.AggregateVersion }).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCMetaAggregateBlobReference>().HasOne<NCMetaBlob>().WithMany()
            .HasForeignKey(value => value.Digest).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<NCMetaCheckpointBlobReference>().HasOne<NCRoundSnapshot>().WithMany()
            .HasForeignKey(value => value.SnapshotId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCMetaCheckpointBlobReference>().HasOne<NCMetaBlob>().WithMany()
            .HasForeignKey(value => value.Digest).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<NCMetaCheckpointBlobBackfill>().HasOne<NCRoundSnapshot>().WithMany()
            .HasForeignKey(value => value.SnapshotId).OnDelete(DeleteBehavior.Cascade);
    }
}
