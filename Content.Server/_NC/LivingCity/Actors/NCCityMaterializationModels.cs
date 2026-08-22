// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared._NC.Coordinates;
using Robust.Shared.GameObjects;

namespace Content.Server._NC.LivingCity.Actors;

public enum NCCityMaterializationReason : byte
{
    PlayerRelevance,
    Incident,
    ServiceResponse,
    Admin,
}

public enum NCCityMaterializationRequestStatus : byte
{
    Queued,
    Loading,
    Retrying,
    Materialized,
    AlreadyMaterialized,
    Rejected,
    Expired,
}

public sealed record NCCityMaterializationRequest(
    Guid RequestId,
    NCActorId ActorId,
    NCCityMaterializationReason Reason,
    int Priority,
    uint SelectionSeed,
    TimeSpan? MaximumWait,
    string Source,
    NCMapCoordinates? PreferredTarget = null,
    float? PreferredRadius = null);

public sealed record NCCityMaterializationRequestState(
    Guid RequestId,
    NCActorId ActorId,
    NCCityMaterializationReason Reason,
    NCCityMaterializationRequestStatus Status,
    int Priority,
    int Attempts,
    NCActiveWorldTime RequestedAt,
    NCActiveWorldTime ExpiresAt,
    EntityUid EntityUid,
    string? Error);

public sealed class NCCityActorRehydratedEvent(
    Guid requestId,
    NCActorId actorId,
    EntityUid entityUid,
    NCCityMaterializationReason reason) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public NCActorId ActorId { get; } = actorId;
    public EntityUid EntityUid { get; } = entityUid;
    public NCCityMaterializationReason Reason { get; } = reason;
}

