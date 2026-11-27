// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>Street reputation of one character, shared by every fixer.</summary>
[Table("nc_street_reputation")]
public sealed class NCStreetReputation
{
    [Key]
    public int ProfileId { get; set; }

    public int Value { get; set; }
}

public abstract partial class ServerDbContext
{
    public DbSet<NCStreetReputation> NCStreetReputations => Set<NCStreetReputation>();

    private static void ConfigureNCFixerModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCStreetReputation>()
            .HasOne<Profile>()
            .WithOne()
            .HasForeignKey<NCStreetReputation>(reputation => reputation.ProfileId)
            .HasConstraintName("FK_nc_street_reputation_profile_profile_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
