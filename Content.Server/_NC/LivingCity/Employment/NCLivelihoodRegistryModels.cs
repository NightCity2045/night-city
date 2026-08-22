// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Employment;

public enum NCLivelihoodMutationStatus : byte
{
    Applied,
    Duplicate,
    Conflict,
    Rejected,
    StorageFailure,
}

public sealed record NCLivelihoodRecord(
    NCLivelihoodSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCEnsureLivelihoodRequest(
    Guid CommandId,
    NCActorId ActorId,
    ProtoId<NCLivelihoodProfilePrototype> Profile,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "population-livelihood");

public sealed record NCLivelihoodMutationResult(
    NCLivelihoodMutationStatus Status,
    NCLivelihoodRecord? Record,
    string? Error);
