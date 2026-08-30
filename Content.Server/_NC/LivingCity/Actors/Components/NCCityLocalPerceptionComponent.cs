// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors.Components;

public enum NCCityLocalObservationKind : byte
{
    Neutral,
    Friendly,
    Hostile,
    Critical,
    Dead,
    /// <summary>An entity tied to a directly observed, still-current police incident.</summary>
    Suspect,
}

/// <summary>Semantic event witnessed by this actor; entity references never leave the server boundary.</summary>
public enum NCCityLocalPerceivedEventKind : byte
{
    Assault,
    Killing,
    Gunshot,
    WeaponDisplayed,
    HeldItemDisplayed,
    SevereInjury,
    BodyDiscovered,
    Fire,
    AddressedSpeech,
    VerbalThreat,
}

public readonly record struct NCCityLocalObservation(
    EntityUid EntityUid,
    string SubjectHandle,
    NCCityLocalObservationKind Kind,
    float DistanceSquared,
    float Confidence,
    NCActiveWorldTime ObservedAt,
    NCActiveWorldTime ExpiresAt);

public readonly record struct NCCityLocalPerceivedEvent(
    Guid EventId,
    NCCityLocalPerceivedEventKind Kind,
    EntityUid ActorEntity,
    string ActorHandle,
    EntityUid TargetEntity,
    string TargetHandle,
    float Confidence,
    NCActiveWorldTime ObservedAt,
    NCActiveWorldTime ExpiresAt);

/// <summary>
/// Raised on the observing human after universal perception records a real incident. Profession systems consume
/// this fact instead of subscribing to the original damage event and repeating the spatial visibility query.
/// </summary>
public sealed class NCCityLocalIncidentObservedEvent(
    EntityUid observer,
    Guid eventId,
    NCCityLocalPerceivedEventKind Kind,
    EntityUid Actor,
    EntityUid Target,
    NCActiveWorldTime ObservedAt) : EntityEventArgs
{
    public EntityUid Observer { get; } = observer;
    public Guid EventId { get; } = eventId;
    public NCCityLocalPerceivedEventKind Kind { get; } = Kind;
    public EntityUid Actor { get; } = Actor;
    public EntityUid Target { get; } = Target;
    public NCActiveWorldTime ObservedAt { get; } = ObservedAt;
}

/// <summary>
/// Bounded, round-local sensory cache. Persistent beliefs and memories consume explicit later events, never this
/// EntityUid-bearing adapter directly.
/// </summary>
[RegisterComponent]
public sealed partial class NCCityLocalPerceptionComponent : Component
{
    [ViewVariables(VVAccess.ReadOnly)] public ProtoId<NCCityLocalPerceptionProfilePrototype> Profile;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime LastEvaluatedAt;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime NextEvaluationAt;
    [ViewVariables(VVAccess.ReadOnly)] public long Revision;
    [ViewVariables(VVAccess.ReadOnly)] public int HostileCount;
    [ViewVariables(VVAccess.ReadOnly)] public int CriticalCount;
    [ViewVariables(VVAccess.ReadOnly)] public EntityUid NearestHostile = EntityUid.Invalid;
    [ViewVariables(VVAccess.ReadOnly)] public EntityUid NearestCritical = EntityUid.Invalid;
    [ViewVariables(VVAccess.ReadOnly)] public List<NCCityLocalObservation> Observations = [];
    [ViewVariables(VVAccess.ReadOnly)] public List<NCCityLocalPerceivedEvent> RecentEvents = [];
}
