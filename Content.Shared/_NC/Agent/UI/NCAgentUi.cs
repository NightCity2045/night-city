// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Agent;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Agent.UI;

[Serializable, NetSerializable]
public enum NCAgentApp : byte
{
    Home,
    Contacts,
    Messages,
    Calls,
    CitiNet,
    Map,
}

[Serializable, NetSerializable]
public enum NCAgentCallState : byte
{
    None,
    Incoming,
    Outgoing,
    Connected,
}

[Serializable, NetSerializable]
public enum NCAgentCallOutcome : byte
{
    Completed,
    Rejected,
    Missed,
    Cancelled,
    Busy,
    SignalLost,
}

[Serializable, NetSerializable]
public enum NCAgentMapFeatureKind : byte
{
    District,
    Sector,
    Street,
    Building,
}

[Serializable, NetSerializable]
public enum NCAgentMapGeometryKind : byte
{
    Polygon,
    TileMask,
}

/// <summary>
/// One compressed 32x32 tile-mask fragment. Each row stores occupied tiles as bits.
/// </summary>
[Serializable, NetSerializable]
public sealed record NCAgentMapTileChunk(int OriginX, int OriginY, List<ulong> Rows);

/// <summary>
/// Public cartography fragment derived from server-only city-zone geometry.
/// </summary>
[Serializable, NetSerializable]
public sealed record NCAgentMapFeature(
    NCAgentMapFeatureKind Kind,
    NCAgentMapGeometryKind GeometryKind,
    string Name,
    List<Vector2> Vertices,
    List<NCAgentMapTileChunk> Chunks);

[Serializable, NetSerializable]
public sealed record NCAgentDirectoryEntry(
    int CharacterId,
    string Alias,
    string Address,
    bool IsContact,
    NCAgentPresenceStatus Presence,
    NCAgentNetworkState NetworkState);

[Serializable, NetSerializable]
public sealed record NCAgentContactSummary(
    int CharacterId,
    string Alias,
    string Address,
    NCAgentPresenceStatus Presence,
    NCAgentNetworkState NetworkState);

[Serializable, NetSerializable]
public sealed record NCAgentNearbyUserSummary(int CharacterId, string Alias, string Address);

[Serializable, NetSerializable]
public sealed record NCAgentBlockedSummary(int CharacterId, string Alias, string Address);

[Serializable, NetSerializable]
public sealed record NCAgentConversationSummary(
    int CharacterId,
    string Alias,
    string Address,
    DateTime LastMessageAt,
    int UnreadCount);

[Serializable, NetSerializable]
public sealed record NCAgentMessageSummary(
    long MessageId,
    int SenderCharacterId,
    string SenderAlias,
    int RecipientCharacterId,
    string RecipientAlias,
    string Body,
    DateTime SentAt,
    bool IsOwn);

[Serializable, NetSerializable]
public sealed record NCAgentCitiNetCommentSummary(
    long CommentId,
    int AuthorCharacterId,
    string AuthorAlias,
    string Body,
    DateTime CreatedAt);

[Serializable, NetSerializable]
public sealed record NCAgentCitiNetPostSummary(
    long PostId,
    int AuthorCharacterId,
    string AuthorAlias,
    string Body,
    DateTime CreatedAt,
    int ReactionCount,
    bool ReactedByViewer,
    bool IsOwn,
    List<NCAgentCitiNetCommentSummary> Comments);

[Serializable, NetSerializable]
public sealed record NCAgentCallSummary(
    NCAgentCallState State,
    int? RemoteCharacterId,
    string RemoteAlias,
    string RemoteAddress);

[Serializable, NetSerializable]
public sealed record NCAgentCallHistorySummary(
    long CallId,
    bool IsOutgoing,
    int RemoteCharacterId,
    string RemoteAlias,
    string RemoteAddress,
    DateTime StartedAt,
    DateTime? AnsweredAt,
    DateTime EndedAt,
    NCAgentCallOutcome Outcome,
    bool IsUnread);

[Serializable, NetSerializable]
public sealed record NCAgentGroupSummary(
    long GroupId,
    string Name,
    string OwnerAlias,
    NCAgentGroupRole Role,
    DateTime LastMessageAt,
    int UnreadCount);

[Serializable, NetSerializable]
public sealed record NCAgentGroupMemberSummary(
    int CharacterId,
    string Alias,
    string Address,
    NCAgentGroupRole Role,
    bool IsContact);

[Serializable, NetSerializable]
public sealed record NCAgentGroupMessageSummary(
    long MessageId,
    long GroupId,
    int SenderCharacterId,
    string SenderAlias,
    string Body,
    DateTime SentAt,
    bool IsOwn);

[Serializable, NetSerializable]
public sealed record NCAgentGroupInviteSummary(
    long GroupId,
    string GroupName,
    int InviterCharacterId,
    string InviterAlias,
    DateTime ExpiresAt);

