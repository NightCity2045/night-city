// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Content.Server.Database;

public enum NCPropertyStatus : byte
{
    Active,
    InheritancePending,
    Archived,
    Invalid,
}

public enum NCBusinessStatus : byte
{
    Active,
    Suspended,
    InheritancePending,
    Closed,
    Invalid,
}

public enum NCOwnerType : byte
{
    Character,
    Organization,
    Business,
    System,
}

public enum NCOwnershipType : byte
{
    Direct,
    Inherited,
    Administrative,
}

[Table("nc_property")]
public sealed class NCProperty
{
    [Key]
    public Guid PropertyId { get; set; }

    [MaxLength(64)]
    public string PrototypeId { get; set; } = string.Empty;

    public Guid? MapEntityId { get; set; }

    [MaxLength(64)]
    public string PropertyType { get; set; } = string.Empty;

    public NCPropertyStatus Status { get; set; }
}

[Table("nc_property_ownership")]
public sealed class NCPropertyOwnership
{
    public Guid PropertyId { get; set; }
    public NCOwnerType OwnerType { get; set; }

    [MaxLength(64)]
    public string OwnerId { get; set; } = string.Empty;

    public int ShareBasisPoints { get; set; }
    public DateTime AcquiredAt { get; set; }
}

[Table("nc_business")]
public sealed class NCBusiness
{
    [Key]
    public Guid BusinessId { get; set; }

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(64)]
    public string BusinessType { get; set; } = string.Empty;

    public Guid? PropertyId { get; set; }
    public Guid BankAccountId { get; set; }
    public NCBusinessStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}

[Table("nc_business_ownership")]
public sealed class NCBusinessOwnership
{
    public Guid BusinessId { get; set; }
    public int OwnerProfileId { get; set; }
    public int ShareBasisPoints { get; set; }
    public NCOwnershipType OwnershipType { get; set; }
    public DateTime AcquiredAt { get; set; }
}
