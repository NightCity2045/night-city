// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Content.Shared.Database._NC.Police;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>
/// Current persistent NCPD operational state for a character profile.
/// </summary>
[Table("nc_police_record")]
public sealed class NCPoliceRecord
{
    [Key]
    public int ProfileId { get; set; }

    public NCPoliceOperationalStatus Status { get; set; }

    [MaxLength(256)]
    public string? Reason { get; set; }

    public int? UpdatedByProfileId { get; set; }

    [MaxLength(128)]
    public string UpdatedByName { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }

    public List<NCPoliceRecordEvent> Events { get; set; } = new();
}

/// <summary>
/// Append-only audit event. Corrections are new events and never remove the original action.
/// </summary>
[Table("nc_police_record_event")]
public sealed class NCPoliceRecordEvent
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public int ProfileId { get; set; }
    public NCPoliceRecord Record { get; set; } = null!;

    public NCPoliceRecordEventType EventType { get; set; }
    public NCPoliceOperationalStatus PreviousStatus { get; set; }
    public NCPoliceOperationalStatus NewStatus { get; set; }

    [MaxLength(256)]
    public string? Reason { get; set; }

    public int? ActorProfileId { get; set; }

    [MaxLength(128)]
    public string ActorName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

public abstract partial class ServerDbContext
{
    public DbSet<NCPoliceRecord> NCPoliceRecords => Set<NCPoliceRecord>();
    public DbSet<NCPoliceRecordEvent> NCPoliceRecordEvents => Set<NCPoliceRecordEvent>();

    private static void ConfigureNCPoliceModels(ModelBuilder modelBuilder)
    {
        ConfigureNCPoliceCaseModels(modelBuilder);

        modelBuilder.Entity<NCPoliceRecord>()
            .HasOne<Profile>()
            .WithOne()
            .HasForeignKey<NCPoliceRecord>(record => record.ProfileId)
            .HasConstraintName("FK_nc_police_record_profile_profile_id")
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCPoliceRecordEvent>()
            .HasOne(value => value.Record)
            .WithMany(record => record.Events)
            .HasForeignKey(value => value.ProfileId)
            .HasConstraintName("FK_nc_police_record_event_nc_police_record_profile_id")
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCPoliceRecordEvent>()
            .HasIndex(value => new { value.ProfileId, value.CreatedAt });
    }
}