/// <summary>
/// Per-user Agent data sent separately from the vanilla PDA state and cartridge state.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCAgentUpdateMessage(
    NCAgentApp activeApp,
    int ownCharacterId,
    string ownAlias,
    string ownAddress,
    NCAgentPresenceStatus presence,
    NCAgentNetworkState networkState,
    bool overlayNotificationsEnabled,
    bool notificationSoundEnabled,
    List<NCAgentDirectoryEntry> directory,
    List<NCAgentContactSummary> contacts,
    List<NCAgentNearbyUserSummary> nearbyUsers,
    List<NCAgentBlockedSummary> blocked,
    List<NCAgentConversationSummary> conversations,
    int? selectedCharacterId,
    List<NCAgentMessageSummary> messages,
    List<NCAgentCitiNetPostSummary> posts,
    int feedOffset,
    bool feedHasNewer,
    bool feedHasOlder,
    NCAgentCallSummary call,
    List<NCAgentCallHistorySummary> callHistory,
    List<NCAgentGroupSummary> groups,
    long? selectedGroupId,
    List<NCAgentGroupMemberSummary> groupMembers,
    List<NCAgentGroupMessageSummary> groupMessages,
    List<NCAgentGroupInviteSummary> groupInvites,
    int unreadMessageCount,
    int unreadGroupMessageCount,
    int missedCallCount,
    string notice) : BoundUserInterfaceMessage
{
    public readonly NCAgentApp ActiveApp = activeApp;
    public readonly int OwnCharacterId = ownCharacterId;
    public readonly string OwnAlias = ownAlias;
    public readonly string OwnAddress = ownAddress;
    public readonly NCAgentPresenceStatus Presence = presence;
    public readonly NCAgentNetworkState NetworkState = networkState;
    public readonly bool OverlayNotificationsEnabled = overlayNotificationsEnabled;
    public readonly bool NotificationSoundEnabled = notificationSoundEnabled;
    public readonly List<NCAgentDirectoryEntry> Directory = directory;
    public readonly List<NCAgentContactSummary> Contacts = contacts;
    public readonly List<NCAgentNearbyUserSummary> NearbyUsers = nearbyUsers;
    public readonly List<NCAgentBlockedSummary> Blocked = blocked;
    public readonly List<NCAgentConversationSummary> Conversations = conversations;
    public readonly int? SelectedCharacterId = selectedCharacterId;
    public readonly List<NCAgentMessageSummary> Messages = messages;
    public readonly List<NCAgentCitiNetPostSummary> Posts = posts;
    public readonly int FeedOffset = feedOffset;
    public readonly bool FeedHasNewer = feedHasNewer;
    public readonly bool FeedHasOlder = feedHasOlder;
    public readonly NCAgentCallSummary Call = call;
    public readonly List<NCAgentCallHistorySummary> CallHistory = callHistory;
    public readonly List<NCAgentGroupSummary> Groups = groups;
    public readonly long? SelectedGroupId = selectedGroupId;
    public readonly List<NCAgentGroupMemberSummary> GroupMembers = groupMembers;
    public readonly List<NCAgentGroupMessageSummary> GroupMessages = groupMessages;
    public readonly List<NCAgentGroupInviteSummary> GroupInvites = groupInvites;
    public readonly int UnreadMessageCount = unreadMessageCount;
    public readonly int UnreadGroupMessageCount = unreadGroupMessageCount;
    public readonly int MissedCallCount = missedCallCount;
    public readonly string Notice = notice;
}

/// <summary>
/// City geometry is sent only when the map app opens so regular Agent updates remain small.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCAgentMapUpdateMessage(
    string networkId,
    int z,
    Vector2 userPosition,
    List<NCAgentMapFeature> features,
    string notice) : BoundUserInterfaceMessage
{
    public readonly string NetworkId = networkId;
    public readonly int Z = z;
    public readonly Vector2 UserPosition = userPosition;
    public readonly List<NCAgentMapFeature> Features = features;
    public readonly string Notice = notice;
}

[Serializable, NetSerializable]
public sealed class NCAgentOpenAppMessage(NCAgentApp app) : BoundUserInterfaceMessage
{
    public readonly NCAgentApp App = app;
}

[Serializable, NetSerializable]
public sealed class NCAgentDirectorySearchMessage(string query) : BoundUserInterfaceMessage
{
    public readonly string Query = query;
}

[Serializable, NetSerializable]
public sealed class NCAgentSetAliasMessage(string alias) : BoundUserInterfaceMessage
{
    public readonly string Alias = alias;
}

[Serializable, NetSerializable]
public sealed class NCAgentSetPresenceMessage(NCAgentPresenceStatus presence) : BoundUserInterfaceMessage
{
    public readonly NCAgentPresenceStatus Presence = presence;
}

