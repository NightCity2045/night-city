// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using System;

namespace Content.Server.Database;

public sealed record NCOrganizationDefinition(
    Guid OrganizationId,
    string PrototypeId,
    string Name,
    Guid? DefaultEntryPositionId,
    bool HasPayrollAccount,
    long PayrollStartingBalance,
    string CurrencyPrototypeId);

public sealed record NCDepartmentDefinition(
    Guid DepartmentId,
    Guid OrganizationId,
    string PrototypeId,
    string Name);

public sealed record NCPositionDefinition(
    Guid PositionId,
    Guid OrganizationId,
    Guid? DepartmentId,
    string PrototypeId,
    string Name,
    int RankWeight,
    long BaseSalary,
    int PayIntervalSeconds,
    bool IsLeadership,
    bool CanHire,
    bool CanPromote,
    bool CanDemote,
    bool CanTransfer,
    bool CanSuspend,
    bool CanDismiss,
    int? MaxPromotableRankWeight);

public sealed record NCEmploymentMutation(
    int TargetProfileId,
    int? ActorProfileId,
    Guid? ActorAdminId,
    NCEmploymentAction Action,
    Guid OrganizationId,
    Guid? PositionId,
    string Reason,
    int? RoundId,
    bool PaidSuspension,
    long? ExpectedVersion,
    Guid RequestId);

public sealed record NCEmploymentResult(
    bool Success,
    string? Error,
    NCCharacterEmployment? Employment);
