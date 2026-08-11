// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Content.Shared._NC.Agent;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.Identity;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCAgentProfileData(
    NCCharacterId CharacterId,
    string Alias,
    string Address,
    NCAgentPresenceStatus Presence,
    bool OverlayNotificationsEnabled,
    bool NotificationSoundEnabled);

public sealed record NCAgentDirectoryData(
    NCCharacterId CharacterId,
    string Alias,
    string Address,
    bool IsContact,
    NCAgentPresenceStatus Presence);

public sealed record NCAgentContactData(
    NCCharacterId CharacterId,
    string Alias,
    string Address,
    NCAgentPresenceStatus Presence);

public sealed record NCAgentBlockedData(NCCharacterId CharacterId, string Alias, string Address);

public sealed record NCAgentConversationData(
    NCCharacterId CharacterId,
    string Alias,
    string Address,
    DateTime LastMessageAt,
    int UnreadCount);

public sealed record NCAgentMessageData(
    long MessageId,
    NCCharacterId SenderCharacterId,
    string SenderAlias,
    NCCharacterId RecipientCharacterId,
    string RecipientAlias,
    string Body,
    DateTime SentAt);

public sealed record NCCitiNetCommentData(
    long CommentId,
    NCCharacterId AuthorCharacterId,
    string AuthorAlias,
    string Body,
    DateTime CreatedAt);

public sealed record NCCitiNetCommentCreationData(
    NCCitiNetCommentData Comment,
    NCCharacterId PostAuthorCharacterId);

public sealed record NCCitiNetPostData(
    long PostId,
    NCCharacterId AuthorCharacterId,
    string AuthorAlias,
    string Body,
    DateTime CreatedAt,
    int ReactionCount,
    bool ReactedByViewer,
    IReadOnlyList<NCCitiNetCommentData> Comments);

public sealed record NCAgentCallHistoryData(
    long CallId,
    bool IsOutgoing,
    NCCharacterId RemoteCharacterId,
    string RemoteAlias,
    string RemoteAddress,
    DateTime StartedAt,
    DateTime? AnsweredAt,
    DateTime EndedAt,
    NCAgentCallOutcome Outcome,
    bool IsUnread);

public sealed record NCAgentGroupData(
    long GroupId,
    string Name,
    string OwnerAlias,
    NCAgentGroupRole Role,
    DateTime LastMessageAt,
    int UnreadCount);

public sealed record NCAgentGroupMemberData(
    NCCharacterId CharacterId,
    string Alias,
    string Address,
    NCAgentGroupRole Role,
    bool IsContact);

public sealed record NCAgentGroupMessageData(
    long MessageId,
    long GroupId,
    NCCharacterId SenderCharacterId,
    string SenderAlias,
    string Body,
    DateTime SentAt);

public sealed record NCAgentGroupInviteData(
    long GroupId,
    string GroupName,
    NCCharacterId InviterCharacterId,
    string InviterAlias,
    DateTime ExpiresAt);

public sealed record NCAgentSnapshotData(
    NCAgentProfileData Profile,
    IReadOnlyList<NCAgentDirectoryData> Directory,
    IReadOnlyList<NCAgentContactData> Contacts,
    IReadOnlyList<NCAgentBlockedData> Blocked,
    IReadOnlyList<NCAgentConversationData> Conversations,
    IReadOnlyList<NCAgentMessageData> Messages,
    IReadOnlyList<NCCitiNetPostData> Posts,
    IReadOnlyList<NCAgentCallHistoryData> CallHistory,
    IReadOnlyList<NCAgentGroupData> Groups,
    IReadOnlyList<NCAgentGroupMemberData> GroupMembers,
    IReadOnlyList<NCAgentGroupMessageData> GroupMessages,
    IReadOnlyList<NCAgentGroupInviteData> GroupInvites,
    bool FeedHasOlderPosts,
    int UnreadMessageCount,
    int UnreadGroupMessageCount,
    int MissedCallCount);

public enum NCAgentAliasUpdateResult : byte
{
    Updated,
    Duplicate,
    MissingProfile,
}

