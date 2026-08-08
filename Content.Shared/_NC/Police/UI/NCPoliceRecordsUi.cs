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

/// <summary>
/// Per-user console update. A direct BUI message avoids the vanilla console problem where users share one selection.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCPoliceRecordsUpdateMessage(
    List<NCPoliceRecordSummary> searchResults,
    NCPoliceRecordSummary? selectedRecord,
    List<NCPoliceRecordHistoryEntry> history,
    bool canEdit) : BoundUserInterfaceMessage
{
    public readonly List<NCPoliceRecordSummary> SearchResults = searchResults;
    public readonly NCPoliceRecordSummary? SelectedRecord = selectedRecord;
    public readonly List<NCPoliceRecordHistoryEntry> History = history;
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
