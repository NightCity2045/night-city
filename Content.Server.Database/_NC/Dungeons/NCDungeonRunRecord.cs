// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

[Table("nc_dungeon_run")]
public sealed class NCDungeonRunRecord
{
    [Key] public Guid RunId { get; set; }
    public Guid DungeonSlotId { get; set; }
    [MaxLength(64)] public string DefinitionId { get; set; } = string.Empty;
    [MaxLength(64)] public string TemplateId { get; set; } = string.Empty;
    public int TemplateRevision { get; set; }
    public Guid DeploymentId { get; set; }
    public int State { get; set; }
    public int Seed { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int Result { get; set; }
    [MaxLength(1024)] public string? Error { get; set; }
    public int Version { get; set; }
    public List<NCDungeonRunParticipant> Participants { get; set; } = [];
    public List<NCDungeonObjectiveRecord> Objectives { get; set; } = [];
    public List<NCDungeonExtractedItemRecord> ExtractedItems { get; set; } = [];
}

[Table("nc_dungeon_run_participant")]
public sealed class NCDungeonRunParticipant
{
    public Guid RunId { get; set; }
    public int ProfileId { get; set; }
    public NCDungeonRunRecord Run { get; set; } = null!;
}

[Table("nc_dungeon_objective")]
public sealed class NCDungeonObjectiveRecord
{
    public Guid RunId { get; set; }
    [MaxLength(64)] public string ObjectiveId { get; set; } = string.Empty;
    [MaxLength(1024)] public string State { get; set; } = string.Empty;
    public NCDungeonRunRecord Run { get; set; } = null!;
}

[Table("nc_dungeon_extracted_item")]
public sealed class NCDungeonExtractedItemRecord
{
    [Key] public Guid ItemId { get; set; }
    public Guid RunId { get; set; }
    public int? ExtractedByProfileId { get; set; }
    public string PersistentState { get; set; } = string.Empty;
    public DateTime ExtractedAt { get; set; }
    public NCDungeonRunRecord Run { get; set; } = null!;
}

public abstract partial class ServerDbContext
{
    public DbSet<NCDungeonRunRecord> NCDungeonRuns => Set<NCDungeonRunRecord>();
    public DbSet<NCDungeonRunParticipant> NCDungeonRunParticipants => Set<NCDungeonRunParticipant>();
    public DbSet<NCDungeonObjectiveRecord> NCDungeonObjectives => Set<NCDungeonObjectiveRecord>();
    public DbSet<NCDungeonExtractedItemRecord> NCDungeonExtractedItems => Set<NCDungeonExtractedItemRecord>();

    private static void ConfigureNCDungeonModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCDungeonRunRecord>().HasIndex(value => new { value.DungeonSlotId, value.State });
        modelBuilder.Entity<NCDungeonRunRecord>().HasIndex(value => value.DeploymentId).IsUnique();

        modelBuilder.Entity<NCDungeonRunParticipant>().HasKey(value => new { value.RunId, value.ProfileId });
        modelBuilder.Entity<NCDungeonRunParticipant>()
            .HasOne(value => value.Run).WithMany(value => value.Participants)
            .HasForeignKey(value => value.RunId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCDungeonRunParticipant>()
            .HasOne<Profile>().WithMany().HasForeignKey(value => value.ProfileId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCDungeonObjectiveRecord>().HasKey(value => new { value.RunId, value.ObjectiveId });
        modelBuilder.Entity<NCDungeonObjectiveRecord>()
            .HasOne(value => value.Run).WithMany(value => value.Objectives)
            .HasForeignKey(value => value.RunId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCDungeonExtractedItemRecord>()
            .HasOne(value => value.Run).WithMany(value => value.ExtractedItems)
            .HasForeignKey(value => value.RunId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCDungeonExtractedItemRecord>()
            .HasOne<Profile>().WithMany().HasForeignKey(value => value.ExtractedByProfileId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
