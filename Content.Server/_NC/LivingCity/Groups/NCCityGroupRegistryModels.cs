// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Groups;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Groups;

public enum NCCityGroupMutationStatus : byte
{
    Applied,
    Duplicate,
    NotFound,
    VersionConflict,
    ClockUnavailable,
    Rejected,
    StorageFailure,
}

public sealed record NCCityGroupRecord(
    NCCityGroupSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCCityActorGroupMembershipRecord(
    NCCityActorGroupMembershipSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCCityGroupPage(
    IReadOnlyList<NCCityGroupRecord> Records,
    NCGroupId? ContinuationGroupId,
    bool HasMore);

public sealed record NCCityGroupMutationResult(
    NCCityGroupMutationStatus Status,
    NCGroupId GroupId,
    IReadOnlyList<NCCityGroupRecord> Groups,
    NCCityActorGroupMembershipRecord? Membership,
    string? Error);

public sealed record NCCityGroupCreateRequest(
    Guid CommandId,
    Guid GroupScopeId,
    string StableLocalId,
    string DisplayName,
    ProtoId<NCCityGroupProfilePrototype> Profile,
    NCOrganizationId FactionId,
    NCActorId LeaderActorId,
    string LeaderRole,
    NCZoneId CurrentZone,
    string Purpose,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCCityGroupAddMemberRequest(
    Guid CommandId,
    NCGroupId GroupId,
    long ExpectedGroupVersion,
    NCActorId ActorId,
    string Role,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCCityGroupRemoveMemberRequest(
    Guid CommandId,
    NCGroupId GroupId,
    long ExpectedGroupVersion,
    NCActorId ActorId,
    NCActorId SuccessorLeaderActorId,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCCityGroupTransferMemberRequest(
    Guid CommandId,
    NCGroupId SourceGroupId,
    long ExpectedSourceVersion,
    NCGroupId DestinationGroupId,
    long ExpectedDestinationVersion,
    NCActorId ActorId,
    string DestinationRole,
    NCActorId SourceSuccessorLeaderActorId,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCCityGroupSetLeaderRequest(
    Guid CommandId,
    NCGroupId GroupId,
    long ExpectedGroupVersion,
    NCActorId LeaderActorId,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCCityGroupSetDirectiveRequest(
    Guid CommandId,
    NCGroupId GroupId,
    long ExpectedGroupVersion,
    string Purpose,
    string CurrentOrder,
    string MissionReference,
    Guid? IncidentId,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

/// <summary>
/// Main-thread notification for bounded group schedulers and projections.
/// </summary>
public sealed class NCCityGroupPersistedEvent(
    NCGroupId groupId,
    long aggregateVersion,
    NCCityGroupStatus status,
    NCZoneId currentZone,
    NCZoneId destinationZone,
    NCActiveWorldTime? expectedArrivalAt) : EntityEventArgs
{
    public NCGroupId GroupId { get; } = groupId;
    public long AggregateVersion { get; } = aggregateVersion;
    public NCCityGroupStatus Status { get; } = status;
    public NCZoneId CurrentZone { get; } = currentZone;
    public NCZoneId DestinationZone { get; } = destinationZone;
    public NCActiveWorldTime? ExpectedArrivalAt { get; } = expectedArrivalAt;
}
