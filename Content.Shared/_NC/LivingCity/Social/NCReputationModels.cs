// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Social;

public readonly record struct NCReputationId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

public readonly record struct NCReputationEntryId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

public enum NCReputationChannel : byte
{
    Personal,
    Faction,
    Professional,
    Public,
}

/// <summary>
/// Canonical persisted audience. Party is populated only for personal/faction channels; DomainKey is populated only
/// for professional/public channels and comes from the explicit stable key of a domain prototype.
/// </summary>
public readonly record struct NCReputationAudience(
    NCReputationChannel Channel,
    NCPartyRef Party,
    string DomainKey);

/// <summary>
/// Authoring/request form. Domain points to data that resolves to a stable persisted audience key.
/// </summary>
public readonly record struct NCReputationAudienceRequest(
    NCReputationChannel Channel,
    NCPartyRef Party,
    ProtoId<NCReputationDomainPrototype>? Domain = null);

public sealed record NCReputationEntrySnapshot(
    NCReputationEntryId EntryId,
    NCReputationAudience Audience,
    int Score,
    int Visibility,
    int Evidence,
    int EventCount,
    int LastChangedSequence,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    Guid LastWorldEventId,
    string LastEffectId,
    int DataVersion,
    Guid LastAppliedEventId);

/// <summary>
/// Bounded reputation projection for one persistent subject. Personal relationships remain a separate authority.
/// </summary>
public sealed record NCReputationSnapshot(
    NCReputationId ReputationId,
    NCPartyRef Subject,
    IReadOnlyList<NCReputationEntrySnapshot> Entries,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCApplyReputationEffectRequest(
    Guid CommandId,
    NCPartyRef Subject,
    NCReputationAudienceRequest Audience,
    ProtoId<NCReputationEffectPrototype> Effect,
    Guid WorldEventId,
    int Magnitude,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "reputation-world-event");
