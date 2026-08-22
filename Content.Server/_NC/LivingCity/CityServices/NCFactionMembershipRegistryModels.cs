// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.CityServices;

public enum NCFactionMembershipMutationStatus : byte
{
    Applied,
    Duplicate,
    Conflict,
    Rejected,
    StorageFailure,
}

public sealed record NCFactionMembershipRecord(
    NCActorFactionMembershipSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCEnsureFactionMembershipRequest(
    Guid CommandId,
    NCActorId ActorId,
    ProtoId<NCFactionProfilePrototype> Faction,
    string Role,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "population-affiliation");

public sealed record NCFactionMembershipMutationResult(
    NCFactionMembershipMutationStatus Status,
    NCFactionMembershipRecord? Record,
    string? Error);
