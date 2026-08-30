// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Groups;

public enum NCCityGroupPhysicalizationDirection : byte
{
    Materialize,
    Dematerialize,
}

public enum NCCityGroupPhysicalizationReason : byte
{
    PlayerRelevance,
    Incident,
    ServiceResponse,
    BudgetPressure,
    Admin,
}

public enum NCCityGroupPhysicalizationStatus : byte
{
    Queued,
    Loading,
    Retrying,
    MaterializingMembers,
    ApplyingFormation,
    DematerializingMembers,
    RollingBack,
    Materialized,
    AlreadyPhysical,
    Dematerialized,
    AlreadyStrategic,
    FailedRolledBack,
    PartialFailure,
    Rejected,
    Expired,
}

public sealed record NCCityGroupPhysicalizationRequest(
    Guid RequestId,
    NCGroupId GroupId,
    NCCityGroupPhysicalizationDirection Direction,
    NCCityGroupPhysicalizationReason Reason,
    int Priority,
    uint SelectionSeed,
    TimeSpan? MaximumWait,
    string Source);

public sealed record NCCityGroupPhysicalizationRequestState(
    Guid RequestId,
    NCGroupId GroupId,
    NCCityGroupPhysicalizationDirection Direction,
    NCCityGroupPhysicalizationReason Reason,
    NCCityGroupPhysicalizationStatus Status,
    int Priority,
    int Attempts,
    int TotalMembers,
    int CompletedMembers,
    NCActiveWorldTime RequestedAt,
    NCActiveWorldTime ExpiresAt,
    string? Error);

public sealed class NCCityGroupPhysicalizationChangedEvent(
    Guid requestId,
    NCGroupId groupId,
    NCCityGroupPhysicalizationDirection direction,
    int memberCount) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public NCGroupId GroupId { get; } = groupId;
    public NCCityGroupPhysicalizationDirection Direction { get; } = direction;
    public int MemberCount { get; } = memberCount;
}
