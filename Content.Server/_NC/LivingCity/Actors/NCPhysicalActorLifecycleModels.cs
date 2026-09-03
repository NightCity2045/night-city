// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Map;

namespace Content.Server._NC.LivingCity.Actors;

public enum NCPhysicalActorSpawnReason : byte
{
    RoundBootstrap,
    Admin,
    Recovery,
    ServiceDispatch,
}

public enum NCPhysicalActorSpawnStatus : byte
{
    Queued,
    Loading,
    Retrying,
    Spawned,
    AlreadyPhysical,
    Rejected,
}

public sealed record NCPhysicalActorSpawnRequest(
    Guid RequestId,
    NCActorId ActorId,
    NCPhysicalActorSpawnReason Reason,
    string Source,
    MapCoordinates? PreferredTarget = null,
    float? PreferredRadius = null);

public sealed record NCPhysicalActorSpawnState(
    Guid RequestId,
    NCActorId ActorId,
    NCPhysicalActorSpawnReason Reason,
    NCPhysicalActorSpawnStatus Status,
    int Attempts,
    NCActiveWorldTime RequestedAt,
    EntityUid? EntityUid,
    string? Error);

public sealed class NCPhysicalActorSpawnedEvent(
    Guid requestId,
    NCActorId actorId,
    EntityUid entityUid,
    NCPhysicalActorSpawnReason reason) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public NCActorId ActorId { get; } = actorId;
    public EntityUid EntityUid { get; } = entityUid;
    public NCPhysicalActorSpawnReason Reason { get; } = reason;
}
