// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Shared._NC.LivingCity.Social;

public readonly record struct NCSocialObligationId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public static NCSocialObligationId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public enum NCSocialObligationKind : byte
{
    Promise,
    Favor,
    Debt,
}

public enum NCSocialObligationStatus : byte
{
    Active,
    Fulfilled,
    Breached,
    Forgiven,
    Cancelled,
}

/// <summary>
/// Structured terms remain authoritative even if dialogue presents them as prose in either supported language.
/// </summary>
public sealed record NCSocialObligationTerms(
    string PurposeKey,
    long Quantity,
    string UnitKey,
    NCInformationSubject Subject);

public sealed record NCSocialObligationSnapshot(
    NCSocialObligationId ObligationId,
    NCSocialObligationKind Kind,
    NCPartyRef Debtor,
    NCPartyRef Creditor,
    NCSocialObligationTerms Terms,
    NCMemoryPrivacy Privacy,
    NCSocialObligationStatus Status,
    Guid CreatedWorldEventId,
    Guid? ResolvedWorldEventId,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime? DueAt,
    NCActiveWorldTime? ResolvedAt,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCSocialObligationIndexEntry(
    NCSocialObligationId ObligationId,
    NCPartyRef Counterparty,
    bool IsDebtor,
    NCSocialObligationKind Kind,
    NCSocialObligationStatus Status,
    NCActiveWorldTime ChangedAt,
    int ChangedSequence);

public sealed record NCSocialObligationIndexSnapshot(
    NCPartyRef Owner,
    IReadOnlyList<NCSocialObligationIndexEntry> Obligations,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCCreateSocialObligationRequest(
    Guid CommandId,
    NCSocialObligationId ObligationId,
    NCSocialObligationKind Kind,
    NCPartyRef Debtor,
    NCPartyRef Creditor,
    NCSocialObligationTerms Terms,
    NCMemoryPrivacy Privacy,
    Guid WorldEventId,
    NCActiveWorldTime? DueAt,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "social-obligation-created");

public sealed record NCResolveSocialObligationRequest(
    Guid CommandId,
    NCSocialObligationId ObligationId,
    NCSocialObligationStatus Status,
    Guid WorldEventId,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "social-obligation-resolved");

