// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Migration;

[Serializable, NetSerializable]
public enum NCLegacySourceKind : byte
{
    CharacterBank,
    OrganizationBank,
    CharacterEmployment,
}

/// <summary>
/// Provenance binding between one legacy authority row and its common-domain projection. It contains no PIN, account
/// number, player UUID, runtime entity, or legacy payload.
/// </summary>
public sealed record NCLegacyMigrationBindingSnapshot(
    Guid BindingId,
    NCLegacySourceKind SourceKind,
    string SourceKey,
    string SourceRevision,
    string ProjectionHash,
    long ProjectedBalance,
    NCPartyRef PrimaryParty,
    NCPartyRef RelatedParty,
    NCAccountId AccountId,
    NCEmploymentId EmploymentId,
    NCTransactionId LastTransactionId,
    NCActiveWorldTime ChangedAt,
    int DataVersion,
    Guid LastAppliedEventId);
