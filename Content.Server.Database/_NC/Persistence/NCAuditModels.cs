using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Content.Server.Database;

public enum NCCharacterLifecycleStatus : byte
{
    Alive,
    PermadeathPending,
    Finalizing,
}

/// <summary>
/// Durable lifecycle state used to defer destructive permadeath until round end or lobby exit.
/// </summary>
[Table("nc_character_lifecycle")]
public sealed class NCCharacterLifecycle
{
    [Key]
    public int ProfileId { get; set; }

    public NCCharacterLifecycleStatus Status { get; set; }
    public int? DeclaredRoundId { get; set; }
    public DateTime? DeclaredAt { get; set; }
    public Guid? DeclaredByAccountId { get; set; }
    public int? DeclaredByProfileId { get; set; }

    [MaxLength(512)]
    public string? Reason { get; set; }

    public Guid? RequestId { get; set; }
}

/// <summary>
/// Technical audit deliberately has no profile foreign keys and survives character deletion.
/// </summary>
[Table("nc_persistence_audit")]
public sealed class NCPersistenceAudit
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long AuditId { get; set; }

    public DateTime Timestamp { get; set; }
    public int? RoundId { get; set; }
    public Guid? ActorAccountId { get; set; }
    public int? ActorProfileId { get; set; }
    public int? TargetProfileId { get; set; }

    [MaxLength(64)]
    public string Action { get; set; } = string.Empty;

    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    [MaxLength(512)]
    public string Reason { get; set; } = string.Empty;

    public Guid RequestId { get; set; }
}

/// <summary>
/// Minimal tombstone retained after the character profile and personal progression are removed.
/// </summary>
[Table("nc_deleted_character_audit")]
public sealed class NCDeletedCharacterAudit
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long DeletedCharacterAuditId { get; set; }

    public int DeletedProfileId { get; set; }

    [MaxLength(128)]
    public string LastCharacterName { get; set; } = string.Empty;

    public Guid AccountId { get; set; }

    [MaxLength(512)]
    public string DeletionReason { get; set; } = string.Empty;

    public int? RoundId { get; set; }
    public DateTime DeletedAt { get; set; }
    public Guid RequestId { get; set; }
}

[Table("nc_character_license")]
public sealed class NCCharacterLicense
{
    public int ProfileId { get; set; }

    [MaxLength(64)]
    public string LicensePrototypeId { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int? IssuedByProfileId { get; set; }
    public Guid? IssuedByAdminId { get; set; }
}