[Serializable, NetSerializable]
public sealed class NCAgentSetNotificationSettingsMessage(
    bool overlayEnabled,
    bool soundEnabled) : BoundUserInterfaceMessage
{
    public readonly bool OverlayEnabled = overlayEnabled;
    public readonly bool SoundEnabled = soundEnabled;
}

[Serializable, NetSerializable]
public sealed class NCAgentAddContactMessage(int characterId) : BoundUserInterfaceMessage
{
    public readonly int CharacterId = characterId;
}

[Serializable, NetSerializable]
public sealed class NCAgentRemoveContactMessage(int characterId) : BoundUserInterfaceMessage
{
    public readonly int CharacterId = characterId;
}

[Serializable, NetSerializable]
public sealed class NCAgentRequestContactExchangeMessage(int characterId) : BoundUserInterfaceMessage
{
    public readonly int CharacterId = characterId;
}

[Serializable, NetSerializable]
public sealed class NCAgentSetBlockedMessage(int characterId, bool blocked) : BoundUserInterfaceMessage
{
    public readonly int CharacterId = characterId;
    public readonly bool Blocked = blocked;
}

[Serializable, NetSerializable]
public sealed class NCAgentClearConversationMessage(int characterId) : BoundUserInterfaceMessage
{
    public readonly int CharacterId = characterId;
}

[Serializable, NetSerializable]
public sealed class NCAgentSelectConversationMessage(int characterId) : BoundUserInterfaceMessage
{
    public readonly int CharacterId = characterId;
}

[Serializable, NetSerializable]
public sealed class NCAgentSendMessageMessage(int recipientCharacterId, string body) : BoundUserInterfaceMessage
{
    public readonly int RecipientCharacterId = recipientCharacterId;
    public readonly string Body = body;
}

[Serializable, NetSerializable]
public sealed class NCAgentCreateGroupMessage(string name, List<int> memberCharacterIds) : BoundUserInterfaceMessage
{
    public readonly string Name = name;
    public readonly List<int> MemberCharacterIds = memberCharacterIds;
}

[Serializable, NetSerializable]
public sealed class NCAgentSelectGroupMessage(long groupId) : BoundUserInterfaceMessage
{
    public readonly long GroupId = groupId;
}

[Serializable, NetSerializable]
public sealed class NCAgentSendGroupMessage(long groupId, string body) : BoundUserInterfaceMessage
{
    public readonly long GroupId = groupId;
    public readonly string Body = body;
}

[Serializable, NetSerializable]
public sealed class NCAgentInviteGroupMemberMessage(long groupId, int characterId) : BoundUserInterfaceMessage
{
    public readonly long GroupId = groupId;
    public readonly int CharacterId = characterId;
}

[Serializable, NetSerializable]
public sealed class NCAgentRespondGroupInviteMessage(long groupId, bool accept) : BoundUserInterfaceMessage
{
    public readonly long GroupId = groupId;
    public readonly bool Accept = accept;
}

[Serializable, NetSerializable]
public sealed class NCAgentRemoveGroupMemberMessage(long groupId, int characterId) : BoundUserInterfaceMessage
{
    public readonly long GroupId = groupId;
    public readonly int CharacterId = characterId;
}

[Serializable, NetSerializable]
public sealed class NCAgentLeaveGroupMessage(long groupId) : BoundUserInterfaceMessage
{
    public readonly long GroupId = groupId;
}

[Serializable, NetSerializable]
public sealed class NCAgentClearGroupConversationMessage(long groupId) : BoundUserInterfaceMessage
{
    public readonly long GroupId = groupId;
}

[Serializable, NetSerializable]
public sealed class NCAgentCreatePostMessage(string body) : BoundUserInterfaceMessage
{
    public readonly string Body = body;
}

[Serializable, NetSerializable]
public sealed class NCAgentCreateCommentMessage(long postId, string body) : BoundUserInterfaceMessage
{
    public readonly long PostId = postId;
    public readonly string Body = body;
}

[Serializable, NetSerializable]
public sealed class NCAgentToggleReactionMessage(long postId) : BoundUserInterfaceMessage
{
    public readonly long PostId = postId;
}

[Serializable, NetSerializable]
public sealed class NCAgentDeletePostMessage(long postId) : BoundUserInterfaceMessage
{
    public readonly long PostId = postId;
}

[Serializable, NetSerializable]
public sealed class NCAgentSetFeedOffsetMessage(int offset) : BoundUserInterfaceMessage
{
    public readonly int Offset = offset;
}

[Serializable, NetSerializable]
public sealed class NCAgentStartCallMessage(int targetCharacterId) : BoundUserInterfaceMessage
{
    public readonly int TargetCharacterId = targetCharacterId;
}

[Serializable, NetSerializable]
public sealed class NCAgentAnswerCallMessage(bool accept) : BoundUserInterfaceMessage
{
    public readonly bool Accept = accept;
}

[Serializable, NetSerializable]
public sealed class NCAgentEndCallMessage : BoundUserInterfaceMessage;
