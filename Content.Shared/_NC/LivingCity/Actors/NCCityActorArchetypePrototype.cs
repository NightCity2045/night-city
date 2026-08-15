// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Actors;

/// <summary>
/// Data-driven materialization and scheduling defaults for one persistent-actor archetype.
/// </summary>
[Prototype("ncCityActorArchetype")]
public sealed partial class NCCityActorArchetypePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public EntProtoId EntityPrototype { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCCitySpawnCandidateProfilePrototype> SpawnCandidateProfile { get; private set; }

    [DataField(required: true)]
    public NCCityTravelMode TravelMode { get; private set; }

    [DataField(required: true)]
    public NCCityActorCognitionTier CognitionTier { get; private set; }

    [DataField(required: true)]
    public HashSet<NCZoneContextSlot> AllowedHomeContexts { get; private set; } = [];

    [DataField(required: true)]
    public HashSet<NCZoneContextSlot> AllowedWorkContexts { get; private set; } = [];

    [DataField(required: true)]
    public ProtoId<NCCityRoutineProfilePrototype> RoutineProfile { get; private set; }
}
