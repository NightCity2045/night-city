// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Map;

namespace Content.Server._NC.LivingCity.Actors;

public enum NCCityActorLod : byte
{
    Entity,
    Local,
    Strategic,
    City,
}

public sealed record NCCityPlayerCluster(
    Guid ClusterId,
    MapId MapId,
    IReadOnlyList<EntityUid> AnchorEntities,
    IReadOnlyList<NCZoneId> ActiveZoneIds,
    int AttachedPlayerCount,
    int MaterializedActorCount,
    int SoftMaterializedBudget);