public partial interface IServerDbManager
{
    Task<NCAgentProfileData?> GetOrCreateNCAgentProfileAsync(NCCharacterId characterId, string addressPrefix);
    Task<NCAgentAliasUpdateResult> UpdateNCAgentAliasAsync(
        NCCharacterId characterId,
        string alias,
        string normalizedAlias);
    Task<NCAgentSnapshotData?> GetNCAgentSnapshotAsync(
        NCCharacterId characterId,
        NCCharacterId? selectedCharacterId,
        long? selectedGroupId,
        string directoryQuery,
        DateTime now,
        int directoryLimit,
        int conversationPeerLimit,
        int messageLimit,
        int callHistoryLimit,
        int groupConversationLimit,
        int groupMessageLimit,
        int postOffset,
        int postLimit,
        int commentLimit);
    Task<int> MarkNCAgentConversationReadAsync(
        NCCharacterId owner,
        NCCharacterId peer,
        DateTime readAt);
    Task<bool> AddNCAgentContactAsync(NCCharacterId owner, NCCharacterId contact);
    Task<bool> RemoveNCAgentContactAsync(NCCharacterId owner, NCCharacterId contact);
    Task<NCAgentMessageData?> CreateNCAgentMessageAsync(
        NCCharacterId sender,
        NCCharacterId recipient,
        string body,
        DateTime sentAt,
        DateTime expiresAt);
    Task<NCCitiNetPostData?> CreateNCCitiNetPostAsync(NCCharacterId author, string body, DateTime createdAt);
    Task<NCCitiNetCommentCreationData?> CreateNCCitiNetCommentAsync(
        NCCharacterId author,
        long postId,
        string body,
        DateTime createdAt);
    Task<bool?> ToggleNCCitiNetReactionAsync(NCCharacterId characterId, long postId, DateTime createdAt);
    Task<bool> DeleteNCCitiNetPostAsync(NCCharacterId author, long postId);
    Task<int> PurgeExpiredNCCitiNetPostsAsync(DateTime cutoff);
    Task<int> PurgeExpiredNCAgentMessagesAsync(DateTime now);
    Task<long?> CreateNCAgentCallLogAsync(
        NCCharacterId caller,
        NCCharacterId target,
        DateTime startedAt,
        DateTime? answeredAt,
        DateTime endedAt,
        NCAgentCallOutcome outcome);
    Task<int> MarkNCAgentCallHistoryReadAsync(NCCharacterId target, DateTime readAt);
    Task<int> PurgeExpiredNCAgentCallLogsAsync(DateTime cutoff);
}

