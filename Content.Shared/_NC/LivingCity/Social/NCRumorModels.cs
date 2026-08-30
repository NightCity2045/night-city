// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Social;

public readonly record struct NCInformationId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

public readonly record struct NCRumorKnowledgeId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public enum NCInformationSubjectKind : byte
{
    None,
    Party,
    Zone,
    Incident,
    StableKey,
}

/// <summary>
/// Persistent-safe subject for information. StableId is a canonical party string, GUID or authored stable key.
/// </summary>
public readonly record struct NCInformationSubject(NCInformationSubjectKind Kind, string StableId)
{
    public bool IsValid => Kind != NCInformationSubjectKind.None && !string.IsNullOrWhiteSpace(StableId);
}

[Serializable, NetSerializable]
public enum NCRumorSourceKind : byte
{
    DirectWitness,
    ParticipantReport,
    AgentMessage,
    Conversation,
    NewsRecord,
}

/// <summary>
/// A rumor is actor knowledge with provenance, not an objective world fact. Generated prose is never stored here.
/// </summary>
public sealed record NCRumorKnowledgeSnapshot(
    NCRumorKnowledgeId KnowledgeId,
    NCInformationId InformationId,
    NCPartyRef Owner,
    string TopicKey,
    NCInformationSubject Subject,
    IReadOnlyList<Guid> SourceWorldEventIds,
    NCRumorSourceKind SourceKind,
    NCPartyRef Originator,
    NCPartyRef ImmediateSource,
    int Confidence,
    int Distortion,
    int HopCount,
    NCMemoryPrivacy Privacy,
    bool Critical,
    NCActiveWorldTime OriginatedAt,
    NCActiveWorldTime LearnedAt,
    NCActiveWorldTime? ExpiresAt,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCRumorBookSnapshot(
    NCPartyRef Owner,
    IReadOnlyList<NCRumorKnowledgeSnapshot> Knowledge,
    long TotalLearned,
    long TotalPropagated,
    long TotalForgotten,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCLearnInformationRequest(
    Guid CommandId,
    NCPartyRef Owner,
    ProtoId<NCRumorTopicPrototype> Topic,
    NCInformationSubject Subject,
    IReadOnlyList<Guid> SourceWorldEventIds,
    NCRumorSourceKind SourceKind,
    NCPartyRef Originator,
    int? Confidence,
    NCMemoryPrivacy? Privacy,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "information-observed");

public sealed record NCPropagateInformationRequest(
    Guid CommandId,
    NCPartyRef SourceOwner,
    NCPartyRef TargetOwner,
    NCRumorKnowledgeId SourceKnowledgeId,
    NCRumorSourceKind Channel,
    Guid PropagationEventId,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "information-propagated");

