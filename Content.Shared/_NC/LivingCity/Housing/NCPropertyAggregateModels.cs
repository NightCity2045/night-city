// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Housing;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Shared._NC.LivingCity.Housing;

/// <summary>
/// Persistent occupancy relation. Gameplay deadlines use active-world time; wall-clock values belong only in audit
/// provenance and never advance a lease while no round is running.
/// </summary>
public sealed record NCPropertyOccupantSnapshot(
    NCPartyRef Occupant,
    NCPropertyOccupancyKind Kind,
    NCActiveWorldTime StartedAt,
    NCActiveWorldTime? ValidUntil);

public sealed record NCPropertyAccessGrantSnapshot(
    Guid GrantId,
    NCPartyRef Subject,
    NCPropertyPermission Permissions,
    int AccessRevision,
    NCActiveWorldTime IssuedAt,
    NCActiveWorldTime? ValidUntil);

public sealed record NCPropertyStorageBudget(
    int MaximumFixtureItems,
    int MaximumIncludedAssets,
    int MaximumPersonalItems,
    int MaximumMaterializedEntities);

/// <summary>
/// Common property authority. Static geography stays mapper-owned in city zones; this aggregate owns mutable tenure,
/// occupancy, access revision, interior selection and settlement provenance.
/// </summary>
public sealed record NCPropertyAggregateSnapshot(
    NCApartmentId PropertyId,
    NCBuildingId BuildingId,
    NCZoneId[] RoomIds,
    string ListingId,
    NCPartyRef Owner,
    NCPropertyOccupantSnapshot[] Occupants,
    NCPropertyAccessGrantSnapshot[] AccessGrants,
    NCPropertyStatus Status,
    long PurchasePrice,
    NCActiveWorldTime? AcquiredAt,
    int AccessRevision,
    string RentPolicyId,
    string AccessPolicyId,
    string? CurrentTemplateId,
    int? CurrentTemplateRevision,
    NCZoneDeploymentId ActiveDeploymentId,
    string InteriorState,
    NCActiveWorldTime? NextRentDueAt,
    NCTransactionId LastSettlementTransactionId,
    NCPropertyLegalState LegalState,
    NCPropertyStorageBudget StorageBudget,
    NCItemId[] FixtureItemIds,
    NCItemId[] IncludedAssetIds,
    string BlueprintSourceId,
    int RenovationRevision,
    long SecurityDepositAmount,
    NCAccountId SecurityDepositAccountId,
    NCActiveWorldTime? DelinquentSince,
    NCActiveWorldTime? EvictionAt,
    NCActiveWorldTime ChangedAt,
    int DataVersion,
    Guid LastAppliedEventId);
