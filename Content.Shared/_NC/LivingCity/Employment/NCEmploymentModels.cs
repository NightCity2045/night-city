// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Employment;

[Serializable, NetSerializable]
public enum NCEmploymentStatus : byte
{
    Active,
    Suspended,
    Terminated,
}

/// <summary>
/// Persistent relation shared by player characters and city actors. Position policy remains data-driven by callers.
/// </summary>
public sealed record NCEmploymentSnapshot(
    NCEmploymentId EmploymentId,
    NCPartyRef Employee,
    NCPartyRef Employer,
    string PositionId,
    NCEmploymentStatus Status,
    NCAccountId EmployerAccountId,
    NCAccountId PayeeAccountId,
    long SalaryAmount,
    long PayrollIntervalTicks,
    NCActiveWorldTime? NextPayrollAt,
    NCActiveWorldTime StartedAt,
    NCActiveWorldTime ChangedAt,
    int DataVersion,
    Guid LastAppliedEventId);

/// <summary>
/// O(1) per-party guard changed atomically with an employment aggregate.
/// A suspended relation remains current; a terminated one releases the employee.
/// </summary>
public sealed record NCPartyEmploymentIndexSnapshot(
    NCPartyRef Employee,
    NCEmploymentId CurrentEmploymentId,
    NCActiveWorldTime ChangedAt,
    int DataVersion,
    Guid LastAppliedEventId);

