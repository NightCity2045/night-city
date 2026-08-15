// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.GameObjects;

namespace Content.Server._NC.LivingCity.Employment;

public enum NCEmploymentMutationStatus : byte
{
    Applied,
    Duplicate,
    NotFound,
    VersionConflict,
    ClockUnavailable,
    Rejected,
    StorageFailure,
}

public sealed record NCEmploymentRecord(
    NCEmploymentSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCPartyEmploymentIndexRecord(
    NCPartyEmploymentIndexSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCHirePartyRequest(
    Guid CommandId,
    NCEmploymentId EmploymentId,
    NCPartyRef Employee,
    NCPartyRef Employer,
    string PositionId,
    long ExpectedEmployeeIndexVersion,
    NCAccountId EmployerAccountId,
    NCAccountId PayeeAccountId,
    long SalaryAmount,
    long PayrollIntervalTicks,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCChangeEmploymentRequest(
    Guid CommandId,
    NCEmploymentId EmploymentId,
    long ExpectedEmploymentVersion,
    long ExpectedEmployeeIndexVersion,
    string PositionId,
    NCEmploymentStatus Status,
    NCAccountId EmployerAccountId,
    NCAccountId PayeeAccountId,
    long SalaryAmount,
    long PayrollIntervalTicks,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCEmploymentMutationResult(
    NCEmploymentMutationStatus Status,
    NCEmploymentRecord? Employment,
    NCPartyEmploymentIndexRecord? EmployeeIndex,
    string? Error);

public sealed class NCEmploymentPersistedEvent(
    NCEmploymentId employmentId,
    NCPartyRef employee,
    NCPartyRef employer,
    long aggregateVersion,
    NCEmploymentStatus status) : EntityEventArgs
{
    public NCEmploymentId EmploymentId { get; } = employmentId;
    public NCPartyRef Employee { get; } = employee;
    public NCPartyRef Employer { get; } = employer;
    public long AggregateVersion { get; } = aggregateVersion;
    public NCEmploymentStatus Status { get; } = status;
}

