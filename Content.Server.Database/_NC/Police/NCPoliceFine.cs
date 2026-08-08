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

[Table("nc_police_fine")]
public sealed class NCPoliceFine
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }
    public int TargetProfileId { get; set; }
    [MaxLength(128)] public string TargetName { get; set; } = string.Empty;
    [MaxLength(128)] public string Article { get; set; } = string.Empty;
    [MaxLength(512)] public string Reason { get; set; } = string.Empty;
    public int Amount { get; set; }
    public NCPoliceFineStatus Status { get; set; }
    public int? IssuedByProfileId { get; set; }
    [MaxLength(128)] public string IssuedByName { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public List<NCPoliceFineEvent> Events { get; set; } = new();
}

/// <summary>Append-only audit trail for every fine lifecycle action.</summary>
[Table("nc_police_fine_event")]
public sealed class NCPoliceFineEvent
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }
    public long FineId { get; set; }
    public NCPoliceFine Fine { get; set; } = null!;
    public NCPoliceFineEventType EventType { get; set; }
    public NCPoliceFineStatus PreviousStatus { get; set; }
    public NCPoliceFineStatus NewStatus { get; set; }
    [MaxLength(512)] public string? Reason { get; set; }
    public int? ActorProfileId { get; set; }
    [MaxLength(128)] public string ActorName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public abstract partial class ServerDbContext
{
    public DbSet<NCPoliceFine> NCPoliceFines => Set<NCPoliceFine>();
    public DbSet<NCPoliceFineEvent> NCPoliceFineEvents => Set<NCPoliceFineEvent>();

    private static void ConfigureNCPoliceFineModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCPoliceFine>()
            .HasIndex(value => new { value.TargetProfileId, value.Status });
        modelBuilder.Entity<NCPoliceFineEvent>()
            .HasOne(value => value.Fine)
            .WithMany(value => value.Events)
            .HasForeignKey(value => value.FineId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCPoliceFineEvent>()
            .HasIndex(value => new { value.FineId, value.CreatedAt });
    }
}
