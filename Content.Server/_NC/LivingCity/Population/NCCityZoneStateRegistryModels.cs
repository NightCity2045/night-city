// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Population;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.GameObjects;

namespace Content.Server._NC.LivingCity.Population;

public enum NCCityZoneStateMutationStatus : byte
{
    Applied,
    Duplicate,
    NotFound,
    VersionConflict,
    ClockUnavailable,
    Rejected,
    StorageFailure,
}

public sealed record NCCityZoneStateRecord(
    NCCityZoneStateSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCCityZoneStateMutationRequest(
    Guid CommandId,
    long ExpectedVersion,
    NCCityZoneStateSnapshot Snapshot,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "living-city.zone-state-projector");

public sealed record NCCityZoneStateMutationResult(
    NCCityZoneStateMutationStatus Status,
    NCZoneId ScopeZone,
    long AggregateVersion,
    NCCityZoneStateRecord? Record,
    string? Error);

/// <summary>
/// Future authoritative city domains publish their normalized signal through this boundary. Activity remains owned
/// by the population projector and cannot be injected by another system.
/// </summary>
public sealed record NCCityStateSignalUpdate(
    NCZoneId ScopeZone,
    NCCityStateSignalKind Kind,
    NCCityStateSignalAvailability Availability,
    float Value,
    string Source,
    long SourceVersion);

public sealed class NCCityZoneStatePersistedEvent(
    NCZoneId scopeZone,
    NCZoneContextSlot scopeSlot,
    long aggregateVersion) : EntityEventArgs
{
    public NCZoneId ScopeZone { get; } = scopeZone;
    public NCZoneContextSlot ScopeSlot { get; } = scopeSlot;
    public long AggregateVersion { get; } = aggregateVersion;
}

