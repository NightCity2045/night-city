// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Housing;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.GameObjects;

namespace Content.Server._NC.LivingCity.Housing;

public enum NCPropertyAggregateMutationStatus : byte
{
    Applied,
    Duplicate,
    NotFound,
    VersionConflict,
    NotAuthorized,
    ClockUnavailable,
    Rejected,
    StorageFailure,
}

public sealed record NCPropertyAggregateRecord(
    NCPropertyAggregateSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

/// <summary>Registers one mapper-authored property directly in the canonical registry.</summary>
public sealed record NCRegisterPropertyRequest(
    Guid CommandId,
    NCApartmentId PropertyId,
    NCBuildingId BuildingId,
    NCZoneId[] RoomIds,
    string ListingId,
    NCPartyRef Owner,
    long PurchasePrice,
    string AccessPolicyId,
    NCPropertyStorageBudget StorageBudget,
    string BlueprintSourceId,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "property-registration");

/// <summary>
/// Commits an already domain-validated non-monetary property change. Geography, listing and owner are immutable here;
/// ownership changes that move money use <see cref="NCPropertySettlementSystem"/> instead.
/// </summary>
public sealed record NCUpdatePropertyRequest(
    Guid CommandId,
    long ExpectedVersion,
    NCPropertyAggregateSnapshot Proposed,
    string EventType,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "property-runtime");

public sealed record NCPropertyAggregateMutationResult(
    NCPropertyAggregateMutationStatus Status,
    NCPropertyAggregateRecord? Property,
    string? Error);

public sealed class NCPropertyAggregatePersistedEvent(
    NCPropertyAggregateRecord property) : EntityEventArgs
{
    public NCPropertyAggregateRecord Property { get; } = property;
}
