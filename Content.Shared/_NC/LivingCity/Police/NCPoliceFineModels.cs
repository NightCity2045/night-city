// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Database._NC.Police;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Police;

/// <summary>A time-sortable, persistent fine identity; it is never derived from an ECS entity.</summary>
[Serializable, NetSerializable]
public readonly record struct NCPoliceFineId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;

    public static NCPoliceFineId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

/// <summary>Canonical legal and payment state of a fine.</summary>
public sealed record NCPoliceFineSnapshot(
    NCPoliceFineId FineId,
    NCPartyRef Target,
    string TargetName,
    string Article,
    string Reason,
    long Amount,
    NCPoliceFineStatus Status,
    NCPartyRef IssuedBy,
    string IssuedByName,
    NCActiveWorldTime IssuedAt,
    NCActiveWorldTime DueAt,
    NCActiveWorldTime UpdatedAt,
    NCActiveWorldTime? PaidAt,
    NCTransactionId? PaymentTransactionId,
    int DataVersion,
    Guid LastAppliedEventId);
