// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Personnel.UI;

[Serializable, NetSerializable]
public enum NCPersonnelConsoleUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed record NCPersonnelPositionSummary(string JobId, int Occupied, int Limit, bool CanAssign);

[Serializable, NetSerializable]
public sealed record NCPersonnelEmployeeSummary(
    int CharacterId, string CharacterName, string JobId, DateTime StartedAt);

[Serializable, NetSerializable]
public sealed record NCPersonnelCandidateSummary(int CharacterId, string CharacterName);

[Serializable, NetSerializable]
public sealed record NCPersonnelHistorySummary(
    string? PreviousJobId, string? NewJobId, string Action, string Reason,
    string ActorName, DateTime CreatedAt);

/// <summary>Sent directly to one user so searches and selections never leak between console users.</summary>
[Serializable, NetSerializable]
public sealed class NCPersonnelConsoleUpdateMessage(
    string departmentId,
    List<NCPersonnelPositionSummary> positions,
    List<NCPersonnelEmployeeSummary> employees,
    List<NCPersonnelCandidateSummary> candidates,
    int? selectedCharacterId,
    List<NCPersonnelHistorySummary> history,
    bool canManage) : BoundUserInterfaceMessage
{
    public readonly string DepartmentId = departmentId;
    public readonly List<NCPersonnelPositionSummary> Positions = positions;
    public readonly List<NCPersonnelEmployeeSummary> Employees = employees;
    public readonly List<NCPersonnelCandidateSummary> Candidates = candidates;
    public readonly int? SelectedCharacterId = selectedCharacterId;
    public readonly List<NCPersonnelHistorySummary> History = history;
    public readonly bool CanManage = canManage;
}

[Serializable, NetSerializable]
public sealed class NCPersonnelSearchMessage(string query) : BoundUserInterfaceMessage
{
    public readonly string Query = query;
}

[Serializable, NetSerializable]
public sealed class NCPersonnelSelectCharacterMessage(int characterId) : BoundUserInterfaceMessage
{
    public readonly int CharacterId = characterId;
}

[Serializable, NetSerializable]
public sealed class NCPersonnelHireMessage(int characterId, string jobId, string reason) : BoundUserInterfaceMessage
{
    public readonly int CharacterId = characterId;
    public readonly string JobId = jobId;
    public readonly string Reason = reason;
}

[Serializable, NetSerializable]
public sealed class NCPersonnelTerminateMessage(int characterId, string reason) : BoundUserInterfaceMessage
{
    public readonly int CharacterId = characterId;
    public readonly string Reason = reason;
}

[Serializable, NetSerializable]
public sealed class NCPersonnelChangePositionMessage(int characterId, string jobId, string reason)
    : BoundUserInterfaceMessage
{
    public readonly int CharacterId = characterId;
    public readonly string JobId = jobId;
    public readonly string Reason = reason;
}
