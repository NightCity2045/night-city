// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Actors.Components;

/// <summary>
/// Server-only projection key placed on a materialized item by its authoritative economy domain.
/// </summary>
[RegisterComponent]
public sealed partial class NCCityPersistentItemAdapterComponent : Component
{
    [DataField]
    public NCItemId ItemId;

    [DataField]
    public NCCityInventoryItemCategory Categories;

    [DataField]
    public string CommodityId = string.Empty;

    [DataField]
    public NCCommodityBatchId CommodityBatchId;

    [DataField]
    public long CommodityUnits;
}

/// <summary>
/// Read-only physical projection of an economic weapon assembly. Runtime combat adapters may inspect these stable
/// part identities, but assembly ownership remains in the economic item aggregates.
/// </summary>
[RegisterComponent]
public sealed partial class NCEconomicAssemblyAdapterComponent : Component
{
    [DataField]
    public List<NCItemId> InstalledPartIds = [];
}

/// <summary>
/// Server-only version marker supplied when the authoritative actor inventory is materialized.
/// </summary>
[RegisterComponent]
public sealed partial class NCCityActorInventoryAdapterComponent : Component
{
    [DataField]
    public long AuthorityVersion;

    [DataField]
    public string EquippedLoadoutId = string.Empty;
}
