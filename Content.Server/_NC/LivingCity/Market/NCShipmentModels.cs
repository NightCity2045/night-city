// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Market;

public sealed record NCShipmentRecord(
    NCShipmentSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCCreateShipmentRequest(
    Guid CommandId,
    NCShipmentId ShipmentId,
    NCPartyRef Shipper,
    NCPartyRef Recipient,
    NCPartyRef Carrier,
    NCStockNodeId OriginStockNodeId,
    NCStockNodeId DestinationStockNodeId,
    NCZoneId OriginZone,
    NCZoneId DestinationZone,
    NCCityTravelMode TravelMode,
    IReadOnlyList<NCShipmentAsset> Assets,
    NCShipmentHandoffKind HandoffKind,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "shipment-create");

public sealed record NCShipmentMutationResult(
    NCMarketMutationStatus Status,
    NCShipmentRecord? Shipment,
    string? Error);

public sealed class NCShipmentAwaitingHandoffEvent(NCShipmentId shipmentId) : EntityEventArgs
{
    public NCShipmentId ShipmentId { get; } = shipmentId;
}
