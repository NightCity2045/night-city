// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Pharmacology.Batch;

[Serializable, NetSerializable]
public enum NCPharmaQcUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum NCPharmaQcNotice : byte
{
    None,
    NoSample,
    NoDoseLeft,
    UnknownBatch,
    NoTestsSelected,
    Busy,
    Started,
    Finished,
}

/// <summary>
/// What the analyzer window shows. The pack's public label and the last measurement are the only batch data
/// that leave the server, and the measurement contains values for the performed tests alone.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCPharmaQcMachineState(
    bool samplePresent,
    int dosesLeft,
    NCPharmaLabel? label,
    NCPharmaQcReport? report,
    NCPharmaQcState batchState,
    bool busy,
    float progress,
    NCPharmaQcNotice notice) : BoundUserInterfaceState
{
    public readonly bool SamplePresent = samplePresent;
    public readonly int DosesLeft = dosesLeft;
    public readonly NCPharmaLabel? Label = label;
    public readonly NCPharmaQcReport? Report = report;
    public readonly NCPharmaQcState BatchState = batchState;
    public readonly bool Busy = busy;
    public readonly float Progress = progress;
    public readonly NCPharmaQcNotice Notice = notice;
}

[Serializable, NetSerializable]
public sealed class NCPharmaQcRunMessage(NCPharmaQcTests tests) : BoundUserInterfaceMessage
{
    public readonly NCPharmaQcTests Tests = tests;
}
