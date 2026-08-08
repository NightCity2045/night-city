// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Database._NC.Police;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Police.UI;

[Serializable, NetSerializable]
public enum NCPoliceRecordsUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed record NCPoliceRecordSummary(
    int CharacterId,
    string CharacterName,
    string? JobPrototypeId,
    bool PresentThisRound,
    NCPoliceOperationalStatus Status,
    string? Reason,
    string UpdatedByName,
    DateTime? UpdatedAt);

[Serializable, NetSerializable]
public sealed record NCPoliceRecordHistoryEntry(
    long EventId,
    NCPoliceRecordEventType EventType,
    NCPoliceOperationalStatus PreviousStatus,
    NCPoliceOperationalStatus NewStatus,
    string? Reason,
    string ActorName,
    DateTime CreatedAt);

[Serializable, NetSerializable]
public sealed record NCPoliceCaseSubjectSummary(
    int CharacterId,
    string CharacterName,
    NCPoliceCaseSubjectRole Role);

[Serializable, NetSerializable]
public sealed record NCPoliceCaseEntrySummary(
    long EntryId,
    NCPoliceCaseEntryType EntryType,
    string Text,
    NCPoliceCaseStatus? PreviousStatus,
    NCPoliceCaseStatus? NewStatus,
    int? SubjectCharacterId,
    string? SubjectName,
    NCPoliceCaseSubjectRole? SubjectRole,
    string AuthorName,
    DateTime CreatedAt);

[Serializable, NetSerializable]
public sealed record NCPoliceCaseSummary(
    long CaseId,
    string Title,
    string Summary,
    NCPoliceCaseStatus Status,
    string CreatedByName,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<NCPoliceCaseSubjectSummary> Subjects,
    List<NCPoliceCaseEntrySummary> Entries);

[Serializable, NetSerializable]
public sealed record NCPoliceWarrantSummary(
    long WarrantId,
    long? CaseId,
    int TargetCharacterId,
    string TargetName,
    NCPoliceWarrantType Type,
    NCPoliceWarrantStatus Status,
    string Reason,
    string IssuedByName,
    DateTime IssuedAt,
    string? ResolvedByName,
    string? ResolutionReason,
    DateTime? ResolvedAt);

[Serializable, NetSerializable]
public sealed record NCPoliceFineSummary(long FineId, int TargetCharacterId, string TargetName,
    string Article, string Reason, int Amount, NCPoliceFineStatus Status, string IssuedByName,
    DateTime IssuedAt, DateTime DueAt, DateTime? PaidAt);

/// <summary>
/// Per-user console update. A direct BUI message avoids the vanilla console problem where users share one selection.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCPoliceRecordsUpdateMessage(
    List<NCPoliceRecordSummary> searchResults,
    NCPoliceRecordSummary? selectedRecord,
    List<NCPoliceRecordHistoryEntry> history,
    List<NCPoliceCaseSummary> cases,
    NCPoliceCaseSummary? selectedCase,
    List<NCPoliceWarrantSummary> warrants,
    List<NCPoliceFineSummary> fines,
    bool canEdit) : BoundUserInterfaceMessage
{
    public readonly List<NCPoliceRecordSummary> SearchResults = searchResults;
    public readonly NCPoliceRecordSummary? SelectedRecord = selectedRecord;
    public readonly List<NCPoliceRecordHistoryEntry> History = history;
    public readonly List<NCPoliceCaseSummary> Cases = cases;
    public readonly NCPoliceCaseSummary? SelectedCase = selectedCase;
    public readonly List<NCPoliceWarrantSummary> Warrants = warrants;
    public readonly List<NCPoliceFineSummary> Fines = fines;
    public readonly bool CanEdit = canEdit;
}

[Serializable, NetSerializable]
public sealed class NCPoliceRecordsSearchMessage(string query) : BoundUserInterfaceMessage
{
    public readonly string Query = query;
}

[Serializable, NetSerializable]
public sealed class NCPoliceRecordsSelectMessage(int characterId) : BoundUserInterfaceMessage
{
    public readonly int CharacterId = characterId;
}

[Serializable, NetSerializable]
public sealed class NCPoliceRecordsChangeStatusMessage(
    int characterId,
    NCPoliceOperationalStatus status,
    string? reason) : BoundUserInterfaceMessage
{
    public readonly int CharacterId = characterId;
    public readonly NCPoliceOperationalStatus Status = status;
    public readonly string? Reason = reason;
}

[Serializable, NetSerializable]
public sealed class NCPoliceCreateCaseMessage(string title, string summary) : BoundUserInterfaceMessage
{
    public readonly string Title = title;
    public readonly string Summary = summary;
}

[Serializable, NetSerializable]
public sealed class NCPoliceSelectCaseMessage(long caseId) : BoundUserInterfaceMessage
{
    public readonly long CaseId = caseId;
}

[Serializable, NetSerializable]
public sealed class NCPoliceAddCaseSubjectMessage(
    long caseId,
    NCPoliceCaseSubjectRole role) : BoundUserInterfaceMessage
{
    public readonly long CaseId = caseId;
    public readonly NCPoliceCaseSubjectRole Role = role;
}

[Serializable, NetSerializable]
public sealed class NCPoliceAddCaseEntryMessage(long caseId, string text) : BoundUserInterfaceMessage
{
    public readonly long CaseId = caseId;
    public readonly string Text = text;
}

[Serializable, NetSerializable]
public sealed class NCPoliceChangeCaseStatusMessage(
    long caseId,
    NCPoliceCaseStatus status,
    string reason) : BoundUserInterfaceMessage
{
    public readonly long CaseId = caseId;
    public readonly NCPoliceCaseStatus Status = status;
    public readonly string Reason = reason;
}

[Serializable, NetSerializable]
public sealed class NCPoliceCreateWarrantMessage(
    NCPoliceWarrantType type,
    string reason,
    long? caseId) : BoundUserInterfaceMessage
{
    public readonly NCPoliceWarrantType Type = type;
    public readonly string Reason = reason;
    public readonly long? CaseId = caseId;
}

[Serializable, NetSerializable]
public sealed class NCPoliceResolveWarrantMessage(
    long warrantId,
    NCPoliceWarrantStatus status,
    string reason) : BoundUserInterfaceMessage
{
    public readonly long WarrantId = warrantId;
    public readonly NCPoliceWarrantStatus Status = status;
    public readonly string Reason = reason;
}

[Serializable, NetSerializable]
public sealed class NCPoliceCreateFineMessage(string article, string reason, int amount) : BoundUserInterfaceMessage
{
    public readonly string Article = article;
    public readonly string Reason = reason;
    public readonly int Amount = amount;
}

[Serializable, NetSerializable]
public sealed class NCPoliceSetFineStatusMessage(long fineId, NCPoliceFineStatus status, string reason)
    : BoundUserInterfaceMessage
{
    public readonly long FineId = fineId;
    public readonly NCPoliceFineStatus Status = status;
    public readonly string Reason = reason;
}
