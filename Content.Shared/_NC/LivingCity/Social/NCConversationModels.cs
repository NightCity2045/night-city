// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Shared._NC.LivingCity.Social;

public readonly record struct NCConversationSessionId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public static NCConversationSessionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public enum NCConversationChannel : byte
{
    InPerson,
    AgentCall,
    AgentDirectMessage,
    AgentGroupMessage,
    AgentContactExchange,
}

public enum NCConversationStatus : byte
{
    Starting,
    Active,
    WaitingForActor,
    WaitingForPlayer,
    Interrupted,
    Ended,
    Expired,
}

public enum NCSpeechActKind : byte
{
    Greeting,
    Statement,
    Question,
    Answer,
    Offer,
    Acceptance,
    Refusal,
    Threat,
    Farewell,
    ChannelActivity,
}

/// <summary>
/// Structured semantic turn. Text stays in its owning chat/Agent system and is treated as untrusted rendering input.
/// </summary>
public sealed record NCConversationTurn(
    long Sequence,
    NCPartyRef Speaker,
    NCSpeechActKind Act,
    string SemanticKey,
    string ExternalReference,
    Guid? WorldEventId,
    NCActiveWorldTime At);

public sealed record NCConversationSessionSnapshot(
    NCConversationSessionId SessionId,
    IReadOnlyList<NCPartyRef> Participants,
    NCConversationChannel Channel,
    NCInformationSubject LocationContext,
    NCActiveWorldTime StartedAt,
    NCActiveWorldTime LastActivityAt,
    int ConversationVersion,
    IReadOnlyList<string> CurrentTopicKeys,
    IReadOnlyList<Guid> RelevantFactIds,
    NCMemoryPrivacy MaximumDisclosure,
    NCLanguage Language,
    NCLocale Locale,
    IReadOnlyList<NCConversationTurn> Turns,
    NCConversationStatus Status,
    string EndReason);

public sealed record NCConversationStartRequest(
    NCConversationSessionId SessionId,
    IReadOnlyList<NCPartyRef> Participants,
    NCConversationChannel Channel,
    NCInformationSubject LocationContext,
    IReadOnlyList<string> TopicKeys,
    IReadOnlyList<Guid> RelevantFactIds,
    NCMemoryPrivacy MaximumDisclosure,
    NCLanguage Language,
    NCLocale Locale);

public sealed record NCConversationTurnRequest(
    NCConversationSessionId SessionId,
    NCPartyRef Speaker,
    NCSpeechActKind Act,
    string SemanticKey,
    string ExternalReference,
    Guid? WorldEventId);

