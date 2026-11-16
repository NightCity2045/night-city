// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.ZLevels.Elevators;

/// <summary>A panel controls only the elevator grid on which it is mounted.</summary>
[RegisterComponent]
public sealed partial class NCElevatorPanelComponent : Component;

/// <summary>Generated cabin fittings are rebuilt by their persistent mapper controller.</summary>
[RegisterComponent]
public sealed partial class NCElevatorCabinPartComponent : Component;

[Serializable, NetSerializable]
public enum NCElevatorUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class NCElevatorMoveMessage(int offset) : BoundUserInterfaceMessage
{
    public readonly int Offset = offset;
}

[Serializable, NetSerializable]
public sealed class NCElevatorUiState(
    int? depth,
    int? targetDepth,
    bool canUp,
    bool canDown,
    double travelSeconds,
    string? feedback = null) : BoundUserInterfaceState
{
    public readonly int? Depth = depth;
    public readonly int? TargetDepth = targetDepth;
    public readonly bool CanUp = canUp;
    public readonly bool CanDown = canDown;
    public readonly double TravelSeconds = travelSeconds;
    public readonly string? Feedback = feedback;
}
