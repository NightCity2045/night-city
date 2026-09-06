// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Missions;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Agent.UI;

/// <summary>Per-view availability, including the exact run token required for cancellation.</summary>
[Serializable, NetSerializable]
public sealed record NCAgentMissionEntry(string DefinitionId, string Location, bool CanStart,
    string UnavailableReason, Guid? RunId, NCMissionRunState? State, int? RemainingSeconds, bool CanFinish);

[Serializable, NetSerializable]
public sealed class NCAgentMissionsUpdateMessage(List<NCAgentMissionEntry> entries, string notice)
    : BoundUserInterfaceMessage
{
    public readonly List<NCAgentMissionEntry> Entries = entries;
    public readonly string Notice = notice;
}

/// <summary>The server resolves recipes and slots; clients cannot supply participants or rewards.</summary>
[Serializable, NetSerializable]
public sealed class NCAgentMissionStartMessage(string definitionId) : BoundUserInterfaceMessage
{
    public readonly string DefinitionId = definitionId;
}

[Serializable, NetSerializable]
public sealed class NCAgentMissionFinishMessage(Guid runId) : BoundUserInterfaceMessage
{
    public readonly Guid RunId = runId;
}
