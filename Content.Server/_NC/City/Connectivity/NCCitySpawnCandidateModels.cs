// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.City.Connectivity;

public readonly record struct NCCitySpawnCandidateId(long Value)
{
    public bool IsValid => Value > 0;
}

public readonly record struct NCCitySpawnCandidateInfo(
    NCCitySpawnCandidateId Id,
    ProtoId<NCCitySpawnCandidateProfilePrototype> Profile,
    NCCitySpawnCandidateKind Kind,
    NCCityTravelMode TravelMode,
    NCRoomNodeId RoomId,
    EntityUid GridUid,
    NCZoneId ZoneId,
    NCMapCoordinates Target);

[ByRefEvent]
public readonly record struct NCCitySpawnCandidateIndexChangedEvent(
    long OldVersion,
    long NewVersion,
    EntityUid AffectedGrid);
