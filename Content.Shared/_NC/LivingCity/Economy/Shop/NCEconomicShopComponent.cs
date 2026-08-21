// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Content.Shared._NC.LivingCity.Economy;

namespace Content.Shared._NC.LivingCity.Economy.Shop;

/// <summary>
/// Data-only configuration for a physical storefront backed by one finite persistent stock node.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class NCEconomicShopComponent : Component
{
    [DataField(required: true)] public string ShopId = string.Empty;
    [DataField(required: true)] public string DisplayName = string.Empty;
    [DataField(required: true)] public string SellerOrganizationId = string.Empty;
    [DataField(required: true)] public string SemanticLocationKey = string.Empty;
    [DataField] public int MaximumListings = 100;
    [DataField] public List<NCEconomicShopCommoditySeed> InitialCommodities = [];
    [DataField] public List<NCEconomicShopItemSeed> InitialItems = [];
}

[DataDefinition]
public sealed partial class NCEconomicShopCommoditySeed
{
    [DataField(required: true)] public ProtoId<NCCommodityPrototype> Commodity;
    [DataField(required: true)] public long Quantity;
    [DataField] public int? Quality;
}

[DataDefinition]
public sealed partial class NCEconomicShopItemSeed
{
    [DataField(required: true)] public ProtoId<NCItemDefinitionPrototype> Definition;
    [DataField(required: true)] public int Quantity;
    [DataField] public int? Condition;
}
