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
