// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Social;

/// <summary>One immutable, untrusted spoken turn in the durable conversation archive.</summary>
public sealed record NCConversationArchivedTurn(
    long Sequence,
    NCPartyRef Speaker,
    NCSpeechActKind Act,
    string Utterance,
    NCActiveWorldTime At);

/// <summary>
/// Durable source archive. Raw dialogue is retained for recall and audit, but never becomes gameplay authority.
/// </summary>
public sealed record NCConversationArchiveSnapshot(
    NCConversationSessionId SessionId,
    IReadOnlyList<NCPartyRef> Participants,
    NCConversationChannel Channel,
    NCInformationSubject LocationContext,
    NCActiveWorldTime StartedAt,
    NCActiveWorldTime LastActivityAt,
    NCMemoryPrivacy MaximumDisclosure,
    NCLanguage Language,
    NCLocale Locale,
    NCConversationStatus Status,
    string EndReason,
    IReadOnlyList<string> TopicKeys,
    IReadOnlyList<string> SemanticSummaryKeys,
    IReadOnlyList<NCConversationArchivedTurn> Turns,
    int DataVersion,
    Guid LastAppliedEventId);

/// <summary>
/// Actor-relative projection used for bounded recall. A separate entry exists for every actor/counterpart pair.
/// </summary>
public sealed record NCConversationArchiveIndexSnapshot(
    NCActorId OwnerActorId,
    NCPartyRef Counterpart,
    NCConversationSessionId SessionId,
    NCActiveWorldTime LastActivityAt,
    IReadOnlyList<string> TopicKeys,
    IReadOnlyList<string> SemanticSummaryKeys,
    string Summary,
    int TurnCount,
    int DataVersion,
    Guid LastAppliedEventId);

/// <summary>Provider-safe projection selected from the durable actor/counterpart archive.</summary>
public sealed record NCConversationMemoryContext(
    NCConversationSessionId SessionId,
    NCActiveWorldTime LastActivityAt,
    IReadOnlyList<string> TopicKeys,
    IReadOnlyList<string> SemanticSummaryKeys,
    string Summary);
