// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Agent;

[Serializable, NetSerializable]
public enum NCAgentPresenceStatus : byte
{
    Online,
    Busy,
    DoNotDisturb,
    Offline,
}

[Serializable, NetSerializable]
public enum NCAgentNetworkState : byte
{
    Online,
    Degraded,
    LocalOnly,
    Disconnected,
    ServiceUnavailable,
}

[Serializable, NetSerializable]
public enum NCAgentGroupRole : byte
{
    Member,
    Owner,
}

[Serializable, NetSerializable]
public enum NCAgentNotificationKind : byte
{
    DirectMessage,
    GroupMessage,
    IncomingCall,
    MissedCall,
    ContactRequest,
    GroupInvite,
    CitiNetComment,
    CitiNetMention,
    System,
}

[Serializable, NetSerializable]
public enum NCAgentNotificationAction : byte
{
    Open,
    Accept,
    Decline,
}

/// <summary>
/// Server-targeted HUD notification. It contains no direct-message body preview.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCAgentNotificationEvent(
    Guid notificationId,
    NCAgentNotificationKind kind,
    string title,
    string detail,
    bool actionable) : EntityEventArgs
{
    public readonly Guid NotificationId = notificationId;
    public readonly NCAgentNotificationKind Kind = kind;
    public readonly string Title = title;
    public readonly string Detail = detail;
    public readonly bool Actionable = actionable;
}

/// <summary>
/// The client only returns an opaque notification identifier; the server resolves and validates its action.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCAgentNotificationActionEvent(
    Guid notificationId,
    NCAgentNotificationAction action) : EntityEventArgs
{
    public readonly Guid NotificationId = notificationId;
    public readonly NCAgentNotificationAction Action = action;
}
