// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Economy;

namespace Content.Shared._NC.LivingCity.Market;

/// <summary>
/// Data-only marker for a physical projection of one persistent shipment manifest.
/// </summary>
[RegisterComponent]
public sealed partial class NCShipmentCargoComponent : Component
{
    [DataField] public NCShipmentId ShipmentId;
    [DataField] public NCStockNodeId TransitStockNodeId;
    [DataField] public bool Sealed = true;
}

/// <summary>
/// Mapper-placeable handoff endpoint. Its semantic zone is derived from city_zone.yml at runtime.
/// </summary>
[RegisterComponent]
public sealed partial class NCShipmentHandoffPointComponent : Component
{
    [DataField(required: true)] public string StableKey = string.Empty;
    [DataField] public NCShipmentHandoffKind HandoffKind = NCShipmentHandoffKind.ParcelLocker;
    [DataField] public int MaximumActiveParcels = 8;
}
