// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Content.Server.Database;

public enum NCOrganizationStatus : byte
{
    Active,
    Archived,
    Invalid,
}

public enum NCEmploymentState : byte
{
    Active,
    SuspendedPaid,
    SuspendedUnpaid,
    Terminated,
    Invalid,
}

public enum NCEmploymentAction : byte
{
    Hire,
    Promote,
    Demote,
    Transfer,
    Suspend,
    Reinstate,
    Dismiss,
    AdministrativeChange,
}

[Table("nc_organization")]
public sealed class NCOrganization
{
    [Key]
    public Guid OrganizationId { get; set; }

    [MaxLength(64)]
    public string PrototypeId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    public Guid? DefaultEntryPositionId { get; set; }
    public Guid? BankAccountId { get; set; }
    public NCOrganizationStatus Status { get; set; }
}

[Table("nc_department")]
public sealed class NCDepartment
{
    [Key]
    public Guid DepartmentId { get; set; }

    public Guid OrganizationId { get; set; }

    [MaxLength(64)]
    public string PrototypeId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;
}

[Table("nc_position")]
public sealed class NCPosition
{
    [Key]
    public Guid PositionId { get; set; }

    public Guid OrganizationId { get; set; }
    public Guid? DepartmentId { get; set; }

    [MaxLength(64)]
    public string PrototypeId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    public int RankWeight { get; set; }
    public long BaseSalary { get; set; }
    public int PayIntervalSeconds { get; set; }
    public Guid? PayrollAccountId { get; set; }
    public bool IsLeadership { get; set; }
    public bool CanHire { get; set; }
    public bool CanPromote { get; set; }
    public bool CanDemote { get; set; }
    public bool CanTransfer { get; set; }
    public bool CanSuspend { get; set; }
    public bool CanDismiss { get; set; }
    public int? MaxPromotableRankWeight { get; set; }
}

[Table("nc_character_employment")]
public sealed class NCCharacterEmployment
{
    [Key]
    public int ProfileId { get; set; }

    public Guid OrganizationId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid PositionId { get; set; }
    public NCEmploymentState EmploymentState { get; set; }
    public DateTime HiredAt { get; set; }
    public int? HiredByProfileId { get; set; }
    public DateTime? LastPromotionAt { get; set; }
    public DateTime? SuspendedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public long Version { get; set; }
}

/// <summary>
/// Immutable employment history. Profile references are scalar snapshots so the record survives permadeath.
/// </summary>
[Table("nc_employment_history")]
public sealed class NCEmploymentHistory
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long EmploymentHistoryId { get; set; }

    public int ProfileId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? OldDepartmentId { get; set; }
    public Guid? NewDepartmentId { get; set; }
    public Guid? OldPositionId { get; set; }
    public Guid? NewPositionId { get; set; }
    public NCEmploymentAction Action { get; set; }
    public int? ActorProfileId { get; set; }
    public Guid? ActorAdminId { get; set; }

    [MaxLength(512)]
    public string Reason { get; set; } = string.Empty;

    public int? RoundId { get; set; }
    public DateTime Timestamp { get; set; }
    public Guid RequestId { get; set; }
}
