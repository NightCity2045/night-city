// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Pharmacology.Lab;

[Serializable, NetSerializable]
public enum NCLabMachineUiKey : byte
{
    Key,
}

/// <summary>Whether a laboratory machine is idle, running a procedure or holding a result for collection.</summary>
[Serializable, NetSerializable]
public enum NCLabMachinePhase : byte
{
    Idle,
    Working,
    Ready,
}

/// <summary>One slot of a machine and what stands in it. The item is shown by its own name on the client.</summary>
[Serializable, NetSerializable]
public readonly record struct NCLabMachineSlot(string Id, string NameKey, NetEntity? Item);

/// <summary>One line of what the machine reports: a label, and a value that is either plain or a localized phrase.</summary>
[Serializable, NetSerializable]
public readonly record struct NCLabMachineReadout(string LabelKey, string Value, string? ValueKey = null);

/// <summary>Everything the window of a laboratory machine shows.</summary>
[Serializable, NetSerializable]
public sealed class NCLabMachineState(
    bool powered,
    NCLabMachinePhase phase,
    float progress,
    int remainingSeconds,
    bool canStart,
    string startKey,
    NCLabMachineSlot[] slots,
    NCLabMachineReadout[] readouts) : BoundUserInterfaceState
{
    public readonly bool Powered = powered;
    public readonly NCLabMachinePhase Phase = phase;
    public readonly float Progress = progress;
    public readonly int RemainingSeconds = remainingSeconds;
    public readonly bool CanStart = canStart;

    /// <summary>The localization id of the start button: start, collect and so on.</summary>
    public readonly string StartKey = startKey;

    public readonly NCLabMachineSlot[] Slots = slots;
    public readonly NCLabMachineReadout[] Readouts = readouts;
}

/// <summary>Starts the machine's procedure. The server checks everything again.</summary>
[Serializable, NetSerializable]
public sealed class NCLabMachineStartMessage : BoundUserInterfaceMessage;

/// <summary>Stops the procedure the machine is running.</summary>
[Serializable, NetSerializable]
public sealed class NCLabMachineCancelMessage : BoundUserInterfaceMessage;

/// <summary>Takes the item out of one slot of the machine.</summary>
[Serializable, NetSerializable]
public sealed class NCLabMachineEjectMessage(string slot) : BoundUserInterfaceMessage
{
    public readonly string Slot = slot;
}

/// <summary>What a laboratory machine shows from outside: whether it is working.</summary>
[Serializable, NetSerializable]
public enum NCLabMachineVisuals : byte
{
    Working,
}

/// <summary>The sprite layer that shows the machine at work.</summary>
[Serializable, NetSerializable]
public enum NCLabMachineVisualLayers : byte
{
    Working,
}
