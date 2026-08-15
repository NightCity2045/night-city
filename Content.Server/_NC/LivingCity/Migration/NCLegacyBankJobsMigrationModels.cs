// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Migration;

namespace Content.Server._NC.LivingCity.Migration;

public enum NCLegacyMigrationRunMode : byte
{
    Verify,
    Apply,
}

public enum NCLegacyMigrationRunStatus : byte
{
    Completed,
    Cancelled,
    Rejected,
    AlreadyRunning,
    Failed,
}

public enum NCLegacyMigrationPhase : byte
{
    Idle,
    CharacterBank,
    OrganizationBank,
    CharacterEmployment,
    Completed,
}

public enum NCLegacyMigrationIssueKind : byte
{
    MissingProjection,
    SourceChanged,
    TargetConflict,
    InvalidLegacyData,
    StorageFailure,
}

public sealed record NCLegacyMigrationIssue(
    NCLegacySourceKind SourceKind,
    string SourceKey,
    NCLegacyMigrationIssueKind Kind,
    string Message);

public sealed record NCLegacyBankJobsMigrationProgress(
    bool Running,
    NCLegacyMigrationRunMode Mode,
    NCLegacyMigrationPhase Phase,
    int Processed,
    int Aligned,
    int Changed,
    int Conflicts,
    int Failed,
    string? LastError);

public sealed record NCLegacyBankJobsMigrationReport(
    NCLegacyMigrationRunStatus Status,
    NCLegacyMigrationRunMode Mode,
    int Processed,
    int Aligned,
    int Changed,
    int Conflicts,
    int Failed,
    IReadOnlyList<NCLegacyMigrationIssue> Issues);
