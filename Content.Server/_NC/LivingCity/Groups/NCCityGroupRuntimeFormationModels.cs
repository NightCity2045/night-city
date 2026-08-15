// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.LivingCity.Groups;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Groups;

public sealed record NCCityRuntimeFormationMember(
    NCActorId ActorId,
    EntityUid EntityUid,
    string Role,
    int SlotIndex,
    Vector2 DesiredOffset,
    bool IsLeader);

public sealed record NCCityRuntimeFormationState(
    NCGroupId GroupId,
    long SourceGroupVersion,
    NCCityGroupFormationPolicy Policy,
    NCActorId LeaderActorId,
    EntityUid LeaderEntity,
    IReadOnlyList<NCCityRuntimeFormationMember> Members);
