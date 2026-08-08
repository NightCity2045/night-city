// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
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

    partial void ConfigureNCModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCCharacterEmployment>()
            .HasOne(employment => employment.Profile)
            .WithOne(profile => profile.NCEmployment)
            .HasForeignKey<NCCharacterEmployment>(employment => employment.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCCharacterEmployment>()
            .HasIndex(employment => employment.JobPrototypeId);
    }
}
