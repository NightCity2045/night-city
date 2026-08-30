// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.GameObjects;

namespace Content.Server._NC.LivingCity.Identity;

public enum NCPartyDirectoryMutationStatus : byte
{
    Applied,
    Duplicate,
    NotFound,
    VersionConflict,
    ClockUnavailable,
    Rejected,
    StorageFailure,
}

public sealed record NCPartyDirectoryRecord(
    NCPartyDirectorySnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCPartyDirectoryRegisterRequest(
    Guid CommandId,
    NCPartyRef Party,
    string DisplayName,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCPartyDirectoryLifecycleRequest(
    Guid CommandId,
    NCPartyRef Party,
    long ExpectedVersion,
    NCPartyLifecycle Lifecycle,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCPartyDirectoryMutationResult(
    NCPartyDirectoryMutationStatus Status,
    NCPartyRef Party,
    NCPartyDirectoryRecord? Record,
    string? Error);

public sealed class NCPartyDirectoryPersistedEvent(
    NCPartyRef party,
    long aggregateVersion,
    NCPartyLifecycle lifecycle) : EntityEventArgs
{
    public NCPartyRef Party { get; } = party;
    public long AggregateVersion { get; } = aggregateVersion;
    public NCPartyLifecycle Lifecycle { get; } = lifecycle;
}

public enum NCPartyResolutionStatus : byte
{
    Resolved,
    Invalid,
    NotFound,
    Inactive,
}

/// <summary>
/// Minimal safe identity projection. Resolution does not materialize an entity or expose private character data.
/// </summary>
public sealed record NCPartyResolution(
    NCPartyResolutionStatus Status,
    NCPartyRef Party,
    string DisplayName,
    NCPartyLifecycle Lifecycle,
    long SourceVersion,
    string? Error)
{
    public bool IsResolved => Status == NCPartyResolutionStatus.Resolved;
}

