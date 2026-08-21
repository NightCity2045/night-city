// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Shared._NC.LivingCity.CityServices;

public enum NCHumanityCrisisStage : byte { Stable, Strained, Dissociating, Cyberpsychosis }

public sealed record NCHumanitySnapshot(NCActorId ActorId, int MaximumHumanity, int CurrentHumanity,
    int PermanentCyberwareLoss, NCHumanityCrisisStage CrisisStage, IReadOnlyList<Guid> AppliedSourceEventIds,
    NCActiveWorldTime UpdatedAt, int DataVersion, Guid LastAppliedEventId);
