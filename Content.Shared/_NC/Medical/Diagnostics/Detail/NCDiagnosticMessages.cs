// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Diagnostics.Detail;

/// <summary>
/// Local server event raised after a vanilla health analyzer completes its authoritative scan.
/// Kept separate from Agent MEDTEK storage so each ECS subscription has a unique event contract.
/// </summary>
[ByRefEvent]
public record struct NCHealthAnalyzerScanCompletedEvent(EntityUid User, EntityUid Target);

[Serializable, NetSerializable]
public enum NCDiagnosticAccessFailure : byte
{
    None,
    ActorUnavailable,
    TargetUnavailable,
    TargetNotMedicalBody,
    TierUnavailable,
    AdminServerOnly,
    CapabilityUnavailable,
    InvalidResolution,
    ToolRequired,
    ToolNotOperational,
    ToolNotHeld,
    ToolUnpowered,
    SelfOnly,
    OutOfRange,
    OperatingTableDisconnected,
    OperatingTableEmpty,
    OperatingTablePatientChanged,
    RateLimited,
}

/// <summary>
/// An untrusted UI request. Target and tool are resolved and fully validated against the sending session.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCDiagnosticSnapshotRequest(
    uint requestId,
    NetEntity target,
    NetEntity? tool,
    NCDiagnosticTier tier) : EntityEventArgs
{
    public uint RequestId { get; } = requestId;
    public NetEntity Target { get; } = target;
    public NetEntity? Tool { get; } = tool;
    public NCDiagnosticTier Tier { get; } = tier;
}

/// <summary>
/// Directed only to the requesting session; never broadcast or sent through PVS.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCDiagnosticSnapshotResponse(
    uint requestId,
    NCActionResolutionId resolutionId,
    bool accepted,
    NCDiagnosticAccessFailure failure,
    NetEntity? target,
    NetEntity? tool,
    NCDiagnosticTier tier,
    NCMedicalSnapshot? snapshot) : EntityEventArgs
{
    public uint RequestId { get; } = requestId;
    public NCActionResolutionId ResolutionId { get; } = resolutionId;
    public bool Accepted { get; } = accepted;
    public NCDiagnosticAccessFailure Failure { get; } = failure;
    public NetEntity? Target { get; } = target;
    public NetEntity? Tool { get; } = tool;
    public NCDiagnosticTier Tier { get; } = tier;
    public NCMedicalSnapshot? Snapshot { get; } = snapshot;
}
