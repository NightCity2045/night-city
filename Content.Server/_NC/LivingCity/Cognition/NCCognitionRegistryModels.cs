// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Cognition;

public enum NCCognitionMutationStatus : byte
{
    Applied,
    Duplicate,
    VersionConflict,
    ClockUnavailable,
    Rejected,
    StorageFailure,
}

public sealed record NCCognitionCommitRequest(
    Guid CommandId,
    Guid CorrelationId,
    Guid? CausationId,
    NCActorId ActorId,
    long ExpectedCognitionVersion,
    long ExpectedBeliefSetVersion,
    NCCognitionSnapshot Cognition,
    NCBeliefSetSnapshot BeliefSet,
    string AuditSource);

public sealed record NCCognitionCommitResult(
    NCCognitionMutationStatus Status,
    NCCognitionRecord? Cognition,
    NCBeliefSetRecord? BeliefSet,
    string? Error);

