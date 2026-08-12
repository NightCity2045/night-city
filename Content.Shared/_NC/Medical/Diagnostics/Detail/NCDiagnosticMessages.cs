// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Diagnostics.Detail;

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
    NCMedicalSnapshot? snapshot) : EntityEventArgs
{
    public uint RequestId { get; } = requestId;
    public NCActionResolutionId ResolutionId { get; } = resolutionId;
    public bool Accepted { get; } = accepted;
    public NCDiagnosticAccessFailure Failure { get; } = failure;
    public NCMedicalSnapshot? Snapshot { get; } = snapshot;
}
