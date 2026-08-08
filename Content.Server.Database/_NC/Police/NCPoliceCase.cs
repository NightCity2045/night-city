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

[Table("nc_police_case")]
public sealed class NCPoliceCase
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(128)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string Summary { get; set; } = string.Empty;

    public NCPoliceCaseStatus Status { get; set; }
    public int? CreatedByProfileId { get; set; }

    [MaxLength(128)]
    public string CreatedByName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<NCPoliceCaseSubject> Subjects { get; set; } = new();
    public List<NCPoliceCaseEntry> Entries { get; set; } = new();
    public List<NCPoliceWarrant> Warrants { get; set; } = new();
}

/// <summary>
/// A historical character link. The name snapshot remains useful even if the profile is later removed.
/// </summary>
[Table("nc_police_case_subject")]
public sealed class NCPoliceCaseSubject
{
    public long CaseId { get; set; }
    public NCPoliceCase Case { get; set; } = null!;
    public int ProfileId { get; set; }

    [MaxLength(128)]
    public string CharacterName { get; set; } = string.Empty;

    public NCPoliceCaseSubjectRole Role { get; set; }
}

/// <summary>
/// Append-only case journal. Reports and system lifecycle entries are never edited in place.
/// </summary>
[Table("nc_police_case_entry")]
public sealed class NCPoliceCaseEntry
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }
    public long CaseId { get; set; }
    public NCPoliceCase Case { get; set; } = null!;
    public NCPoliceCaseEntryType EntryType { get; set; }

    [MaxLength(1024)]
    public string Text { get; set; } = string.Empty;

    public NCPoliceCaseStatus? PreviousStatus { get; set; }
    public NCPoliceCaseStatus? NewStatus { get; set; }
    public int? SubjectProfileId { get; set; }

    [MaxLength(128)]
    public string? SubjectName { get; set; }

    public NCPoliceCaseSubjectRole? SubjectRole { get; set; }

    public int? AuthorProfileId { get; set; }

    [MaxLength(128)]
    public string AuthorName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

[Table("nc_police_warrant")]
public sealed class NCPoliceWarrant
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }
    public long? CaseId { get; set; }
    public NCPoliceCase? Case { get; set; }
    public int TargetProfileId { get; set; }

    [MaxLength(128)]
    public string TargetName { get; set; } = string.Empty;

    public NCPoliceWarrantType Type { get; set; }
    public NCPoliceWarrantStatus Status { get; set; }

    [MaxLength(512)]
    public string Reason { get; set; } = string.Empty;

    public int? IssuedByProfileId { get; set; }

    [MaxLength(128)]
    public string IssuedByName { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; }
    public int? ResolvedByProfileId { get; set; }

    [MaxLength(128)]
    public string? ResolvedByName { get; set; }

    [MaxLength(512)]
    public string? ResolutionReason { get; set; }

    public DateTime? ResolvedAt { get; set; }
}

public abstract partial class ServerDbContext
{
    public DbSet<NCPoliceCase> NCPoliceCases => Set<NCPoliceCase>();
    public DbSet<NCPoliceCaseSubject> NCPoliceCaseSubjects => Set<NCPoliceCaseSubject>();
    public DbSet<NCPoliceCaseEntry> NCPoliceCaseEntries => Set<NCPoliceCaseEntry>();
    public DbSet<NCPoliceWarrant> NCPoliceWarrants => Set<NCPoliceWarrant>();

    private static void ConfigureNCPoliceCaseModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCPoliceCaseSubject>()
            .HasKey(value => new { value.CaseId, value.ProfileId });
        modelBuilder.Entity<NCPoliceCaseSubject>()
            .HasOne(value => value.Case)
            .WithMany(value => value.Subjects)
            .HasForeignKey(value => value.CaseId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCPoliceCaseSubject>()
            .HasIndex(value => value.ProfileId);

        modelBuilder.Entity<NCPoliceCaseEntry>()
            .HasOne(value => value.Case)
            .WithMany(value => value.Entries)
            .HasForeignKey(value => value.CaseId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCPoliceCaseEntry>()
            .HasIndex(value => new { value.CaseId, value.CreatedAt });

        modelBuilder.Entity<NCPoliceWarrant>()
            .HasOne(value => value.Case)
            .WithMany(value => value.Warrants)
            .HasForeignKey(value => value.CaseId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<NCPoliceWarrant>()
            .HasIndex(value => new { value.TargetProfileId, value.Status });
    }
}
