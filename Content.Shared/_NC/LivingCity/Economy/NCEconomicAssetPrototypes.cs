// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;
using Robust.Shared.Localization;
using Content.Shared.Chemistry.Reagent;

namespace Content.Shared._NC.LivingCity.Economy;

[Prototype("ncCommodity")]
public sealed partial class NCCommodityPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string StableKey { get; private set; } = string.Empty;
    [DataField] public NCCommodityUnit Unit { get; private set; } = NCCommodityUnit.Unit;
    [DataField] public NCEconomicAssetDomain Domain { get; private set; }
    [DataField] public bool Divisible { get; private set; } = true;
    [DataField] public long MaximumBatchQuantity { get; private set; } = 1_000_000;
    [DataField] public int DefaultQuality { get; private set; } = 1_000;
    [DataField] public TimeSpan ShelfLife { get; private set; }
    [DataField] public long? BaseUnitPrice { get; private set; }
    [DataField] public NCEconomicLegalStatus LegalStatus { get; private set; }
    [DataField] public NCEconomicTradeStatus TradeStatus { get; private set; }
    [DataField] public bool AllowsAggregatedDemand { get; private set; }
    [DataField] public EntProtoId? MaterializedPrototype { get; private set; }
    [DataField] public int MaterializedUnitsPerEntity { get; private set; } = 1;
    [DataField] public string MaterializedSolutionId { get; private set; } = string.Empty;
    [DataField] public ProtoId<ReagentPrototype>? MaterializedReagent { get; private set; }
}

[Prototype("ncItemDefinition")]
public sealed partial class NCItemDefinitionPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public LocId DisplayName { get; private set; } = default!;
    [DataField(required: true)] public string StableKey { get; private set; } = string.Empty;
    [DataField] public NCEconomicAssetDomain Domain { get; private set; }
    [DataField] public string DomainPrototypeId { get; private set; } = string.Empty;
    [DataField] public EntProtoId? MaterializedPrototype { get; private set; }
    [DataField] public long? BasePrice { get; private set; }
    [DataField] public int DefaultCondition { get; private set; } = 1_000;
    [DataField] public NCEconomicLegalStatus LegalStatus { get; private set; }
    [DataField] public NCEconomicTradeStatus TradeStatus { get; private set; }
    [DataField] public bool SerialRequired { get; private set; }
    [DataField] public int MaximumInstalledParts { get; private set; } = 18;
    [DataField] public List<ProtoId<NCItemDefinitionPrototype>> AllowedInstalledPartDefinitions { get; private set; } = [];
}

[Prototype("ncEconomicAssetPolicy")]
public sealed partial class NCEconomicAssetPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public int MaximumBatchesPerStockNode { get; private set; } = 512;
    [DataField] public int MaximumItemsPerStockNode { get; private set; } = 512;
    [DataField] public int MaximumSourceEvents { get; private set; } = 16;
    [DataField] public int MaximumStableKeyLength { get; private set; } = 128;
    [DataField] public int MaximumSerialLength { get; private set; } = 128;
    [DataField] public int MaximumQueryCount { get; private set; } = 100;
}

[Prototype("ncEconomicPricingPolicy")]
public sealed partial class NCEconomicPricingPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public string CurrencyId { get; private set; } = "eb";
    [DataField] public TimeSpan MaximumQuoteLifetime { get; private set; } = TimeSpan.FromMinutes(10);
    [DataField] public TimeSpan MaximumReservationLifetime { get; private set; } = TimeSpan.FromMinutes(5);
    [DataField] public int MinimumMultiplierBasisPoints { get; private set; } = 2_500;
    [DataField] public int MaximumMultiplierBasisPoints { get; private set; } = 40_000;
    [DataField] public int ScarcityWeightBasisPoints { get; private set; } = 4_000;
    [DataField] public int DemandWeightBasisPoints { get; private set; } = 2_500;
    [DataField] public int DangerWeightBasisPoints { get; private set; } = 1_500;
    [DataField] public int RelationshipWeightBasisPoints { get; private set; } = -2_000;
    [DataField] public int LegalityWeightBasisPoints { get; private set; } = 2_000;
    [DataField] public int UrgencyWeightBasisPoints { get; private set; } = 2_000;
    [DataField] public int FeeBasisPoints { get; private set; }
    [DataField] public int TaxBasisPoints { get; private set; }
    [DataField] public string FeeReceiverServiceId { get; private set; } = string.Empty;
    [DataField] public string TaxReceiverServiceId { get; private set; } = string.Empty;
    [DataField] public long MaximumQuoteTotal { get; private set; } = 1_000_000_000;
}

[Prototype("ncEconomicService")]
public sealed partial class NCEconomicServicePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public LocId DisplayName { get; private set; } = default!;
    [DataField(required: true)] public string StableKey { get; private set; } = string.Empty;
    [DataField(required: true)] public string FulfillmentKey { get; private set; } = string.Empty;
    [DataField] public long? BasePrice { get; private set; }
    [DataField] public TimeSpan ExpectedDuration { get; private set; }
    [DataField] public NCEconomicLegalStatus LegalStatus { get; private set; }
}

[Prototype("ncEconomicInformationAccess")]
public sealed partial class NCEconomicInformationAccessPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string StableKey { get; private set; } = string.Empty;
    [DataField(required: true)] public string GrantKey { get; private set; } = string.Empty;
    [DataField] public long? BasePrice { get; private set; }
    [DataField] public bool Reusable { get; private set; }
    [DataField] public NCEconomicLegalStatus LegalStatus { get; private set; }
}
