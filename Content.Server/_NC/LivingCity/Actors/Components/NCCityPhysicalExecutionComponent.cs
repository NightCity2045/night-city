// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Connectivity;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors.Components;

/// <summary>
/// Round-local data adapter between a persistent actor intent and physical SS14 movement. All transitions and side
/// effects are owned by <see cref="NCCityPhysicalExecutionSystem"/>.
/// </summary>
[RegisterComponent]
public sealed partial class NCCityPhysicalExecutionComponent : Component
{
    [ViewVariables(VVAccess.ReadOnly)] public NCActorId ActorId;
    [ViewVariables(VVAccess.ReadOnly)] public Guid GoalId;
    [ViewVariables(VVAccess.ReadOnly)] public long ActorVersion;
    [ViewVariables(VVAccess.ReadOnly)] public NCCityPhysicalExecutionState State;
    [ViewVariables(VVAccess.ReadOnly)] public NCCityPhysicalMode Mode;
    [ViewVariables(VVAccess.ReadOnly)] public NCCityPhysicalBehaviorOwner BehaviorOwner;
    [ViewVariables(VVAccess.ReadOnly)] public NCCityPhysicalInterruptFlags Interrupts;
    [ViewVariables(VVAccess.ReadOnly)] public NCZoneId CurrentZone;
    [ViewVariables(VVAccess.ReadOnly)] public NCZoneId DestinationZone;
    [ViewVariables(VVAccess.ReadOnly)] public NCMapCoordinates Destination;
    [ViewVariables(VVAccess.ReadOnly)] public NCCityTravelMode TravelMode;
    [ViewVariables(VVAccess.ReadOnly)] public string Activity = string.Empty;
    [ViewVariables(VVAccess.ReadOnly)] public string Goal = string.Empty;
    [ViewVariables(VVAccess.ReadOnly)] public NCCityActorAvailability Availability;
    [ViewVariables(VVAccess.ReadOnly)] public NCCityActorPresenceMode PresenceMode;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime NextScheduleUpdateAt;
    [ViewVariables(VVAccess.ReadOnly)] public ProtoId<NCCityPhysicalNavigationProfilePrototype> NavigationProfile;
    [ViewVariables(VVAccess.ReadOnly)] public ProtoId<NCCitySpawnCandidateProfilePrototype> SpawnCandidateProfile;
    [ViewVariables(VVAccess.ReadOnly)] public ProtoId<NCCityPhysicalStateTreePrototype> StateTreeProfile;
    [ViewVariables(VVAccess.ReadOnly)] public ProtoId<NCCityLocalPerceptionProfilePrototype> PerceptionProfile;
    [ViewVariables(VVAccess.ReadOnly)] public NCCityPhysicalFallback BlockedFallback;
    [ViewVariables(VVAccess.ReadOnly)] public bool InteractWithDoors;
    [ViewVariables(VVAccess.ReadOnly)] public bool CanClimb;
    [ViewVariables(VVAccess.ReadOnly)] public bool CanPry;
    [ViewVariables(VVAccess.ReadOnly)] public bool CanSmash;
    [ViewVariables(VVAccess.ReadOnly)] public List<NCCityNavigationWaypoint> Waypoints = [];
    [ViewVariables(VVAccess.ReadOnly)] public int WaypointIndex;
    [ViewVariables(VVAccess.ReadOnly)] public int ReplanAttempts;
    [ViewVariables(VVAccess.ReadOnly)] public HashSet<NCRoomPortalId> RejectedRoomPortals = [];
    [ViewVariables(VVAccess.ReadOnly)] public HashSet<NCCityPortalId> RejectedCityPortals = [];
    [ViewVariables(VVAccess.ReadOnly)] public NCCityNavigationPlanVersion PlanVersion;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime StepStartedAt;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime LastPortalInteractionAt;
    [ViewVariables(VVAccess.ReadOnly)] public string? LastFailure;
    [ViewVariables(VVAccess.ReadOnly)] public bool OwnsSteering;
    [ViewVariables(VVAccess.ReadOnly)] public bool HtnSuppressed;
    [ViewVariables(VVAccess.ReadOnly)] public bool RestoreHtnEnabled;
    [ViewVariables(VVAccess.ReadOnly)] public long NextTraceSequence;
    [ViewVariables(VVAccess.ReadOnly)] public List<NCCityPhysicalExecutionTraceEntry> Trace = [];
}
