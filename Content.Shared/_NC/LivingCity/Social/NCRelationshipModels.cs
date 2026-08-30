// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Social;

public readonly record struct NCRelationshipId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// Directed social state from Observer toward Subject. Positive Debt means the observer believes they owe the subject;
/// a negative value means the observer believes the subject owes them.
/// </summary>
public sealed record NCRelationshipSnapshot(
    NCRelationshipId RelationshipId,
    NCPartyRef Observer,
    NCPartyRef Subject,
    int Trust,
    int Fear,
    int Respect,
    int Affection,
    int Hostility,
    long Debt,
    int Obligation,
    int InteractionCount,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    Guid LastWorldEventId,
    string LastEffectId,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCRelationshipIndexEntry(
    NCRelationshipId RelationshipId,
    NCPartyRef Subject,
    NCActiveWorldTime LastChangedAt,
    int LastChangedSequence);

public sealed record NCRelationshipIndexSnapshot(
    NCPartyRef Observer,
    IReadOnlyList<NCRelationshipIndexEntry> Relationships,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCApplyRelationshipEffectRequest(
    Guid CommandId,
    NCPartyRef Observer,
    NCPartyRef Subject,
    ProtoId<NCRelationshipEffectPrototype> Effect,
    Guid WorldEventId,
    int Magnitude,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "social-world-event");
