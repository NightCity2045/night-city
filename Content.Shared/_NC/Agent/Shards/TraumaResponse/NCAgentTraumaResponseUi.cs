// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Coordinates;
using Content.Shared.CartridgeLoader;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Agent.Shards.TraumaResponse;

[Serializable, NetSerializable]
public enum NCTraumaResponseUrgency : byte
{
    Wounded,
    Critical,
    UnderFire,
}

[Serializable, NetSerializable]
public enum NCTraumaResponseCallStatus : byte
{
    Requested,
    Accepted,
    Completed,
    Rejected,
    Cancelled,
    Expired,
}

[Serializable, NetSerializable]
public enum NCTraumaResponseAction : byte
{
    Refresh,
    Accept,
    Reject,
    Complete,
}

[Serializable, NetSerializable]
public sealed record NCTraumaResponseCallSummary(
    Guid Id,
    long Version,
    int ClientCharacterId,
    string ClientName,
    string PlanId,
    int Priority,
    int TreatmentCoverageBasisPoints,
    int MaximumThreatBasisPoints,
    NCTraumaResponseUrgency Urgency,
    NCTraumaResponseCallStatus Status,
    string Description,
    string Zone,
    NCMapCoordinates? Coordinates,
    long WaitingSeconds,
    string LeadName,
    List<string> Participants,
    bool ViewerAssigned);

[Serializable, NetSerializable]
public sealed record NCTraumaResponseResponderSummary(
    int CharacterId,
    string Name,
    string JobId,
    bool Busy);

[Serializable, NetSerializable]
public sealed class NCAgentTraumaResponseUiState(
    bool authorized,
    string viewerJobId,
    List<NCTraumaResponseCallSummary> queue,
    NCTraumaResponseCallSummary? activeCall,
    List<NCTraumaResponseCallSummary> recentCalls,
    List<NCTraumaResponseResponderSummary> responders,
    string notice) : BoundUserInterfaceState
{
    public readonly bool Authorized = authorized;
    public readonly string ViewerJobId = viewerJobId;
    public readonly List<NCTraumaResponseCallSummary> Queue = queue;
    public readonly NCTraumaResponseCallSummary? ActiveCall = activeCall;
    public readonly List<NCTraumaResponseCallSummary> RecentCalls = recentCalls;
    public readonly List<NCTraumaResponseResponderSummary> Responders = responders;
    public readonly string Notice = notice;
}

[Serializable, NetSerializable]
public sealed class NCAgentTraumaResponseActionMessage(
    NCTraumaResponseAction action,
    Guid callId,
    long expectedVersion) : CartridgeMessageEvent
{
    public readonly NCTraumaResponseAction Action = action;
    public readonly Guid CallId = callId;
    public readonly long ExpectedVersion = expectedVersion;
}

[Serializable, NetSerializable]
public sealed record NCTraumaResponseClientCallSummary(
    Guid Id,
    long Version,
    NCTraumaResponseCallStatus Status,
    string PlanId,
    string Zone,
    string LeadName,
    List<string> Participants);
