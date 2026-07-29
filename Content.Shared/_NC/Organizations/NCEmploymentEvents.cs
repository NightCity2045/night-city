// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Organizations;

[Serializable, NetSerializable]
public enum NCEmploymentActionType : byte
{
    Hire,
    Promote,
    Demote,
    Transfer,
    Suspend,
    Reinstate,
    Dismiss,
}

[Serializable, NetSerializable]
public sealed class NCEmploymentActionRequest : EntityEventArgs
{
    public NetEntity Target { get; }
    public NCEmploymentActionType Action { get; }
    public string? OrganizationPrototypeId { get; }
    public string? PositionPrototypeId { get; }
    public string Reason { get; }
    public Guid RequestId { get; }
    public bool PaidSuspension { get; }
    public long ExpectedVersion { get; }

    public NCEmploymentActionRequest(
        NetEntity target,
        NCEmploymentActionType action,
        string? organizationPrototypeId,
        string? positionPrototypeId,
        string reason,
        Guid requestId,
        bool paidSuspension = false,
        long expectedVersion = 0)
    {
        Target = target;
        Action = action;
        OrganizationPrototypeId = organizationPrototypeId;
        PositionPrototypeId = positionPrototypeId;
        Reason = reason;
        RequestId = requestId;
        PaidSuspension = paidSuspension;
        ExpectedVersion = expectedVersion;
    }
}

[Serializable, NetSerializable]
public sealed class NCEmploymentActionResponse : EntityEventArgs
{
    public bool Success { get; }
    public string? Error { get; }

    public NCEmploymentActionResponse(bool success, string? error)
    {
        Success = success;
        Error = error;
    }
}

[Serializable, NetSerializable]
public sealed record NCEmploymentHistorySummary(
    string Action,
    string? OldPositionPrototypeId,
    string? NewPositionPrototypeId,
    string Reason,
    DateTime Timestamp);

[Serializable, NetSerializable]
public sealed record NCHROnlineCharacterSummary(
    NetEntity Character,
    string Name,
    string? PositionPrototypeId,
    byte EmploymentState);

[Serializable, NetSerializable]
public sealed class NCHROnlineListRequest : EntityEventArgs;

[Serializable, NetSerializable]
public sealed class NCHROpenFileRequest : EntityEventArgs
{
    public NetEntity Target { get; }

    public NCHROpenFileRequest(NetEntity target)
    {
        Target = target;
    }
}

[Serializable, NetSerializable]
public sealed class NCHROnlineListState : EntityEventArgs
{
    public string OrganizationPrototypeId { get; }
    public NCHROnlineCharacterSummary[] Characters { get; }

    public NCHROnlineListState(
        string organizationPrototypeId,
        NCHROnlineCharacterSummary[] characters)
    {
        OrganizationPrototypeId = organizationPrototypeId;
        Characters = characters;
    }
}

[Serializable, NetSerializable]
public sealed class NCHRPanelState : EntityEventArgs
{
    public NetEntity Target { get; }
    public string TargetName { get; }
    public string OrganizationPrototypeId { get; }
    public string? CurrentPositionPrototypeId { get; }
    public byte EmploymentState { get; }
    public bool CanHire { get; }
    public bool CanPromote { get; }
    public bool CanDemote { get; }
    public bool CanTransfer { get; }
    public bool CanSuspend { get; }
    public bool CanDismiss { get; }
    public int? MaxPromotableRankWeight { get; }
    public long EmploymentVersion { get; }
    public NCEmploymentHistorySummary[] History { get; }

    public NCHRPanelState(
        NetEntity target,
        string targetName,
        string organizationPrototypeId,
        string? currentPositionPrototypeId,
        byte employmentState,
        bool canHire,
        bool canPromote,
        bool canDemote,
        bool canTransfer,
        bool canSuspend,
        bool canDismiss,
        int? maxPromotableRankWeight,
        long employmentVersion,
        NCEmploymentHistorySummary[] history)
    {
        Target = target;
        TargetName = targetName;
        OrganizationPrototypeId = organizationPrototypeId;
        CurrentPositionPrototypeId = currentPositionPrototypeId;
        EmploymentState = employmentState;
        CanHire = canHire;
        CanPromote = canPromote;
        CanDemote = canDemote;
        CanTransfer = canTransfer;
        CanSuspend = canSuspend;
        CanDismiss = canDismiss;
        MaxPromotableRankWeight = maxPromotableRankWeight;
        EmploymentVersion = employmentVersion;
        History = history;
    }
}
