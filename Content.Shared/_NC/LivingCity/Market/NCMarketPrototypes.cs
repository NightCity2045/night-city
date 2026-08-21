// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Economy;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Market;

[Prototype("ncProductionRecipe")]
public sealed partial class NCProductionRecipePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string StableKey { get; private set; } = string.Empty;
    [DataField] public List<NCProductionCommodityInput> CommodityInputs { get; private set; } = [];
    [DataField] public List<NCProductionCommodityOutput> CommodityOutputs { get; private set; } = [];
    [DataField] public List<NCProductionItemOutput> ItemOutputs { get; private set; } = [];
    [DataField] public TimeSpan Duration { get; private set; } = TimeSpan.FromMinutes(10);
    [DataField] public List<NCBusinessKind> AllowedBusinessKinds { get; private set; } = [];
    [DataField] public bool AllowsAggregatedDemand { get; private set; }
}

[DataDefinition]
public sealed partial class NCProductionCommodityInput
{
    [DataField(required: true)] public ProtoId<NCCommodityPrototype> Commodity;
    [DataField(required: true)] public long Quantity;
    [DataField] public int MinimumQuality;
}

[DataDefinition]
public sealed partial class NCProductionCommodityOutput
{
    [DataField(required: true)] public ProtoId<NCCommodityPrototype> Commodity;
    [DataField(required: true)] public long Quantity;
    [DataField] public int Quality = 1_000;
}

[DataDefinition]
public sealed partial class NCProductionItemOutput
{
    [DataField(required: true)] public ProtoId<NCItemDefinitionPrototype> Definition;
    [DataField(required: true)] public int Quantity;
    [DataField] public int Condition = 1_000;
}

[Prototype("ncMarketPolicy")]
public sealed partial class NCMarketPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public int MaximumListingsPerParty { get; private set; } = 256;
    [DataField] public int MaximumRequestsPerParty { get; private set; } = 128;
    [DataField] public int MaximumDiscoveryResults { get; private set; } = 100;
    [DataField] public TimeSpan MaximumListingLifetime { get; private set; } = TimeSpan.FromDays(7);
    [DataField] public TimeSpan MaximumRequestLifetime { get; private set; } = TimeSpan.FromDays(7);
    [DataField] public int MaximumShipmentAssets { get; private set; } = 32;
    [DataField] public int MaximumProductionInputs { get; private set; } = 16;
    [DataField] public int MaximumProductionOutputs { get; private set; } = 16;
    [DataField] public int MaximumRouteLegs { get; private set; } = 128;
    [DataField] public TimeSpan MinimumShipmentDuration { get; private set; } = TimeSpan.FromMinutes(1);
    [DataField] public TimeSpan MaximumShipmentDuration { get; private set; } = TimeSpan.FromHours(24);
    [DataField] public int ShipmentDurationBasisPoints { get; private set; } = 10_000;
    [DataField] public int ThreatCostToRiskBasisPoints { get; private set; } = 100;
}

[Prototype("ncAggregateDemandPolicy")]
public sealed partial class NCAggregateDemandPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public TimeSpan Interval { get; private set; } = TimeSpan.FromMinutes(30);
    [DataField] public int MaximumBusinessesPerRun { get; private set; } = 100;
    [DataField] public int MaximumRequestsPerRun { get; private set; } = 32;
    [DataField] public List<NCAggregateCommodityDemand> CommodityDemands { get; private set; } = [];
}

[DataDefinition]
public sealed partial class NCAggregateCommodityDemand
{
    [DataField(required: true)] public ProtoId<NCCommodityPrototype> Commodity;
    [DataField(required: true)] public long TargetQuantity;
    [DataField(required: true)] public long RequestQuantity;
    [DataField(required: true)] public long MaximumUnitPrice;
    [DataField] public int MinimumQuality;
    [DataField] public bool UseOutputStock = true;
    [DataField] public List<NCBusinessKind> BusinessKinds = [];
}
