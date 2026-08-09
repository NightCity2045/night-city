// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>
/// Persistent mutable state for one apartment/property zone. Its primary key is the zone GUID.
/// </summary>
[Table("nc_property_record")]
public sealed class NCPropertyRecord
{
    [Key]
    public Guid PropertyId { get; set; }

    public Guid BuildingId { get; set; }
    public int? OwnerProfileId { get; set; }
    public int Status { get; set; }
    public int PurchasePrice { get; set; }
    public DateTime? AcquiredAt { get; set; }
    public int AccessRevision { get; set; }
    [MaxLength(64)] public string RentPolicyId { get; set; } = string.Empty;
    [MaxLength(64)] public string AccessPolicyId { get; set; } = string.Empty;
    [MaxLength(64)] public string? CurrentTemplateId { get; set; }
    public int? CurrentTemplateRevision { get; set; }
    public Guid? ActiveDeploymentId { get; set; }
    public string InteriorState { get; set; } = string.Empty;
    public DateTime? NextRentDueAt { get; set; }
    public int Version { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<NCPropertyRoom> Rooms { get; set; } = [];
    public List<NCPropertyTenant> Tenants { get; set; } = [];
    public List<NCPropertyAccessGrant> AccessGrants { get; set; } = [];
    public List<NCPropertyEvent> Events { get; set; } = [];
}

/// <summary>
/// Last complete apartment entity graph. The row is replaced atomically only after serialization succeeds.
/// </summary>
[Table("nc_property_snapshot")]
public sealed class NCPropertySnapshot
{
    [Key]
    public Guid PropertyId { get; set; }

    [MaxLength(64)] public string TemplateId { get; set; } = string.Empty;
    public int TemplateRevision { get; set; }
    public int FormatVersion { get; set; }
    public string Payload { get; set; } = string.Empty;
    public int EntityCount { get; set; }
    public int ResidentEntityCount { get; set; }
    public DateTime UpdatedAt { get; set; }
}

[Table("nc_property_room")]
public sealed class NCPropertyRoom
{
    public Guid PropertyId { get; set; }
    public Guid RoomId { get; set; }
    public NCPropertyRecord Property { get; set; } = null!;
}

[Table("nc_property_tenant")]
public sealed class NCPropertyTenant
{
    public Guid PropertyId { get; set; }
    public int ProfileId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? ValidUntil { get; set; }
    public DateTime? RevokedAt { get; set; }
    public NCPropertyRecord Property { get; set; } = null!;
}

[Table("nc_property_access_grant")]
public sealed class NCPropertyAccessGrant
{
    [Key]
    public Guid GrantId { get; set; }
    public Guid PropertyId { get; set; }
    public int ProfileId { get; set; }
    public int Permissions { get; set; }
    public int AccessRevision { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime? ValidUntil { get; set; }
    public DateTime? RevokedAt { get; set; }
    public NCPropertyRecord Property { get; set; } = null!;
}

public enum NCPropertyEventType : byte
{
    Registered,
    Purchased,
    Rented,
    Sold,
    Vacated,
    Evicted,
    LegalRevocation,
    AccessRevisionChanged,
    InteriorRequested,
    InteriorActivated,
    AdministrativeChange,
    RentRenewed,
    RentDelinquent,
}

[Table("nc_property_event")]
public sealed class NCPropertyEvent
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }
    public Guid PropertyId { get; set; }
    public NCPropertyEventType EventType { get; set; }
    public int? SubjectProfileId { get; set; }
    [MaxLength(512)] public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public NCPropertyRecord Property { get; set; } = null!;
}

/// <summary>
/// Durable state machine record used to recover an interrupted multi-floor deployment idempotently.
/// </summary>
[Table("nc_zone_deployment_journal")]
public sealed class NCZoneDeploymentJournal
{
    [Key]
    public Guid DeploymentId { get; set; }
    public Guid TargetZoneId { get; set; }
    [MaxLength(64)] public string TemplateId { get; set; } = string.Empty;
    public int TemplateRevision { get; set; }
    public int Purpose { get; set; }
    public int State { get; set; }
    public int BaseZ { get; set; }
    public int? OwnerProfileId { get; set; }
    public Guid? DungeonRunId { get; set; }
    public int ExpectedZoneVersion { get; set; }
    public string BaselineState { get; set; } = string.Empty;
    [MaxLength(1024)] public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public abstract partial class ServerDbContext
{
    public DbSet<NCPropertyRecord> NCPropertyRecords => Set<NCPropertyRecord>();
    public DbSet<NCPropertyRoom> NCPropertyRooms => Set<NCPropertyRoom>();
    public DbSet<NCPropertyTenant> NCPropertyTenants => Set<NCPropertyTenant>();
    public DbSet<NCPropertyAccessGrant> NCPropertyAccessGrants => Set<NCPropertyAccessGrant>();
    public DbSet<NCPropertyEvent> NCPropertyEvents => Set<NCPropertyEvent>();
    public DbSet<NCPropertySnapshot> NCPropertySnapshots => Set<NCPropertySnapshot>();
    public DbSet<NCZoneDeploymentJournal> NCZoneDeploymentJournals => Set<NCZoneDeploymentJournal>();

    private static void ConfigureNCHousingModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCPropertyRecord>()
            .HasIndex(value => value.OwnerProfileId);
        modelBuilder.Entity<NCPropertyRecord>()
            .HasIndex(value => new { value.Status, value.BuildingId });
        modelBuilder.Entity<NCPropertyRecord>()
            .HasOne<Profile>()
            .WithMany()
            .HasForeignKey(value => value.OwnerProfileId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<NCPropertyRoom>()
            .HasKey(value => new { value.PropertyId, value.RoomId });
        modelBuilder.Entity<NCPropertyRoom>()
            .HasOne(value => value.Property)
            .WithMany(value => value.Rooms)
            .HasForeignKey(value => value.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCPropertyTenant>()
            .HasKey(value => new { value.PropertyId, value.ProfileId });
        modelBuilder.Entity<NCPropertyTenant>()
            .HasOne(value => value.Property)
            .WithMany(value => value.Tenants)
            .HasForeignKey(value => value.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCPropertyTenant>()
            .HasOne<Profile>()
            .WithMany()
            .HasForeignKey(value => value.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCPropertyAccessGrant>()
            .HasOne(value => value.Property)
            .WithMany(value => value.AccessGrants)
            .HasForeignKey(value => value.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCPropertyAccessGrant>()
            .HasOne<Profile>()
            .WithMany()
            .HasForeignKey(value => value.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCPropertyAccessGrant>()
            .HasIndex(value => new { value.PropertyId, value.ProfileId, value.RevokedAt });

        modelBuilder.Entity<NCPropertyEvent>()
            .HasOne(value => value.Property)
            .WithMany(value => value.Events)
            .HasForeignKey(value => value.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCPropertyEvent>()
            .HasIndex(value => new { value.PropertyId, value.CreatedAt });

        modelBuilder.Entity<NCPropertySnapshot>()
            .HasOne<NCPropertyRecord>()
            .WithOne()
            .HasForeignKey<NCPropertySnapshot>(value => value.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCZoneDeploymentJournal>()
            .HasIndex(value => new { value.TargetZoneId, value.State });
    }
}
