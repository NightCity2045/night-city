// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>The Trauma Team plan a character subscribes to.</summary>
[Table("nc_trauma_subscription")]
public sealed class NCTraumaSubscription
{
    [Key]
    public int ProfileId { get; set; }

    [MaxLength(64)]
    public string PlanId { get; set; } = string.Empty;
}

public abstract partial class ServerDbContext
{
    public DbSet<NCTraumaSubscription> NCTraumaSubscriptions => Set<NCTraumaSubscription>();

    private static void ConfigureNCDispatchModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCTraumaSubscription>()
            .HasOne<Profile>()
            .WithOne()
            .HasForeignKey<NCTraumaSubscription>(subscription => subscription.ProfileId)
            .HasConstraintName("FK_nc_trauma_subscription_profile_profile_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
