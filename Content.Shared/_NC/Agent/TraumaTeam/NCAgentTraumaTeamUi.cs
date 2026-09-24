// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;
using Content.Shared._NC.Agent.Shards.TraumaResponse;

namespace Content.Shared._NC.Agent.TraumaTeam;

[Serializable, NetSerializable]
public sealed record NCAgentTraumaTeamJobSummary(
    string JobId,
    string Name,
    string Description,
    int Salary,
    int Occupied,
    int Limit,
    bool Available);

[Serializable, NetSerializable]
public sealed record NCAgentTraumaTeamPlanSummary(
    string PlanId,
    string Name,
    string Description,
    long PricePerDay,
    int DispatchPriority,
    int TreatmentCoverageBasisPoints,
    int MaximumThreatBasisPoints,
    string Color);

[Serializable, NetSerializable]
public sealed class NCAgentTraumaTeamUpdateMessage(
    string? currentJobId,
    List<NCAgentTraumaTeamJobSummary> jobs,
    List<NCAgentTraumaTeamPlanSummary> plans,
    long balance,
    string? activePlanId,
    long remainingSeconds,
    int minimumDays,
    int maximumDays,
    NCTraumaResponseClientCallSummary? responseCall,
    string notice) : BoundUserInterfaceMessage
{
    public readonly string? CurrentJobId = currentJobId;
    public readonly List<NCAgentTraumaTeamJobSummary> Jobs = jobs;
    public readonly List<NCAgentTraumaTeamPlanSummary> Plans = plans;
    public readonly long Balance = balance;
    public readonly string? ActivePlanId = activePlanId;
    public readonly long RemainingSeconds = remainingSeconds;
    public readonly int MinimumDays = minimumDays;
    public readonly int MaximumDays = maximumDays;
    public readonly NCTraumaResponseClientCallSummary? ResponseCall = responseCall;
    public readonly string Notice = notice;
}

[Serializable, NetSerializable]
public sealed class NCAgentTraumaTeamHireMessage(string jobId) : BoundUserInterfaceMessage
{
    public readonly string JobId = jobId;
}

[Serializable, NetSerializable]
public sealed class NCAgentTraumaTeamPurchaseMessage(Guid commandId, string planId, int days)
    : BoundUserInterfaceMessage
{
    public readonly Guid CommandId = commandId;
    public readonly string PlanId = planId;
    public readonly int Days = days;
}

[Serializable, NetSerializable]
public sealed class NCAgentTraumaTeamRefreshMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class NCAgentTraumaTeamRequestResponseMessage(
    NCTraumaResponseUrgency urgency,
    string description) : BoundUserInterfaceMessage
{
    public readonly NCTraumaResponseUrgency Urgency = urgency;
    public readonly string Description = description;
}

[Serializable, NetSerializable]
public sealed class NCAgentTraumaTeamCancelResponseMessage(Guid callId, long expectedVersion)
    : BoundUserInterfaceMessage
{
    public readonly Guid CallId = callId;
    public readonly long ExpectedVersion = expectedVersion;
}
