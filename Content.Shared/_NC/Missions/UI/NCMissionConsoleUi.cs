// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Missions.UI;

[Serializable, NetSerializable]
public enum NCMissionConsoleUiKey : byte
{
    Key,
}

/// <summary>Per-user server-authoritative view of one public mission terminal.</summary>
[Serializable, NetSerializable]
public sealed class NCMissionConsoleUpdateMessage(
    string definitionId,
    NCMissionSlotId slotId,
    bool hasRun,
    NCMissionRunState state,
    int? remainingSeconds,
    int? warningInSeconds,
    bool canStart,
    bool canFinish,
    string notice) : BoundUserInterfaceMessage
{
    public readonly string DefinitionId = definitionId;
    public readonly NCMissionSlotId SlotId = slotId;
    public readonly bool HasRun = hasRun;
    public readonly NCMissionRunState State = state;
    public readonly int? RemainingSeconds = remainingSeconds;
    public readonly int? WarningInSeconds = warningInSeconds;
    public readonly bool CanStart = canStart;
    public readonly bool CanFinish = canFinish;
    public readonly string Notice = notice;
}

[Serializable, NetSerializable]
public sealed class NCMissionConsoleStartMessage : BoundUserInterfaceMessage
{
}

[Serializable, NetSerializable]
public sealed class NCMissionConsoleFinishMessage : BoundUserInterfaceMessage
{
}
