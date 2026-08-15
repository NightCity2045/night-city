// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>
/// The last validated semantic medical graph for one character profile. Payload is bounded YAML produced
/// by RobustToolbox DataDefinition serialization; it never contains EntityUid or NetEntity values.
/// </summary>
[Table("nc_medical_character_snapshot")]
public sealed class NCMedicalCharacterSnapshot
{
    public const int MaximumPayloadLength = 524_288;

    [Key]
    public int ProfileId { get; set; }

    public int SnapshotVersion { get; set; }
    public long Revision { get; set; }

    [MaxLength(MaximumPayloadLength)]
    public string Payload { get; set; } = string.Empty;

    public DateTime CapturedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Profile Profile { get; set; } = null!;
}

public abstract partial class ServerDbContext
{
    public DbSet<NCMedicalCharacterSnapshot> NCMedicalCharacterSnapshots => Set<NCMedicalCharacterSnapshot>();

    private static void ConfigureNCMedicalModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCMedicalCharacterSnapshot>()
            .HasOne(value => value.Profile)
            .WithOne()
            .HasForeignKey<NCMedicalCharacterSnapshot>(value => value.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCMedicalCharacterSnapshot>()
            .ToTable(table =>
            {
                table.HasCheckConstraint("CK_nc_medical_snapshot_revision", "revision > 0");
                table.HasCheckConstraint("CK_nc_medical_snapshot_version", "snapshot_version > 0");
                table.HasCheckConstraint(
                    "CK_nc_medical_snapshot_payload_length",
                    $"length(payload) <= {NCMedicalCharacterSnapshot.MaximumPayloadLength}");
            });
    }
}
