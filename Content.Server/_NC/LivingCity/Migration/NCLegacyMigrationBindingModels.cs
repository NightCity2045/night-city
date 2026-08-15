// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Migration;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.GameObjects;

namespace Content.Server._NC.LivingCity.Migration;

public enum NCLegacyBindingMutationStatus : byte
{
    Applied,
    Duplicate,
    NotFound,
    VersionConflict,
    ClockUnavailable,
    Rejected,
    StorageFailure,
}

public sealed record NCLegacyMigrationBindingRecord(
    NCLegacyMigrationBindingSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCAdvanceLegacyMigrationBindingRequest(
    Guid CommandId,
    long ExpectedVersion,
    NCLegacyMigrationBindingSnapshot Snapshot,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCLegacyBindingMutationResult(
    NCLegacyBindingMutationStatus Status,
    NCLegacyMigrationBindingRecord? Binding,
    string? Error);

public sealed class NCLegacyMigrationBindingPersistedEvent(
    Guid bindingId,
    NCLegacySourceKind sourceKind,
    string sourceKey,
    long aggregateVersion) : EntityEventArgs
{
    public Guid BindingId { get; } = bindingId;
    public NCLegacySourceKind SourceKind { get; } = sourceKind;
    public string SourceKey { get; } = sourceKey;
    public long AggregateVersion { get; } = aggregateVersion;
}

