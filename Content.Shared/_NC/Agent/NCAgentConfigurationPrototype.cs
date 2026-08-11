// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Agent;

/// <summary>
/// Data-driven limits and persistence policy for Agent services.
/// </summary>
[Prototype("ncAgentConfiguration")]
public sealed partial class NCAgentConfigurationPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public string AddressPrefix = string.Empty;

    [DataField(required: true)]
    public TimeSpan MessageRetention;

    [DataField]
    public int MaximumDirectoryQueryLength = 64;

    [DataField]
    public int MinimumAliasLength = 3;

    [DataField]
    public int MaximumAliasLength = 32;

    [DataField]
    public int MaximumMessageLength = 1024;

    [DataField]
    public int MaximumPostLength = 2048;

    [DataField]
    public int MaximumCommentLength = 1024;

    [DataField]
    public int DirectoryResultLimit = 30;

    [DataField]
    public int ConversationMessageLimit = 100;

    [DataField]
    public int ConversationPeerLimit = 50;

    [DataField]
    public TimeSpan IncomingCallTimeout = TimeSpan.FromSeconds(30);

    [DataField]
    public TimeSpan CallHistoryRetention = TimeSpan.FromDays(30);

    [DataField]
    public int CallHistoryLimit = 50;

    [DataField]
    public int MaximumGroupNameLength = 48;

    [DataField]
    public int MaximumGroupMembers = 20;

    [DataField]
    public int GroupConversationLimit = 30;

    [DataField]
    public int GroupMessageLimit = 100;

    [DataField]
    public TimeSpan ContactRequestTimeout = TimeSpan.FromSeconds(30);

    [DataField]
    public float ContactShareRange = 2f;

    [DataField]
    public TimeSpan GroupInviteRetention = TimeSpan.FromHours(24);

    [DataField]
    public TimeSpan NotificationOverlayDuration = TimeSpan.FromSeconds(6);

    [DataField]
    public int NotificationOverlayLimit = 3;

    [DataField]
    public bool ServiceAvailable = true;

    [DataField]
    public NCAgentNetworkState DefaultNetworkState = NCAgentNetworkState.Online;

    [DataField]
    public bool DegradedAllowsCalls;

    [DataField]
    public bool DegradedAllowsMessages = true;

    [DataField]
    public bool DegradedAllowsCitiNet = true;

    [DataField]
    public TimeSpan CitiNetPostRetention = TimeSpan.FromDays(30);

    [DataField]
    public TimeSpan CitiNetPostCooldown = TimeSpan.FromSeconds(10);

    [DataField]
    public TimeSpan CitiNetCommentCooldown = TimeSpan.FromSeconds(3);

    [DataField]
    public TimeSpan CitiNetReactionCooldown = TimeSpan.FromSeconds(1);

    [DataField]
    public int MaximumMentionsPerEntry = 10;

    [DataField]
    public int FeedPostLimit = 20;

    [DataField]
    public int FeedCommentLimit = 200;
}
