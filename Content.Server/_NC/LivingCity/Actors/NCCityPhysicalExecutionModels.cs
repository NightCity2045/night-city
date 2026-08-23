// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Actors;

public enum NCCityPhysicalExecutionState : byte
{
    Idle,
    LoadingIntent,
    Planning,
    Moving,
    TraversingPortal,
    Replanning,
    Committing,
    Blocked,
    Suspended,
    Failed,
}

[Flags]
public enum NCCityPhysicalInterruptFlags : ushort
{
    None = 0,
    Combat = 1 << 0,
    Conversation = 1 << 1,
    Detained = 1 << 2,
    Medical = 1 << 3,
    Scripted = 1 << 4,
    Police = 1 << 5,
    Work = 1 << 6,
}

/// <summary>
/// One bounded diagnostic transition. Sequence is local to the physical goal and makes traces deterministic.
/// </summary>
public readonly record struct NCCityPhysicalExecutionTraceEntry(
    long Sequence,
    NCActiveWorldTime At,
    NCCityPhysicalExecutionState State,
    string Event,
    int WaypointIndex,
    NCCityNavigationWaypointKind? WaypointKind,
    string Detail);

/// <summary>
/// Immutable F7-facing view; it contains only bounded scalar runtime data.
/// </summary>
public sealed record NCCityPhysicalExecutionSnapshot(
    NCActorId ActorId,
    EntityUid EntityUid,
    Guid GoalId,
    long ActorVersion,
    NCCityPhysicalExecutionState State,
    NCCityPhysicalMode Mode,
    NCCityPhysicalBehaviorOwner BehaviorOwner,
    NCCityPhysicalInterruptFlags Interrupts,
    NCZoneId DestinationZone,
    NCMapCoordinates Destination,
    string Activity,
    string Goal,
    int WaypointIndex,
    int WaypointCount,
    int ReplanAttempts,
    int RejectedRoomPortalCount,
    int RejectedCityPortalCount,
    NCCityNavigationPlanVersion PlanVersion,
    string? LastFailure,
    NCCityPhysicalExecutionTraceEntry[] Trace);

public readonly record struct NCCityPhysicalExecutionCounters(
    bool Active,
    int TrackedActors,
    int ScheduledExecutions,
    int QueuedIntentLoads,
    int IntentLoadsInFlight,
    int MutationsInFlight,
    long PlansBuilt,
    long WaypointsCompleted,
    long Replans,
    long PortalTraversals,
    long Arrivals,
    long BlockedGoals,
    long FailedGoals,
    string? ConfigurationError);

internal enum NCCityPhysicalMutationKind : byte
{
    Arrival,
    Blocked,
    Aborted,
}
