// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

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
public sealed record NCAgentDirectoryEntry(int CharacterId, string Name, string Address, bool IsContact);

[Serializable, NetSerializable]
public sealed record NCAgentContactSummary(int CharacterId, string Name, string Address);

[Serializable, NetSerializable]
public sealed record NCAgentMessageSummary(
    long MessageId,
    int SenderCharacterId,
    string SenderName,
    int RecipientCharacterId,
    string RecipientName,
    string Body,
    DateTime SentAt,
    bool IsOwn);

[Serializable, NetSerializable]
public sealed record NCAgentCitiNetCommentSummary(
    long CommentId,
    int AuthorCharacterId,
    string AuthorName,
    string Body,
    DateTime CreatedAt);

[Serializable, NetSerializable]
public sealed record NCAgentCitiNetPostSummary(
    long PostId,
    int AuthorCharacterId,
    string AuthorName,
    string Body,
    DateTime CreatedAt,
    int ReactionCount,
    bool ReactedByViewer,
    List<NCAgentCitiNetCommentSummary> Comments);

[Serializable, NetSerializable]
public sealed record NCAgentCallSummary(
    NCAgentCallState State,
    int? RemoteCharacterId,
    string RemoteName,
    string RemoteAddress);

/// <summary>
/// Per-user Agent data sent separately from the vanilla PDA state and cartridge state.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCAgentUpdateMessage(
    NCAgentApp activeApp,
    int ownCharacterId,
    string ownAddress,
    List<NCAgentDirectoryEntry> directory,
    List<NCAgentContactSummary> contacts,
    int? selectedCharacterId,
    List<NCAgentMessageSummary> messages,
    List<NCAgentCitiNetPostSummary> posts,
    NCAgentCallSummary call,
    string notice) : BoundUserInterfaceMessage
{
    public readonly NCAgentApp ActiveApp = activeApp;
    public readonly int OwnCharacterId = ownCharacterId;
    public readonly string OwnAddress = ownAddress;
    public readonly List<NCAgentDirectoryEntry> Directory = directory;
    public readonly List<NCAgentContactSummary> Contacts = contacts;
    public readonly int? SelectedCharacterId = selectedCharacterId;
    public readonly List<NCAgentMessageSummary> Messages = messages;
    public readonly List<NCAgentCitiNetPostSummary> Posts = posts;
    public readonly NCAgentCallSummary Call = call;
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
