// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Actors;

public enum NCCityDematerializationReason : byte
{
    PlayerIrrelevant,
    BudgetPressure,
    GroupHandOff,
    Admin,
}

public enum NCCityDematerializationStatus : byte
{
    Requested,
    Loading,
    Capturing,
    Committing,
    Retrying,
    Dematerialized,
    CommittedRetained,
    AlreadyDematerialized,
    Rejected,
    Expired,
}

public sealed record NCCityDematerializationRequest(
    Guid RequestId,
    NCActorId ActorId,
    EntityUid EntityUid,
    NCCityDematerializationReason Reason,
    int Priority,
    string Source);

public sealed record NCCityDematerializationRequestState(
    Guid RequestId,
    NCActorId ActorId,
    EntityUid EntityUid,
    NCCityDematerializationReason Reason,
    NCCityDematerializationStatus Status,
    int Attempts,
    NCActiveWorldTime RequestedAt,
    NCActiveWorldTime ExpiresAt,
    string? Error);

public sealed class NCCityActorDematerializedEvent(
    Guid requestId,
    NCActorId actorId,
    EntityUid entityUid,
    NCCityDematerializationReason reason) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public NCActorId ActorId { get; } = actorId;
    public EntityUid EntityUid { get; } = entityUid;
    public NCCityDematerializationReason Reason { get; } = reason;
}
