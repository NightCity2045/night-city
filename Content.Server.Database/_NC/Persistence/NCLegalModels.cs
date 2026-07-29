// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Content.Server.Database;

public enum NCLegalRecordStatus : byte
{
    Active,
    Expired,
    Revoked,
    Invalid,
}

public enum NCInheritanceAssetType : byte
{
    Property,
    Business,
}

public enum NCInheritanceStatus : byte
{
    Pending,
    Resolved,
    Escheated,
    Cancelled,
}

[Table("nc_character_license")]
public sealed class NCCharacterLicense
{
    public int ProfileId { get; set; }

    [MaxLength(64)]
    public string LicensePrototypeId { get; set; } = string.Empty;

    public NCLegalRecordStatus Status { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int? IssuedByProfileId { get; set; }
    public Guid? IssuedByAdminId { get; set; }
    public DateTime? RevokedAt { get; set; }

    [MaxLength(512)]
    public string Reason { get; set; } = string.Empty;
}

[Table("nc_character_document")]
public sealed class NCCharacterDocument
{
    [Key]
    public Guid DocumentId { get; set; }

    public int ProfileId { get; set; }

    [MaxLength(64)]
    public string DocumentPrototypeId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string SerialNumber { get; set; } = string.Empty;

    public NCLegalRecordStatus Status { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int? IssuedByProfileId { get; set; }
    public Guid? IssuedByAdminId { get; set; }
    public DateTime? RevokedAt { get; set; }

    [MaxLength(2048)]
    public string Payload { get; set; } = string.Empty;

    [MaxLength(512)]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Durable estate record that survives deletion of the deceased profile.
/// </summary>
[Table("nc_inheritance_case")]
public sealed class NCInheritanceCase
{
    [Key]
    public Guid InheritanceCaseId { get; set; }

    public NCInheritanceAssetType AssetType { get; set; }
    public Guid AssetId { get; set; }
    public int DeceasedProfileId { get; set; }
    public int ShareBasisPoints { get; set; }
    public NCInheritanceStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public NCOwnerType? ResolvedOwnerType { get; set; }

    [MaxLength(64)]
    public string? ResolvedOwnerId { get; set; }

    public Guid? ResolvedByAccountId { get; set; }

    [MaxLength(512)]
    public string Reason { get; set; } = string.Empty;
}