public sealed partial class ServerDbManager
{
    public Task<NCAgentProfileData?> GetOrCreateNCAgentProfileAsync(NCCharacterId characterId, string addressPrefix)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.GetOrCreateNCAgentProfileAsync(characterId, addressPrefix));
    }

    public Task<NCAgentAliasUpdateResult> UpdateNCAgentAliasAsync(
        NCCharacterId characterId,
        string alias,
        string normalizedAlias)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.UpdateNCAgentAliasAsync(characterId, alias, normalizedAlias));
    }

    public Task<NCAgentSnapshotData?> GetNCAgentSnapshotAsync(
        NCCharacterId characterId,
        NCCharacterId? selectedCharacterId,
        long? selectedGroupId,
        string directoryQuery,
        DateTime now,
        int directoryLimit,
        int conversationPeerLimit,
        int messageLimit,
        int callHistoryLimit,
        int groupConversationLimit,
        int groupMessageLimit,
        int postOffset,
        int postLimit,
        int commentLimit)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCAgentSnapshotAsync(
            characterId,
            selectedCharacterId,
            selectedGroupId,
            directoryQuery,
            now,
            directoryLimit,
            conversationPeerLimit,
            messageLimit,
            callHistoryLimit,
            groupConversationLimit,
            groupMessageLimit,
            postOffset,
            postLimit,
            commentLimit));
    }

    public Task<int> MarkNCAgentConversationReadAsync(
        NCCharacterId owner,
        NCCharacterId peer,
        DateTime readAt)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.MarkNCAgentConversationReadAsync(owner, peer, readAt));
    }

    public Task<bool> AddNCAgentContactAsync(NCCharacterId owner, NCCharacterId contact)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.AddNCAgentContactAsync(owner, contact));
    }

    public Task<bool> RemoveNCAgentContactAsync(NCCharacterId owner, NCCharacterId contact)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.RemoveNCAgentContactAsync(owner, contact));
    }

    public Task<NCAgentMessageData?> CreateNCAgentMessageAsync(
        NCCharacterId sender,
        NCCharacterId recipient,
        string body,
        DateTime sentAt,
        DateTime expiresAt)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.CreateNCAgentMessageAsync(sender, recipient, body, sentAt, expiresAt));
    }

    public Task<NCCitiNetPostData?> CreateNCCitiNetPostAsync(NCCharacterId author, string body, DateTime createdAt)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.CreateNCCitiNetPostAsync(author, body, createdAt));
    }

    public Task<NCCitiNetCommentCreationData?> CreateNCCitiNetCommentAsync(
        NCCharacterId author,
        long postId,
        string body,
        DateTime createdAt)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.CreateNCCitiNetCommentAsync(author, postId, body, createdAt));
    }

    public Task<bool?> ToggleNCCitiNetReactionAsync(NCCharacterId characterId, long postId, DateTime createdAt)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.ToggleNCCitiNetReactionAsync(characterId, postId, createdAt));
    }

    public Task<bool> DeleteNCCitiNetPostAsync(NCCharacterId author, long postId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.DeleteNCCitiNetPostAsync(author, postId));
    }

    public Task<int> PurgeExpiredNCCitiNetPostsAsync(DateTime cutoff)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.PurgeExpiredNCCitiNetPostsAsync(cutoff));
    }

    public Task<int> PurgeExpiredNCAgentMessagesAsync(DateTime now)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.PurgeExpiredNCAgentMessagesAsync(now));
    }

    public Task<long?> CreateNCAgentCallLogAsync(
        NCCharacterId caller,
        NCCharacterId target,
        DateTime startedAt,
        DateTime? answeredAt,
        DateTime endedAt,
        NCAgentCallOutcome outcome)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.CreateNCAgentCallLogAsync(
            caller,
            target,
            startedAt,
            answeredAt,
            endedAt,
            outcome));
    }

    public Task<int> MarkNCAgentCallHistoryReadAsync(NCCharacterId target, DateTime readAt)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.MarkNCAgentCallHistoryReadAsync(target, readAt));
    }

    public Task<int> PurgeExpiredNCAgentCallLogsAsync(DateTime cutoff)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.PurgeExpiredNCAgentCallLogsAsync(cutoff));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<NCAgentAliasUpdateResult> UpdateNCAgentAliasAsync(
        NCCharacterId characterId,
        string alias,
        string normalizedAlias)
    {
        if (!characterId.IsValid || alias.Length is < 1 or > 32 || normalizedAlias.Length is < 1 or > 32)
            return NCAgentAliasUpdateResult.MissingProfile;

        await using var db = await GetDb();
        var profile = await db.DbContext.NCAgentProfiles
            .Include(value => value.Profile)
            .SingleOrDefaultAsync(value => value.ProfileId == characterId.Value);
        if (profile == null)
            return NCAgentAliasUpdateResult.MissingProfile;

        // Null aliases still display the character name, so reserve those visible names as well.
        var duplicate = await db.DbContext.NCAgentProfiles.AnyAsync(value =>
            value.ProfileId != characterId.Value &&
            (value.NormalizedAlias ?? value.Profile.CharacterName.ToUpper()) == normalizedAlias);
        if (duplicate)
            return NCAgentAliasUpdateResult.Duplicate;

        profile.Alias = alias;
        profile.NormalizedAlias = normalizedAlias;
        profile.UpdatedAt = DateTime.UtcNow;

        try
        {
            await db.DbContext.SaveChangesAsync();
            return NCAgentAliasUpdateResult.Updated;
        }
        catch (DbUpdateException)
        {
            // The unique index closes the race between simultaneous rename requests.
            return NCAgentAliasUpdateResult.Duplicate;
        }
    }

    public async Task<NCAgentProfileData?> GetOrCreateNCAgentProfileAsync(
        NCCharacterId characterId,
        string addressPrefix)
    {
        if (!characterId.IsValid)
            return null;

        var prefix = addressPrefix.Trim().ToUpperInvariant();
        if (prefix.Length is < 1 or > 8)
            return null;

        await using var db = await GetDb();
        var existing = await db.DbContext.NCAgentProfiles
            .AsNoTracking()
            .Include(value => value.Profile)
            .SingleOrDefaultAsync(value => value.ProfileId == characterId.Value);
        if (existing != null)
            return ToProfileData(existing);

        var character = await db.DbContext.Profile.SingleOrDefaultAsync(value => value.Id == characterId.Value);
        if (character == null)
            return null;

        string address;
        do
        {
            address = $"{prefix}-{RandomNumberGenerator.GetHexString(8)}";
        }
        while (await db.DbContext.NCAgentProfiles.AnyAsync(value => value.Address == address));

        var now = DateTime.UtcNow;
        var profile = new NCAgentProfile
        {
            ProfileId = characterId.Value,
            Address = address,
            CreatedAt = now,
            UpdatedAt = now,
            Profile = character,
        };
        db.DbContext.NCAgentProfiles.Add(profile);
        await db.DbContext.SaveChangesAsync();
        return ToProfileData(profile);
    }

    public async Task<NCAgentSnapshotData?> GetNCAgentSnapshotAsync(
        NCCharacterId characterId,
        NCCharacterId? selectedCharacterId,
        long? selectedGroupId,
        string directoryQuery,
        DateTime now,
        int directoryLimit,
        int conversationPeerLimit,
        int messageLimit,
        int callHistoryLimit,
        int groupConversationLimit,
        int groupMessageLimit,
        int postOffset,
        int postLimit,
        int commentLimit)
    {
        if (!characterId.IsValid)
            return null;

        await using var db = await GetDb();
        var profile = await db.DbContext.NCAgentProfiles
            .AsNoTracking()
            .Include(value => value.Profile)
            .SingleOrDefaultAsync(value => value.ProfileId == characterId.Value);
        if (profile == null)
            return null;

        var blocked = await db.DbContext.NCAgentBlocks
            .AsNoTracking()
            .Where(value => value.OwnerProfileId == characterId.Value)
            .OrderBy(value => value.Blocked.Alias ?? value.Blocked.Profile.CharacterName)
            .Select(value => new NCAgentBlockedData(
                new NCCharacterId(value.BlockedProfileId),
                value.Blocked.Alias ?? value.Blocked.Profile.CharacterName,
                value.Blocked.Address))
            .ToListAsync();

        var contacts = await db.DbContext.NCAgentContacts
            .AsNoTracking()
            .Where(value => value.OwnerProfileId == characterId.Value &&
                !db.DbContext.NCAgentBlocks.Any(block =>
                    block.OwnerProfileId == characterId.Value && block.BlockedProfileId == value.ContactProfileId ||
                    block.OwnerProfileId == value.ContactProfileId && block.BlockedProfileId == characterId.Value))
            .OrderBy(value => value.Contact.Alias ?? value.Contact.Profile.CharacterName)
            .Select(value => new NCAgentContactData(
                new NCCharacterId(value.ContactProfileId),
                value.Contact.Alias ?? value.Contact.Profile.CharacterName,
                value.Contact.Address,
                (NCAgentPresenceStatus) value.Contact.Presence))
            .ToListAsync();

        var contactIds = contacts.Select(value => value.CharacterId.Value).ToList();

        // Conversation peers are derived from live message history, so an unsolicited incoming message
        // remains reachable without silently turning its sender into a saved contact.
        var conversationActivity = await db.DbContext.NCAgentMessages
            .AsNoTracking()
            .Where(value => value.ExpiresAt > now &&
                (value.SenderProfileId == characterId.Value || value.RecipientProfileId == characterId.Value) &&
                !db.DbContext.NCAgentDirectConversationStates.Any(state =>
                    state.OwnerProfileId == characterId.Value &&
                    state.PeerProfileId == (value.SenderProfileId == characterId.Value
                        ? value.RecipientProfileId
                        : value.SenderProfileId) &&
                    state.ClearedAt != null &&
                    value.SentAt <= state.ClearedAt))
            .GroupBy(value => value.SenderProfileId == characterId.Value
                ? value.RecipientProfileId
                : value.SenderProfileId)
            .Select(group => new
            {
                ProfileId = group.Key,
                LastMessageAt = group.Max(value => value.SentAt),
                UnreadCount = group.Count(value =>
                    value.RecipientProfileId == characterId.Value && value.ReadAt == null),
            })
            .OrderByDescending(value => value.LastMessageAt)
            .Take(Math.Clamp(conversationPeerLimit, 1, 200))
            .ToListAsync();
        var conversationIds = conversationActivity.Select(value => value.ProfileId).ToList();
        var conversationProfiles = await db.DbContext.NCAgentProfiles
            .AsNoTracking()
            .Where(value => conversationIds.Contains(value.ProfileId))
            .Select(value => new NCAgentContactData(
                new NCCharacterId(value.ProfileId),
                value.Alias ?? value.Profile.CharacterName,
                value.Address,
                (NCAgentPresenceStatus) value.Presence))
            .ToDictionaryAsync(value => value.CharacterId.Value);
        var conversations = conversationActivity
            .Where(value => conversationProfiles.ContainsKey(value.ProfileId))
            .Select(value => new NCAgentConversationData(
                conversationProfiles[value.ProfileId].CharacterId,
                conversationProfiles[value.ProfileId].Alias,
                conversationProfiles[value.ProfileId].Address,
                value.LastMessageAt,
                value.UnreadCount))
            .ToList();

        // Notification totals are not limited by the number of conversation buttons returned to the client.
        var unreadMessageCount = await db.DbContext.NCAgentMessages
            .AsNoTracking()
            .CountAsync(value => value.RecipientProfileId == characterId.Value &&
                                 value.ExpiresAt > now &&
                                 value.ReadAt == null &&
                                 !db.DbContext.NCAgentDirectConversationStates.Any(state =>
                                     state.OwnerProfileId == characterId.Value &&
                                     state.PeerProfileId == value.SenderProfileId &&
                                     state.ClearedAt != null &&
                                     value.SentAt <= state.ClearedAt));

        var normalizedQuery = directoryQuery.Trim().ToLowerInvariant();
        IReadOnlyList<NCAgentDirectoryData> directory = Array.Empty<NCAgentDirectoryData>();
        if (normalizedQuery.Length > 0)
        {
            var normalizedDirectoryQuery = normalizedQuery.ToUpperInvariant();
            directory = await db.DbContext.NCAgentProfiles
                .AsNoTracking()
                .Where(value => value.ProfileId != characterId.Value &&
                    !db.DbContext.NCAgentBlocks.Any(block =>
                        block.OwnerProfileId == characterId.Value && block.BlockedProfileId == value.ProfileId ||
                        block.OwnerProfileId == value.ProfileId && block.BlockedProfileId == characterId.Value) &&
                    (value.Address.ToLower().Contains(normalizedQuery) ||
                     (value.NormalizedAlias ?? value.Profile.CharacterName.ToUpper())
                     .Contains(normalizedDirectoryQuery)))
                .OrderBy(value => value.Alias ?? value.Profile.CharacterName)
                .Take(Math.Clamp(directoryLimit, 1, 100))
                .Select(value => new NCAgentDirectoryData(
                    new NCCharacterId(value.ProfileId),
                    value.Alias ?? value.Profile.CharacterName,
                    value.Address,
                    contactIds.Contains(value.ProfileId),
                    (NCAgentPresenceStatus) value.Presence))
                .ToListAsync();
        }

        IReadOnlyList<NCAgentMessageData> messages = Array.Empty<NCAgentMessageData>();
        if (selectedCharacterId is { IsValid: true } selected &&
            await db.DbContext.NCAgentProfiles.AnyAsync(value => value.ProfileId == selected.Value))
        {
            var clearedAt = await db.DbContext.NCAgentDirectConversationStates
                .AsNoTracking()
                .Where(value => value.OwnerProfileId == characterId.Value && value.PeerProfileId == selected.Value)
                .Select(value => value.ClearedAt)
                .SingleOrDefaultAsync();
            var newestFirst = await db.DbContext.NCAgentMessages
                .AsNoTracking()
                .Where(value => value.ExpiresAt > now &&
                    (clearedAt == null || value.SentAt > clearedAt) &&
                    (value.SenderProfileId == characterId.Value && value.RecipientProfileId == selected.Value ||
                     value.SenderProfileId == selected.Value && value.RecipientProfileId == characterId.Value))
                .OrderByDescending(value => value.SentAt)
                .Take(Math.Clamp(messageLimit, 1, 500))
                .Select(value => new NCAgentMessageData(
                    value.Id,
                    new NCCharacterId(value.SenderProfileId),
                    value.Sender.Alias ?? value.Sender.Profile.CharacterName,
                    new NCCharacterId(value.RecipientProfileId),
                    value.Recipient.Alias ?? value.Recipient.Profile.CharacterName,
                    value.Body,
                    value.SentAt))
                .ToListAsync();
            newestFirst.Reverse();
            messages = newestFirst;
        }

        var callHistory = await db.DbContext.NCAgentCallLogs
            .AsNoTracking()
            .Where(value => value.CallerProfileId == characterId.Value ||
                            value.TargetProfileId == characterId.Value)
            .OrderByDescending(value => value.EndedAt)
            .Take(Math.Clamp(callHistoryLimit, 1, 200))
            .Select(value => new NCAgentCallHistoryData(
                value.Id,
                value.CallerProfileId == characterId.Value,
                new NCCharacterId(value.CallerProfileId == characterId.Value
                    ? value.TargetProfileId
                    : value.CallerProfileId),
                value.CallerProfileId == characterId.Value
                    ? value.Target.Alias ?? value.Target.Profile.CharacterName
                    : value.Caller.Alias ?? value.Caller.Profile.CharacterName,
                value.CallerProfileId == characterId.Value
                    ? value.Target.Address
                    : value.Caller.Address,
                value.StartedAt,
                value.AnsweredAt,
                value.EndedAt,
                (NCAgentCallOutcome) value.Outcome,
                value.TargetProfileId == characterId.Value && value.TargetReadAt == null))
            .ToListAsync();
        var missedCallCount = await db.DbContext.NCAgentCallLogs
            .AsNoTracking()
            .CountAsync(value => value.TargetProfileId == characterId.Value && value.TargetReadAt == null);

        var groups = await db.DbContext.NCAgentGroupMembers
            .AsNoTracking()
            .Where(value => value.ProfileId == characterId.Value)
            .OrderByDescending(value => value.Group.UpdatedAt)
            .Take(Math.Clamp(groupConversationLimit, 1, 100))
            .Select(value => new NCAgentGroupData(
                value.GroupId,
                value.Group.Name,
                value.Group.Owner.Alias ?? value.Group.Owner.Profile.CharacterName,
                (NCAgentGroupRole) value.Role,
                value.Group.Messages
                    .Where(message => message.ExpiresAt > now &&
                                      (value.ClearedAt == null || message.SentAt > value.ClearedAt))
                    .Select(message => (DateTime?) message.SentAt)
                    .Max() ?? value.Group.UpdatedAt,
                value.Group.Messages.Count(message =>
                    message.ExpiresAt > now &&
                    message.SenderProfileId != characterId.Value &&
                    (value.ClearedAt == null || message.SentAt > value.ClearedAt) &&
                    message.SentAt > (value.LastReadAt ?? value.JoinedAt))))
            .ToListAsync();

        var unreadGroupMessageCount = await (
            from message in db.DbContext.NCAgentGroupMessages.AsNoTracking()
            join member in db.DbContext.NCAgentGroupMembers.AsNoTracking()
                on message.GroupId equals member.GroupId
            where member.ProfileId == characterId.Value &&
                  message.ExpiresAt > now &&
                  message.SenderProfileId != characterId.Value &&
                  (member.ClearedAt == null || message.SentAt > member.ClearedAt) &&
                  message.SentAt > (member.LastReadAt ?? member.JoinedAt)
            select message.Id)
            .CountAsync();

        IReadOnlyList<NCAgentGroupMemberData> groupMembers = Array.Empty<NCAgentGroupMemberData>();
        IReadOnlyList<NCAgentGroupMessageData> groupMessages = Array.Empty<NCAgentGroupMessageData>();
        if (selectedGroupId is > 0 &&
            await db.DbContext.NCAgentGroupMembers.AnyAsync(value =>
                value.GroupId == selectedGroupId.Value && value.ProfileId == characterId.Value))
        {
            var membership = await db.DbContext.NCAgentGroupMembers
                .AsNoTracking()
                .SingleAsync(value => value.GroupId == selectedGroupId.Value &&
                                      value.ProfileId == characterId.Value);
            groupMembers = await db.DbContext.NCAgentGroupMembers
                .AsNoTracking()
                .Where(value => value.GroupId == selectedGroupId.Value)
                .OrderByDescending(value => value.Role)
                .ThenBy(value => value.Profile.Alias ?? value.Profile.Profile.CharacterName)
                .Select(value => new NCAgentGroupMemberData(
                    new NCCharacterId(value.ProfileId),
                    value.Profile.Alias ?? value.Profile.Profile.CharacterName,
                    value.Profile.Address,
                    (NCAgentGroupRole) value.Role,
                    contactIds.Contains(value.ProfileId)))
                .ToListAsync();

            var newestGroupMessages = await db.DbContext.NCAgentGroupMessages
                .AsNoTracking()
                .Where(value => value.GroupId == selectedGroupId.Value &&
                                value.ExpiresAt > now &&
                                (membership.ClearedAt == null || value.SentAt > membership.ClearedAt))
                .OrderByDescending(value => value.SentAt)
                .Take(Math.Clamp(groupMessageLimit, 1, 500))
                .Select(value => new NCAgentGroupMessageData(
                    value.Id,
                    value.GroupId,
                    new NCCharacterId(value.SenderProfileId),
                    value.Sender.Alias ?? value.Sender.Profile.CharacterName,
                    value.Body,
                    value.SentAt))
                .ToListAsync();
            newestGroupMessages.Reverse();
            groupMessages = newestGroupMessages;
        }

        var groupInvites = await db.DbContext.NCAgentGroupInvites
            .AsNoTracking()
            .Where(value => value.InviteeProfileId == characterId.Value && value.ExpiresAt > now)
            .OrderByDescending(value => value.CreatedAt)
            .Select(value => new NCAgentGroupInviteData(
                value.GroupId,
                value.Group.Name,
                new NCCharacterId(value.InviterProfileId),
                value.Inviter.Alias ?? value.Inviter.Profile.CharacterName,
                value.ExpiresAt))
            .ToListAsync();

        // Feed rows are queried only for the active CitiNet app; other Agent screens avoid this heavier join.
        var posts = new List<NCCitiNetPostData>();
        var feedHasOlderPosts = false;
        if (postLimit > 0)
        {
            var pageSize = Math.Clamp(postLimit, 1, 200);
            var pageOffset = Math.Clamp(postOffset, 0, 10_000);
            posts = await db.DbContext.NCCitiNetPosts
                .AsNoTracking()
                .Where(value => !db.DbContext.NCAgentBlocks.Any(block =>
                    block.OwnerProfileId == characterId.Value && block.BlockedProfileId == value.AuthorProfileId ||
                    block.OwnerProfileId == value.AuthorProfileId && block.BlockedProfileId == characterId.Value))
                .OrderByDescending(value => value.CreatedAt)
                .ThenByDescending(value => value.Id)
                .Skip(pageOffset)
                .Take(pageSize + 1)
                .Select(value => new NCCitiNetPostData(
                    value.Id,
                    new NCCharacterId(value.AuthorProfileId),
                    value.Author.Alias ?? value.Author.Profile.CharacterName,
                    value.Body,
                    value.CreatedAt,
                    value.Reactions.Count,
                    value.Reactions.Any(reaction => reaction.ProfileId == characterId.Value),
                    Array.Empty<NCCitiNetCommentData>()))
                .ToListAsync();

            feedHasOlderPosts = posts.Count > pageSize;
            if (feedHasOlderPosts)
                posts.RemoveAt(posts.Count - 1);
        }

        var postIds = posts.Select(value => value.PostId).ToList();
        var comments = await db.DbContext.NCCitiNetComments
            .AsNoTracking()
            .Where(value => postIds.Contains(value.PostId) &&
                !db.DbContext.NCAgentBlocks.Any(block =>
                    block.OwnerProfileId == characterId.Value && block.BlockedProfileId == value.AuthorProfileId ||
                    block.OwnerProfileId == value.AuthorProfileId && block.BlockedProfileId == characterId.Value))
            .OrderByDescending(value => value.CreatedAt)
            .Take(Math.Clamp(commentLimit, 1, 1000))
            .Select(value => new
            {
                value.PostId,
                Data = new NCCitiNetCommentData(
                    value.Id,
                    new NCCharacterId(value.AuthorProfileId),
                    value.Author.Alias ?? value.Author.Profile.CharacterName,
                    value.Body,
                    value.CreatedAt),
            })
            .ToListAsync();

        var postsWithComments = posts
            .Select(post => post with
            {
                Comments = comments
                    .Where(comment => comment.PostId == post.PostId)
                    .OrderBy(comment => comment.Data.CreatedAt)
                    .Select(comment => comment.Data)
                    .ToList(),
            })
            .ToList();

        return new NCAgentSnapshotData(
            ToProfileData(profile),
            directory,
            contacts,
            blocked,
            conversations,
            messages,
            postsWithComments,
            callHistory,
            groups,
            groupMembers,
            groupMessages,
            groupInvites,
            feedHasOlderPosts,
            unreadMessageCount,
            unreadGroupMessageCount,
            missedCallCount);
    }

    public async Task<int> MarkNCAgentConversationReadAsync(
        NCCharacterId owner,
        NCCharacterId peer,
        DateTime readAt)
    {
        if (!owner.IsValid || !peer.IsValid || owner == peer)
            return 0;

        await using var db = await GetDb();
        return await db.DbContext.NCAgentMessages
            .Where(value => value.RecipientProfileId == owner.Value &&
                            value.SenderProfileId == peer.Value &&
                            value.ExpiresAt > readAt &&
                            value.ReadAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.ReadAt, readAt));
    }

    public async Task<bool> AddNCAgentContactAsync(NCCharacterId owner, NCCharacterId contact)
    {
        if (!owner.IsValid || !contact.IsValid || owner == contact)
            return false;

        await using var db = await GetDb();
        if (!await db.DbContext.NCAgentProfiles.AnyAsync(value => value.ProfileId == owner.Value) ||
            !await db.DbContext.NCAgentProfiles.AnyAsync(value => value.ProfileId == contact.Value) ||
            await db.DbContext.NCAgentBlocks.AnyAsync(value =>
                value.OwnerProfileId == owner.Value && value.BlockedProfileId == contact.Value ||
                value.OwnerProfileId == contact.Value && value.BlockedProfileId == owner.Value))
            return false;

        if (await db.DbContext.NCAgentContacts.AnyAsync(value =>
                value.OwnerProfileId == owner.Value && value.ContactProfileId == contact.Value))
            return true;

        db.DbContext.NCAgentContacts.Add(new NCAgentContact
        {
            OwnerProfileId = owner.Value,
            ContactProfileId = contact.Value,
            CreatedAt = DateTime.UtcNow,
        });
        await db.DbContext.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemoveNCAgentContactAsync(NCCharacterId owner, NCCharacterId contact)
    {
        if (!owner.IsValid || !contact.IsValid)
            return false;

        await using var db = await GetDb();
        var value = await db.DbContext.NCAgentContacts.SingleOrDefaultAsync(entry =>
            entry.OwnerProfileId == owner.Value && entry.ContactProfileId == contact.Value);
        if (value == null)
            return true;

        db.DbContext.NCAgentContacts.Remove(value);
        await db.DbContext.SaveChangesAsync();
        return true;
    }

    public async Task<NCAgentMessageData?> CreateNCAgentMessageAsync(
        NCCharacterId sender,
        NCCharacterId recipient,
        string body,
        DateTime sentAt,
        DateTime expiresAt)
    {
        var normalized = body.Trim();
        if (!sender.IsValid || !recipient.IsValid || sender == recipient || normalized.Length is < 1 or > 1024 ||
            expiresAt <= sentAt)
            return null;

        await using var db = await GetDb();
        var senderProfile = await db.DbContext.NCAgentProfiles
            .Include(value => value.Profile)
            .SingleOrDefaultAsync(value => value.ProfileId == sender.Value);
        var recipientProfile = await db.DbContext.NCAgentProfiles
            .Include(value => value.Profile)
            .SingleOrDefaultAsync(value => value.ProfileId == recipient.Value);
        if (senderProfile == null || recipientProfile == null)
            return null;
        if (await db.DbContext.NCAgentBlocks.AnyAsync(value =>
                value.OwnerProfileId == sender.Value && value.BlockedProfileId == recipient.Value ||
                value.OwnerProfileId == recipient.Value && value.BlockedProfileId == sender.Value))
        {
            return null;
        }

        var message = new NCAgentMessage
        {
            SenderProfileId = sender.Value,
            RecipientProfileId = recipient.Value,
            Body = normalized,
            SentAt = sentAt,
            ExpiresAt = expiresAt,
        };
        db.DbContext.NCAgentMessages.Add(message);
        await db.DbContext.SaveChangesAsync();
        return new NCAgentMessageData(
            message.Id,
            sender,
            senderProfile.Alias ?? senderProfile.Profile.CharacterName,
            recipient,
            recipientProfile.Alias ?? recipientProfile.Profile.CharacterName,
            message.Body,
            message.SentAt);
    }

    public async Task<NCCitiNetPostData?> CreateNCCitiNetPostAsync(
        NCCharacterId author,
        string body,
        DateTime createdAt)
    {
        var normalized = body.Trim();
        if (!author.IsValid || normalized.Length is < 1 or > 2048)
            return null;

        await using var db = await GetDb();
        var profile = await db.DbContext.NCAgentProfiles
            .Include(value => value.Profile)
            .SingleOrDefaultAsync(value => value.ProfileId == author.Value);
        if (profile == null)
            return null;

        var post = new NCCitiNetPost
        {
            AuthorProfileId = author.Value,
            Body = normalized,
            CreatedAt = createdAt,
        };
        db.DbContext.NCCitiNetPosts.Add(post);
        await db.DbContext.SaveChangesAsync();
        return new NCCitiNetPostData(
            post.Id,
            author,
            profile.Alias ?? profile.Profile.CharacterName,
            post.Body,
            post.CreatedAt,
            0,
            false,
            Array.Empty<NCCitiNetCommentData>());
    }

    public async Task<NCCitiNetCommentCreationData?> CreateNCCitiNetCommentAsync(
        NCCharacterId author,
        long postId,
        string body,
        DateTime createdAt)
    {
        var normalized = body.Trim();
        if (!author.IsValid || postId <= 0 || normalized.Length is < 1 or > 1024)
            return null;

        await using var db = await GetDb();
        var profile = await db.DbContext.NCAgentProfiles
            .Include(value => value.Profile)
            .SingleOrDefaultAsync(value => value.ProfileId == author.Value);
        var postAuthorId = await db.DbContext.NCCitiNetPosts
            .AsNoTracking()
            .Where(value => value.Id == postId &&
                !db.DbContext.NCAgentBlocks.Any(block =>
                    block.OwnerProfileId == author.Value && block.BlockedProfileId == value.AuthorProfileId ||
                    block.OwnerProfileId == value.AuthorProfileId && block.BlockedProfileId == author.Value))
            .Select(value => (int?) value.AuthorProfileId)
            .SingleOrDefaultAsync();
        if (profile == null || postAuthorId == null)
            return null;

        var comment = new NCCitiNetComment
        {
            PostId = postId,
            AuthorProfileId = author.Value,
            Body = normalized,
            CreatedAt = createdAt,
        };
        db.DbContext.NCCitiNetComments.Add(comment);
        await db.DbContext.SaveChangesAsync();
        return new NCCitiNetCommentCreationData(
            new NCCitiNetCommentData(
                comment.Id,
                author,
                profile.Alias ?? profile.Profile.CharacterName,
                comment.Body,
                comment.CreatedAt),
            new NCCharacterId(postAuthorId.Value));
    }

    public async Task<bool?> ToggleNCCitiNetReactionAsync(
        NCCharacterId characterId,
        long postId,
        DateTime createdAt)
    {
        if (!characterId.IsValid || postId <= 0)
            return null;

        await using var db = await GetDb();
        if (!await db.DbContext.NCAgentProfiles.AnyAsync(value => value.ProfileId == characterId.Value) ||
            !await db.DbContext.NCCitiNetPosts.AnyAsync(value => value.Id == postId &&
                !db.DbContext.NCAgentBlocks.Any(block =>
                    block.OwnerProfileId == characterId.Value && block.BlockedProfileId == value.AuthorProfileId ||
                    block.OwnerProfileId == value.AuthorProfileId && block.BlockedProfileId == characterId.Value)))
            return null;

        var existing = await db.DbContext.NCCitiNetReactions.SingleOrDefaultAsync(value =>
            value.PostId == postId && value.ProfileId == characterId.Value);
        if (existing != null)
        {
            db.DbContext.NCCitiNetReactions.Remove(existing);
            await db.DbContext.SaveChangesAsync();
            return false;
        }

        db.DbContext.NCCitiNetReactions.Add(new NCCitiNetReaction
        {
            PostId = postId,
            ProfileId = characterId.Value,
            CreatedAt = createdAt,
        });
        await db.DbContext.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteNCCitiNetPostAsync(NCCharacterId author, long postId)
    {
        if (!author.IsValid || postId <= 0)
            return false;

        await using var db = await GetDb();
        return await db.DbContext.NCCitiNetPosts
            .Where(value => value.Id == postId && value.AuthorProfileId == author.Value)
            .ExecuteDeleteAsync() == 1;
    }

    public async Task<int> PurgeExpiredNCCitiNetPostsAsync(DateTime cutoff)
    {
        await using var db = await GetDb();
        return await db.DbContext.NCCitiNetPosts
            .Where(value => value.CreatedAt <= cutoff)
            .ExecuteDeleteAsync();
    }

    public async Task<int> PurgeExpiredNCAgentMessagesAsync(DateTime now)
    {
        await using var db = await GetDb();
        return await db.DbContext.NCAgentMessages
            .Where(value => value.ExpiresAt <= now)
            .ExecuteDeleteAsync();
    }

    public async Task<long?> CreateNCAgentCallLogAsync(
        NCCharacterId caller,
        NCCharacterId target,
        DateTime startedAt,
        DateTime? answeredAt,
        DateTime endedAt,
        NCAgentCallOutcome outcome)
    {
        if (!caller.IsValid || !target.IsValid || caller == target ||
            endedAt < startedAt || answeredAt < startedAt || answeredAt > endedAt ||
            !Enum.IsDefined(outcome))
        {
            return null;
        }

        await using var db = await GetDb();
        if (!await db.DbContext.NCAgentProfiles.AnyAsync(value => value.ProfileId == caller.Value) ||
            !await db.DbContext.NCAgentProfiles.AnyAsync(value => value.ProfileId == target.Value))
        {
            return null;
        }

        var call = new NCAgentCallLog
        {
            CallerProfileId = caller.Value,
            TargetProfileId = target.Value,
            StartedAt = startedAt,
            AnsweredAt = answeredAt,
            EndedAt = endedAt,
            Outcome = (byte) outcome,
            // Only missed calls create an unread notification for the target.
            TargetReadAt = outcome == NCAgentCallOutcome.Missed ? null : endedAt,
        };
        db.DbContext.NCAgentCallLogs.Add(call);
        await db.DbContext.SaveChangesAsync();
        return call.Id;
    }

    public async Task<int> MarkNCAgentCallHistoryReadAsync(NCCharacterId target, DateTime readAt)
    {
        if (!target.IsValid)
            return 0;

        await using var db = await GetDb();
        return await db.DbContext.NCAgentCallLogs
            .Where(value => value.TargetProfileId == target.Value && value.TargetReadAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.TargetReadAt, readAt));
    }

    public async Task<int> PurgeExpiredNCAgentCallLogsAsync(DateTime cutoff)
    {
        await using var db = await GetDb();
        return await db.DbContext.NCAgentCallLogs
            .Where(value => value.EndedAt <= cutoff)
            .ExecuteDeleteAsync();
    }

    private static NCAgentProfileData ToProfileData(NCAgentProfile profile)
    {
        return new NCAgentProfileData(
            new NCCharacterId(profile.ProfileId),
            profile.Alias ?? profile.Profile.CharacterName,
            profile.Address,
            (NCAgentPresenceStatus) profile.Presence,
            profile.OverlayNotificationsEnabled,
            profile.NotificationSoundEnabled);
    }
}
