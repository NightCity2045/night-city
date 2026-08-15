// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Actors;
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
    public long CommodityUnits;
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
