// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
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
    Passage,
    Storage,
    Workstation,
    Weapon,
}

/// <summary>Stable adapters from existing vanilla components to data-driven city affordances.</summary>
public enum NCCityObjectAffordanceSource : byte
{
    Strap,
    VendingMachine,
    MedicalTreatment,
    Door,
    Storage,
    Edible,
    Weapon,
    Computer,
    ReagentDispenser,
    Microwave,
    PlantHolder,
    Puddle,
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
/// Maps an existing vanilla object category to semantic capabilities. C# only detects the category; YAML decides
/// what the object means to A-Life.
/// </summary>
[Prototype("ncCityObjectAffordanceRule")]
public sealed partial class NCCityObjectAffordanceRulePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField(required: true)] public NCCityObjectAffordanceSource Source { get; private set; }
    [DataField(required: true)] public List<ProtoId<NCCityAffordancePrototype>> Affordances { get; private set; } = [];
}

/// <summary>
/// Generates semantic capabilities from authored geography and the existing navigation samples. An empty Zone means
/// every candidate matching RequiredContext; a concrete Zone restricts generation to that hierarchy.
/// </summary>
[Prototype("ncCityZoneAffordanceSource")]
public sealed partial class NCCityZoneAffordanceSourcePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField] public Guid? Zone { get; private set; }
    [DataField] public NCZoneContextSlot RequiredContext { get; private set; }
    [DataField(required: true)] public ProtoId<NCCitySpawnCandidateProfilePrototype> CandidateProfile { get; private set; }
    [DataField(required: true)] public List<ProtoId<NCCityAffordancePrototype>> Affordances { get; private set; } = [];
    /// <summary>Capabilities placed only at real Room/Portal boundaries of the selected building hierarchy.</summary>
    [DataField] public List<ProtoId<NCCityAffordancePrototype>> BoundaryAffordances { get; private set; } = [];
    [DataField] public int MaximumCandidates { get; private set; } = 32;
}

