// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.LivingCity.Affordances;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Affordances;

public readonly record struct NCCityAffordanceId(long Value)
{
    public bool IsValid => Value > 0;
}

public readonly record struct NCCityAffordanceInfo(
    NCCityAffordanceId Id,
    ProtoId<NCCityAffordancePrototype> Prototype,
    NCCityAffordanceKind Kind,
    string KnowledgeKey,
    string OrganizationKey,
    EntityUid Provider,
    NCZoneId ZoneId,
    NCMapCoordinates Target,
    bool PhysicalProvider);

[ByRefEvent]
public readonly record struct NCCityAffordanceCatalogChangedEvent(long OldVersion, long NewVersion);

