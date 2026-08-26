// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Employment;

public enum NCWorkforceAssignmentStatus : byte
{
    Applied,
    Existing,
    Conflict,
    Rejected,
    Failed,
}

public sealed record NCWorkforceAssignmentResult(
    NCWorkforceAssignmentStatus Status,
    NCActorId ActorId,
    ProtoId<DepartmentPrototype>? Organization,
    ProtoId<JobPrototype>? Job,
    NCEmploymentId EmploymentId,
    string? Error);

public sealed record NCWorkforcePositionProjection(
    ProtoId<DepartmentPrototype> Organization,
    ProtoId<JobPrototype> Job,
    int AssignedActors);

public sealed record NCWorkforceActorProjection(
    NCActorId ActorId,
    ProtoId<DepartmentPrototype> Organization,
    ProtoId<JobPrototype> Job);

/// <summary>
/// Main-thread notification that a persistent actor has a canonical organization/job projection.
/// Consumers must treat it as an index update, not as a new hire command.
/// </summary>
public sealed class NCWorkforceAssignmentChangedEvent(
    NCActorId actorId,
    ProtoId<DepartmentPrototype> organization,
    ProtoId<JobPrototype> job,
    bool active = true) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public ProtoId<DepartmentPrototype> Organization { get; } = organization;
    public ProtoId<JobPrototype> Job { get; } = job;
    public bool Active { get; } = active;
}
