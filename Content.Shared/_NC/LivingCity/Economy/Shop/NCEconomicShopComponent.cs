// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Content.Shared._NC.LivingCity.Economy;
using System.Numerics;

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
    /// <summary>Definitions linked to this catalog are included without duplicating them on the console entity.</summary>
    [DataField] public ProtoId<NCEconomicShopCatalogPrototype>? Catalog;
    /// <summary>When true, another authoritative system must bind an existing stock node; bootstrap is skipped.</summary>
    [DataField] public bool ExternalStock;
    [DataField] public List<NCEconomicShopCommoditySeed> InitialCommodities = [];
    [DataField] public List<NCEconomicShopItemSeed> InitialItems = [];
}

/// <summary>
/// Marker prototype that groups product definitions into one mapper-selectable storefront catalog.
/// </summary>
[Prototype("ncEconomicShopCatalog")]
public sealed partial class NCEconomicShopCatalogPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
}

/// <summary>
/// Mapper-authored, round-local delivery bay for vehicles bought from one storefront.
/// </summary>
[RegisterComponent]
public sealed partial class NCEconomicShopVehicleDeliveryZoneComponent : Component
{
    [DataField(required: true)] public string ShopId = string.Empty;
    [DataField] public Vector2 HalfExtents = new(2.5f, 4f);
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
