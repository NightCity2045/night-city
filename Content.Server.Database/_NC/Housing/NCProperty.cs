// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>
/// The lease state of one rentable property, keyed by the id mappers give it. A row without a tenant is vacant;
/// the key version survives vacancy so cards of earlier leases never open the doors again.
/// </summary>
[Table("nc_property")]
public sealed class NCProperty
{
    [Key]
    [MaxLength(64)]
    public string PropertyId { get; set; } = string.Empty;

    /// <summary>The renting character; cleared when the character is deleted.</summary>
    public int? TenantProfileId { get; set; }

    /// <summary>Active-world time, in ticks, the rent is paid until.</summary>
    public long PaidUntil { get; set; }

    public int KeyVersion { get; set; }

    /// <summary>Asking price when the tenant offers the lease for resale.</summary>
    public long? ResalePrice { get; set; }

    /// <summary>Bank account the resale money goes to.</summary>
    [MaxLength(32)]
    public string? ResaleSeller { get; set; }

    /// <summary>The only bank account allowed to buy, if the tenant named one.</summary>
    [MaxLength(32)]
    public string? ResaleBuyer { get; set; }
}

public abstract partial class ServerDbContext
{
    public DbSet<NCProperty> NCProperties => Set<NCProperty>();

    private static void ConfigureNCHousingModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCProperty>()
            .HasOne<Profile>()
            .WithMany()
            .HasForeignKey(property => property.TenantProfileId)
            .HasConstraintName("FK_nc_property_profile_tenant_profile_id")
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<NCProperty>()
            .HasIndex(property => property.TenantProfileId);
    }
}
