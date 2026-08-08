// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>
/// Lifecycle of a concrete character job. Terminated records are retained as a minimal audit trail.
/// </summary>
public enum NCEmploymentState : byte
{
    Active,
    Suspended,
    Terminated,
}

public enum NCEmploymentEventType : byte
{
    EntrySelected,
    Hired,
    Terminated,
    AdministrativeChange,
    Promoted,
    Demoted,
    Transferred,
    Resigned,
}

/// <summary>
/// The current employment of one character. ProfileId is the identity and primary key,
/// so a second synthetic EmploymentId is intentionally unnecessary.
/// </summary>
[Table("nc_character_employment")]
public sealed class NCCharacterEmployment
{
    [Key]
    public int ProfileId { get; set; }

    [MaxLength(64)]
    public string JobPrototypeId { get; set; } = string.Empty;

    public NCEmploymentState State { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Profile Profile { get; set; } = null!;
    public List<NCEmploymentEvent> Events { get; set; } = new();
}

/// <summary>Append-only personnel history. Employment changes are corrected with another event, never erased.</summary>
[Table("nc_employment_event")]
public sealed class NCEmploymentEvent
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }
    public int ProfileId { get; set; }
    public NCCharacterEmployment Employment { get; set; } = null!;
    public NCEmploymentEventType EventType { get; set; }
    [MaxLength(64)] public string? PreviousJobPrototypeId { get; set; }
    [MaxLength(64)] public string? NewJobPrototypeId { get; set; }
    public NCEmploymentState? PreviousState { get; set; }
    public NCEmploymentState NewState { get; set; }
    [MaxLength(512)] public string Reason { get; set; } = string.Empty;
    public int? ActorProfileId { get; set; }
    [MaxLength(128)] public string ActorName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Detached character data used by the server preference cache.
/// </summary>
public sealed record NCCharacterEmploymentData(
    int ProfileId,
    int Slot,
    string? JobPrototypeId,
    NCEmploymentState? State);

public abstract partial class ServerDbContext
{
    public DbSet<NCCharacterEmployment> NCCharacterEmployment => Set<NCCharacterEmployment>();
    public DbSet<NCEmploymentEvent> NCEmploymentEvents => Set<NCEmploymentEvent>();

    partial void ConfigureNCModels(ModelBuilder modelBuilder)
    {
        ConfigureNCBankModels(modelBuilder);
        ConfigureNCPoliceModels(modelBuilder);

        modelBuilder.Entity<NCCharacterEmployment>()
            .HasOne(employment => employment.Profile)
            .WithOne(profile => profile.NCEmployment)
            .HasForeignKey<NCCharacterEmployment>(employment => employment.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCCharacterEmployment>()
            .HasIndex(employment => employment.JobPrototypeId);

        modelBuilder.Entity<NCEmploymentEvent>()
            .HasOne(value => value.Employment)
            .WithMany(value => value.Events)
            .HasForeignKey(value => value.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCEmploymentEvent>()
            .HasIndex(value => new { value.ProfileId, value.CreatedAt });
    }
}
