// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Serialization;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Affordances;

public enum NCCityAffordanceKind : byte
{
    Workplace,
    Residence,
    PublicEntrance,
    BuildingExit,
    PatrolArea,
    Muster,
    Dispatch,
    Booking,
    Holding,
    EvidenceStorage,
    Armory,
    ReportDesk,
    MedicalTreatment,
    Shop,
    Warehouse,
    ConstructionSite,
    RestArea,
    FoodSource,
}

/// <summary>Data-only declaration placed on a real usable object, never on an invisible mapping marker.</summary>
[RegisterComponent]
public sealed partial class NCCityAffordanceProviderComponent : Component
{
    [DataField(required: true)] public List<ProtoId<NCCityAffordancePrototype>> Affordances = [];
}

[Prototype("ncCityAffordance")]
public sealed partial class NCCityAffordancePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField(required: true)] public NCCityAffordanceKind Kind { get; private set; }
    [DataField(required: true)] public string KnowledgeKey { get; private set; } = string.Empty;
    [DataField] public string OrganizationKey { get; private set; } = string.Empty;
    [DataField] public bool RequiresPhysicalProvider { get; private set; }
}

/// <summary>
/// Generates semantic capabilities from authored geography and the existing navigation samples. An empty Zone means
/// every candidate matching RequiredContext; a concrete Zone restricts generation to that hierarchy.
/// </summary>
[Prototype("ncCityZoneAffordanceSource")]
public sealed partial class NCCityZoneAffordanceSourcePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField(customTypeSerializer: typeof(NCZoneIdSerializer))] public NCZoneId Zone { get; private set; }
    [DataField] public NCZoneContextSlot RequiredContext { get; private set; }
    [DataField(required: true)] public ProtoId<NCCitySpawnCandidateProfilePrototype> CandidateProfile { get; private set; }
    [DataField(required: true)] public List<ProtoId<NCCityAffordancePrototype>> Affordances { get; private set; } = [];
    [DataField] public int MaximumCandidates { get; private set; } = 32;
}

