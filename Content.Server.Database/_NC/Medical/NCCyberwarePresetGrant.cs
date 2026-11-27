// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>A role cyberware preset that a character has already received; presets are granted once.</summary>
[Table("nc_cyberware_preset_grant")]
public sealed class NCCyberwarePresetGrant
{
    public int ProfileId { get; set; }

    [MaxLength(128)]
    public string PresetId { get; set; } = string.Empty;
}

public abstract partial class ServerDbContext
{
    public DbSet<NCCyberwarePresetGrant> NCCyberwarePresetGrants => Set<NCCyberwarePresetGrant>();

    private static void ConfigureNCCyberwarePresetModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCCyberwarePresetGrant>()
            .HasKey(grant => new { grant.ProfileId, grant.PresetId });

        modelBuilder.Entity<NCCyberwarePresetGrant>()
            .HasOne<Profile>()
            .WithMany()
            .HasForeignKey(grant => grant.ProfileId)
            .HasConstraintName("FK_nc_cyberware_preset_grant_profile_profile_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
