// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;

namespace Content.Server._NC.LivingCity.Population;

/// <summary>
/// Round-local read model for F7 observation. It is derived from the event-driven population projection and is
/// never persisted as a second source of city authority.
/// </summary>
public readonly record struct NCCityZoneObserverProjection(
    NCZoneId ScopeZone,
    NCZoneContextSlot ScopeSlot,
    int TotalActors,
    int LivingActors,
    int FrozenActors,
    int TravelingActors,
    int PublicActors,
    int AbstractHomeActors,
    int PhysicalHomeActors,
    float Activity,
    float Scarcity,
    float Threat,
    float Control);
