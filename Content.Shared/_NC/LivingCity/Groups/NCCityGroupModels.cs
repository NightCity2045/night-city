// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Groups;

[Serializable, NetSerializable]
public enum NCCityGroupStatus : byte
{
    Active,
    Traveling,
    Engaged,
    Recovering,
    Disbanded,
    Archived,
}

[Serializable, NetSerializable]
public enum NCCityGroupAlertState : byte
{
    Unaware,
    Suspicious,
    Alerted,
    Combat,
}

[Serializable, NetSerializable]
public enum NCCityGroupFormationPolicy : byte
{
    Loose,
    Column,
    Wedge,
    Defensive,
}

/// <summary>
/// Current group membership. Runtime formation slots and EntityUid values never enter this record.
/// </summary>
public sealed record NCCityGroupMember(
    NCActorId ActorId,
    string Role,
    NCActiveWorldTime JoinedAt);

/// <summary>
/// Bounded membership history retained in the group snapshot. The event journal remains the complete history.
/// </summary>
public sealed record NCCityGroupMembershipHistoryEntry(
    NCActorId ActorId,
    string Role,
    NCActiveWorldTime JoinedAt,
    NCActiveWorldTime? LeftAt,
    Guid JoinedEventId,
    Guid? LeftEventId);

public sealed record NCCityGroupRoute(
    NCZoneId CurrentZone,
    NCZoneId DestinationZone,
    NCCityTravelMode TravelMode,
    NCActiveWorldTime? ExpectedArrivalAt);

/// <summary>
/// Strategic resource summary. Concrete vehicles, cargo lots and items stay in their own authoritative domains.
/// </summary>
public sealed record NCCityGroupResourceSummary(
    int TransportCapacity,
    int CargoUnits,
    int SupplyUnits,
    float Morale);

public sealed record NCCityGroupDirective(
    string Purpose,
    string CurrentOrder,
    string MissionReference,
    Guid? IncidentId);

/// <summary>
/// Persistent group aggregate used by strategic simulation. Physical formation and local AI state are runtime-only.
/// </summary>
public sealed record NCCityGroupSnapshot(
    NCGroupId GroupId,
    string StableLocalId,
    string DisplayName,
    string ProfileId,
    NCOrganizationId FactionId,
    NCActorId LeaderActorId,
    NCCityGroupMember[] Members,
    NCCityGroupMembershipHistoryEntry[] MembershipHistory,
    NCCityGroupDirective Directive,
    NCCityGroupRoute Route,
    NCCityGroupResourceSummary Resources,
    NCCityGroupFormationPolicy FormationPolicy,
    bool MaterializeTogether,
    NCCityGroupAlertState AlertState,
    NCCityGroupStatus Status,
    int DataVersion,
    Guid LastAppliedEventId);

/// <summary>
/// Per-actor membership index changed atomically with group aggregates. It is the O(1) guard for the invariant that
/// one actor belongs to at most one persistent group.
/// </summary>
public sealed record NCCityActorGroupMembershipSnapshot(
    NCActorId ActorId,
    NCGroupId GroupId,
    string Role,
    NCActiveWorldTime? JoinedAt,
    NCActiveWorldTime ChangedAt,
    int DataVersion,
    Guid LastAppliedEventId);
