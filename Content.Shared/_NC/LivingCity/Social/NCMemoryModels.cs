// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Social;

public readonly record struct NCMemoryBookId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

public readonly record struct NCMemoryId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

public readonly record struct NCMemorySummaryId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

public enum NCMemorySourceKind : byte
{
    DirectPerception,
    ParticipantReport,
    ThirdPartyReport,
    Record,
    Inference,
}

public enum NCMemoryPrivacy : byte
{
    Public,
    Personal,
    Restricted,
    Secret,
}

public enum NCMemoryRetentionClass : byte
{
    Transient,
    Standard,
    Durable,
    Permanent,
}

/// <summary>
/// Describes how the owner acquired the episode. SourceParty is required for reports and records, and absent for the
/// owner's own perception or inference.
/// </summary>
public readonly record struct NCMemorySource(
    NCMemorySourceKind Kind,
    NCPartyRef SourceParty);

public sealed record NCEpisodicMemorySnapshot(
    NCMemoryId MemoryId,
    NCPartyRef Owner,
    string EventTypeKey,
    string ConsolidationKey,
    IReadOnlyList<Guid> SourceWorldEventIds,
    IReadOnlyList<NCPartyRef> Participants,
    NCMemorySource Source,
    IReadOnlyList<string> EmotionalTagKeys,
    int Salience,
    int Confidence,
    NCMemoryPrivacy Privacy,
    NCMemoryRetentionClass RetentionClass,
    bool Critical,
    bool CanConsolidate,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime? ExpiresAt,
    NCActiveWorldTime? LastRecalledAt,
    int RecallCount,
    int LastChangedSequence,
    int DataVersion,
    Guid LastAppliedEventId);

/// <summary>
/// Canonical structured consolidation. It contains no generated prose and preserves the complete bounded provenance of
/// every episode it replaces. A future neural narrative is only a disposable rendering of this record.
/// </summary>
public sealed record NCConsolidatedMemorySnapshot(
    NCMemorySummaryId SummaryId,
    NCPartyRef Owner,
    string ConsolidationKey,
    IReadOnlyList<NCMemoryId> SourceMemoryIds,
    IReadOnlyList<Guid> SourceWorldEventIds,
    IReadOnlyList<string> EventTypeKeys,
    IReadOnlyList<NCPartyRef> Participants,
    IReadOnlyList<NCMemorySourceKind> SourceKinds,
    IReadOnlyList<string> EmotionalTagKeys,
    int OccurrenceCount,
    int MaximumSalience,
    int MinimumConfidence,
    NCMemoryPrivacy Privacy,
    NCActiveWorldTime FirstOccurredAt,
    NCActiveWorldTime LastOccurredAt,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime ExpiresAt,
    NCActiveWorldTime? LastRecalledAt,
    int RecallCount,
    int LastChangedSequence,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCMemoryBookSnapshot(
    NCMemoryBookId BookId,
    NCPartyRef Owner,
    IReadOnlyList<NCEpisodicMemorySnapshot> Episodes,
    IReadOnlyList<NCConsolidatedMemorySnapshot> Summaries,
    long TotalRemembered,
    long TotalForgotten,
    long TotalConsolidated,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCRememberWorldEventRequest(
    Guid CommandId,
    NCPartyRef Owner,
    ProtoId<NCMemoryEventPrototype> EventType,
    Guid WorldEventId,
    IReadOnlyList<Guid> AdditionalSourceEventIds,
    IReadOnlyList<NCPartyRef> Participants,
    NCMemorySource Source,
    IReadOnlyList<ProtoId<NCMemoryEmotionPrototype>> AdditionalEmotionalTags,
    int? Salience,
    int? Confidence,
    NCMemoryPrivacy? Privacy,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "memory-world-event");

public sealed record NCConsolidateMemoryRequest(
    Guid CommandId,
    NCPartyRef Owner,
    Guid ConsolidationEventId,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "memory-consolidation");

public sealed record NCRecordMemoryRecallRequest(
    Guid CommandId,
    NCPartyRef Owner,
    Guid RecallEventId,
    IReadOnlyList<NCMemoryId> MemoryIds,
    IReadOnlyList<NCMemorySummaryId> SummaryIds,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "memory-recall");

public sealed record NCMemoryQuery(
    IReadOnlyList<NCPartyRef> Participants,
    IReadOnlyList<string> EventTypeKeys,
    IReadOnlyList<string> EmotionalTagKeys,
    NCMemoryPrivacy MaximumPrivacy,
    int Limit);

public sealed record NCMemoryContextItem(
    NCMemoryId? MemoryId,
    NCMemorySummaryId? SummaryId,
    int Relevance,
    NCEpisodicMemorySnapshot? Episode,
    NCConsolidatedMemorySnapshot? Summary);
