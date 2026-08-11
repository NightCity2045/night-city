// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Shared._NC.Agent;
using Content.Shared._NC.Identity;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCAgentGroupCreationData(
    long GroupId,
    string Name,
    IReadOnlyList<NCCharacterId> InvitedCharacterIds);

public partial interface IServerDbManager
{
    Task<IReadOnlyList<NCAgentProfileData>> GetNCAgentProfilesAsync(IReadOnlyCollection<NCCharacterId> characters);
    Task<IReadOnlyList<NCCharacterId>> ResolveNCCitiNetMentionRecipientsAsync(
        NCCharacterId author,
        IReadOnlyCollection<string> normalizedAliases);
    Task<bool> UpdateNCAgentPresenceAsync(NCCharacterId characterId, NCAgentPresenceStatus presence);
    Task<bool> UpdateNCAgentNotificationSettingsAsync(NCCharacterId characterId, bool overlayEnabled, bool soundEnabled);
    Task<bool> AreNCAgentProfilesBlockedAsync(NCCharacterId first, NCCharacterId second);
    Task<bool> SetNCAgentBlockedAsync(NCCharacterId owner, NCCharacterId target, bool blocked, DateTime changedAt);
    Task<bool> AddNCAgentMutualContactsAsync(NCCharacterId first, NCCharacterId second, DateTime createdAt);
    Task<bool> ClearNCAgentConversationAsync(NCCharacterId owner, NCCharacterId peer, DateTime clearedAt);
    Task<int> MarkNCAgentGroupReadAsync(NCCharacterId owner, long groupId, DateTime readAt);
    Task<NCAgentGroupCreationData?> CreateNCAgentGroupAsync(
        NCCharacterId owner,
        string name,
        IReadOnlyCollection<NCCharacterId> invitees,
        DateTime createdAt,
        DateTime inviteExpiresAt,
        int maximumNameLength,
        int maximumMembers);
    Task<bool> InviteNCAgentGroupMemberAsync(
        NCCharacterId owner,
        long groupId,
        NCCharacterId invitee,
        DateTime createdAt,
        DateTime expiresAt,
        int maximumMembers);
    Task<bool> RespondNCAgentGroupInviteAsync(NCCharacterId invitee, long groupId, bool accept, DateTime respondedAt, int maximumMembers);
    Task<NCAgentGroupMessageData?> CreateNCAgentGroupMessageAsync(
        NCCharacterId sender,
        long groupId,
        string body,
        DateTime sentAt,
        DateTime expiresAt,
        int maximumMessageLength);
    Task<IReadOnlyList<NCCharacterId>> GetNCAgentGroupRecipientIdsAsync(long groupId, NCCharacterId exclude);
    Task<bool> RemoveNCAgentGroupMemberAsync(NCCharacterId owner, long groupId, NCCharacterId member);
    Task<bool> LeaveNCAgentGroupAsync(NCCharacterId member, long groupId, DateTime changedAt);
    Task<bool> ClearNCAgentGroupConversationAsync(NCCharacterId owner, long groupId, DateTime clearedAt);
    Task<int> PurgeExpiredNCAgentGroupMessagesAsync(DateTime now);
    Task<int> PurgeExpiredNCAgentGroupInvitesAsync(DateTime now);
}

public sealed partial class ServerDbManager
{
    public Task<IReadOnlyList<NCAgentProfileData>> GetNCAgentProfilesAsync(
        IReadOnlyCollection<NCCharacterId> characters)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCAgentProfilesAsync(characters));
    }

    public Task<IReadOnlyList<NCCharacterId>> ResolveNCCitiNetMentionRecipientsAsync(
        NCCharacterId author,
        IReadOnlyCollection<string> normalizedAliases)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.ResolveNCCitiNetMentionRecipientsAsync(author, normalizedAliases));
    }

    public Task<bool> UpdateNCAgentPresenceAsync(NCCharacterId characterId, NCAgentPresenceStatus presence)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.UpdateNCAgentPresenceAsync(characterId, presence));
    }

    public Task<bool> UpdateNCAgentNotificationSettingsAsync(
        NCCharacterId characterId,
        bool overlayEnabled,
        bool soundEnabled)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.UpdateNCAgentNotificationSettingsAsync(
            characterId,
            overlayEnabled,
            soundEnabled));
    }

    public Task<bool> AreNCAgentProfilesBlockedAsync(NCCharacterId first, NCCharacterId second)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.AreNCAgentProfilesBlockedAsync(first, second));
    }

    public Task<bool> SetNCAgentBlockedAsync(
        NCCharacterId owner,
        NCCharacterId target,
        bool blocked,
        DateTime changedAt)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.SetNCAgentBlockedAsync(owner, target, blocked, changedAt));
    }

    public Task<bool> AddNCAgentMutualContactsAsync(NCCharacterId first, NCCharacterId second, DateTime createdAt)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.AddNCAgentMutualContactsAsync(first, second, createdAt));
    }

    public Task<bool> ClearNCAgentConversationAsync(NCCharacterId owner, NCCharacterId peer, DateTime clearedAt)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.ClearNCAgentConversationAsync(owner, peer, clearedAt));
    }

    public Task<int> MarkNCAgentGroupReadAsync(NCCharacterId owner, long groupId, DateTime readAt)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.MarkNCAgentGroupReadAsync(owner, groupId, readAt));
    }

    public Task<NCAgentGroupCreationData?> CreateNCAgentGroupAsync(
        NCCharacterId owner,
        string name,
        IReadOnlyCollection<NCCharacterId> invitees,
        DateTime createdAt,
        DateTime inviteExpiresAt,
        int maximumNameLength,
        int maximumMembers)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.CreateNCAgentGroupAsync(
            owner,
            name,
            invitees,
            createdAt,
            inviteExpiresAt,
            maximumNameLength,
            maximumMembers));
    }

    public Task<bool> InviteNCAgentGroupMemberAsync(
        NCCharacterId owner,
        long groupId,
        NCCharacterId invitee,
        DateTime createdAt,
        DateTime expiresAt,
        int maximumMembers)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.InviteNCAgentGroupMemberAsync(
            owner,
            groupId,
            invitee,
            createdAt,
            expiresAt,
            maximumMembers));
    }

    public Task<bool> RespondNCAgentGroupInviteAsync(
        NCCharacterId invitee,
        long groupId,
        bool accept,
        DateTime respondedAt,
        int maximumMembers)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.RespondNCAgentGroupInviteAsync(
            invitee,
            groupId,
            accept,
            respondedAt,
            maximumMembers));
    }

    public Task<NCAgentGroupMessageData?> CreateNCAgentGroupMessageAsync(
        NCCharacterId sender,
        long groupId,
        string body,
        DateTime sentAt,
        DateTime expiresAt,
        int maximumMessageLength)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.CreateNCAgentGroupMessageAsync(
            sender,
            groupId,
            body,
            sentAt,
            expiresAt,
            maximumMessageLength));
    }

    public Task<IReadOnlyList<NCCharacterId>> GetNCAgentGroupRecipientIdsAsync(long groupId, NCCharacterId exclude)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCAgentGroupRecipientIdsAsync(groupId, exclude));
    }

    public Task<bool> RemoveNCAgentGroupMemberAsync(NCCharacterId owner, long groupId, NCCharacterId member)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.RemoveNCAgentGroupMemberAsync(owner, groupId, member));
    }

    public Task<bool> LeaveNCAgentGroupAsync(NCCharacterId member, long groupId, DateTime changedAt)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.LeaveNCAgentGroupAsync(member, groupId, changedAt));
    }

    public Task<bool> ClearNCAgentGroupConversationAsync(NCCharacterId owner, long groupId, DateTime clearedAt)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.ClearNCAgentGroupConversationAsync(owner, groupId, clearedAt));
    }

    public Task<int> PurgeExpiredNCAgentGroupMessagesAsync(DateTime now)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.PurgeExpiredNCAgentGroupMessagesAsync(now));
    }

    public Task<int> PurgeExpiredNCAgentGroupInvitesAsync(DateTime now)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.PurgeExpiredNCAgentGroupInvitesAsync(now));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<IReadOnlyList<NCAgentProfileData>> GetNCAgentProfilesAsync(
        IReadOnlyCollection<NCCharacterId> characters)
    {
        var ids = characters
            .Where(value => value.IsValid)
            .Select(value => value.Value)
            .Distinct()
            .Take(100)
            .ToList();
        if (ids.Count == 0)
            return Array.Empty<NCAgentProfileData>();

        await using var db = await GetDb();
        return await db.DbContext.NCAgentProfiles
            .AsNoTracking()
            .Where(value => ids.Contains(value.ProfileId))
            .Select(value => new NCAgentProfileData(
                new NCCharacterId(value.ProfileId),
                value.Alias ?? value.Profile.CharacterName,
                value.Address,
                (NCAgentPresenceStatus) value.Presence,
                value.OverlayNotificationsEnabled,
                value.NotificationSoundEnabled))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<NCCharacterId>> ResolveNCCitiNetMentionRecipientsAsync(
        NCCharacterId author,
        IReadOnlyCollection<string> normalizedAliases)
    {
        var aliases = normalizedAliases
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToUpperInvariant())
            .Distinct()
            .Take(100)
            .ToList();
        if (!author.IsValid || aliases.Count == 0)
            return Array.Empty<NCCharacterId>();

        await using var db = await GetDb();
        return await db.DbContext.NCAgentProfiles
            .AsNoTracking()
            .Where(value => value.ProfileId != author.Value &&
                aliases.Contains(value.NormalizedAlias ?? value.Profile.CharacterName.ToUpper()) &&
                !db.DbContext.NCAgentBlocks.Any(block =>
                    block.OwnerProfileId == author.Value && block.BlockedProfileId == value.ProfileId ||
                    block.OwnerProfileId == value.ProfileId && block.BlockedProfileId == author.Value))
            .Select(value => new NCCharacterId(value.ProfileId))
            .ToListAsync();
    }

    public async Task<bool> UpdateNCAgentPresenceAsync(NCCharacterId characterId, NCAgentPresenceStatus presence)
    {
        if (!characterId.IsValid || !Enum.IsDefined(presence))
            return false;

        await using var db = await GetDb();
        var changed = await db.DbContext.NCAgentProfiles
            .Where(value => value.ProfileId == characterId.Value)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Presence, (byte) presence)
                .SetProperty(value => value.UpdatedAt, DateTime.UtcNow));
        return changed == 1;
    }

    public async Task<bool> UpdateNCAgentNotificationSettingsAsync(
        NCCharacterId characterId,
        bool overlayEnabled,
        bool soundEnabled)
    {
        if (!characterId.IsValid)
            return false;

        await using var db = await GetDb();
        var changed = await db.DbContext.NCAgentProfiles
            .Where(value => value.ProfileId == characterId.Value)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.OverlayNotificationsEnabled, overlayEnabled)
                .SetProperty(value => value.NotificationSoundEnabled, soundEnabled)
                .SetProperty(value => value.UpdatedAt, DateTime.UtcNow));
        return changed == 1;
    }

    public async Task<bool> AreNCAgentProfilesBlockedAsync(NCCharacterId first, NCCharacterId second)
    {
        if (!first.IsValid || !second.IsValid || first == second)
            return false;

        await using var db = await GetDb();
        return await db.DbContext.NCAgentBlocks.AsNoTracking().AnyAsync(value =>
            value.OwnerProfileId == first.Value && value.BlockedProfileId == second.Value ||
            value.OwnerProfileId == second.Value && value.BlockedProfileId == first.Value);
    }

    public async Task<bool> SetNCAgentBlockedAsync(
        NCCharacterId owner,
        NCCharacterId target,
        bool blocked,
        DateTime changedAt)
    {
        if (!owner.IsValid || !target.IsValid || owner == target)
            return false;

        await using var db = await GetDb();
        if (!await ProfilesExistAsync(db.DbContext, owner, target))
            return false;

        var existing = await db.DbContext.NCAgentBlocks.SingleOrDefaultAsync(value =>
            value.OwnerProfileId == owner.Value && value.BlockedProfileId == target.Value);
        if (blocked && existing == null)
        {
            db.DbContext.NCAgentBlocks.Add(new NCAgentBlock
            {
                OwnerProfileId = owner.Value,
                BlockedProfileId = target.Value,
                CreatedAt = changedAt,
            });

            var contact = await db.DbContext.NCAgentContacts.SingleOrDefaultAsync(value =>
                value.OwnerProfileId == owner.Value && value.ContactProfileId == target.Value);
            if (contact != null)
                db.DbContext.NCAgentContacts.Remove(contact);
        }
        else if (!blocked && existing != null)
        {
            db.DbContext.NCAgentBlocks.Remove(existing);
        }

        await db.DbContext.SaveChangesAsync();
        return true;
    }

    public async Task<bool> AddNCAgentMutualContactsAsync(
        NCCharacterId first,
        NCCharacterId second,
        DateTime createdAt)
    {
        if (!first.IsValid || !second.IsValid || first == second)
            return false;

        await using var db = await GetDb();
        if (!await ProfilesExistAsync(db.DbContext, first, second) ||
            await db.DbContext.NCAgentBlocks.AnyAsync(value =>
                value.OwnerProfileId == first.Value && value.BlockedProfileId == second.Value ||
                value.OwnerProfileId == second.Value && value.BlockedProfileId == first.Value))
        {
            return false;
        }

        var existing = await db.DbContext.NCAgentContacts
            .Where(value => value.OwnerProfileId == first.Value && value.ContactProfileId == second.Value ||
                            value.OwnerProfileId == second.Value && value.ContactProfileId == first.Value)
            .Select(value => new { value.OwnerProfileId, value.ContactProfileId })
            .ToListAsync();
        if (!existing.Any(value => value.OwnerProfileId == first.Value))
            db.DbContext.NCAgentContacts.Add(NewContact(first, second, createdAt));
        if (!existing.Any(value => value.OwnerProfileId == second.Value))
            db.DbContext.NCAgentContacts.Add(NewContact(second, first, createdAt));

        await db.DbContext.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ClearNCAgentConversationAsync(
        NCCharacterId owner,
        NCCharacterId peer,
        DateTime clearedAt)
    {
        if (!owner.IsValid || !peer.IsValid || owner == peer)
            return false;

        await using var db = await GetDb();
        if (!await ProfilesExistAsync(db.DbContext, owner, peer))
            return false;

        var state = await db.DbContext.NCAgentDirectConversationStates.SingleOrDefaultAsync(value =>
            value.OwnerProfileId == owner.Value && value.PeerProfileId == peer.Value);
        if (state == null)
        {
            db.DbContext.NCAgentDirectConversationStates.Add(new NCAgentDirectConversationState
            {
                OwnerProfileId = owner.Value,
                PeerProfileId = peer.Value,
                ClearedAt = clearedAt,
            });
        }
        else
        {
            state.ClearedAt = clearedAt;
        }

        await db.DbContext.NCAgentMessages
            .Where(value => value.RecipientProfileId == owner.Value &&
                            value.SenderProfileId == peer.Value &&
                            value.SentAt <= clearedAt &&
                            value.ReadAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.ReadAt, clearedAt));
        await db.DbContext.SaveChangesAsync();
        return true;
    }

    public async Task<int> MarkNCAgentGroupReadAsync(NCCharacterId owner, long groupId, DateTime readAt)
    {
        if (!owner.IsValid || groupId <= 0)
            return 0;

        await using var db = await GetDb();
        return await db.DbContext.NCAgentGroupMembers
            .Where(value => value.GroupId == groupId && value.ProfileId == owner.Value)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.LastReadAt, readAt));
    }

    public async Task<NCAgentGroupCreationData?> CreateNCAgentGroupAsync(
        NCCharacterId owner,
        string name,
        IReadOnlyCollection<NCCharacterId> invitees,
        DateTime createdAt,
        DateTime inviteExpiresAt,
        int maximumNameLength,
        int maximumMembers)
    {
        var normalizedName = name.Trim();
        var distinctInvitees = invitees.Where(value => value.IsValid && value != owner).Distinct().ToList();
        if (!owner.IsValid || maximumNameLength < 1 || normalizedName.Length is < 1 ||
            normalizedName.Length > maximumNameLength ||
            maximumMembers < 2 || distinctInvitees.Count + 1 > maximumMembers ||
            inviteExpiresAt <= createdAt)
        {
            return null;
        }

        await using var db = await GetDb();
        if (!await db.DbContext.NCAgentProfiles.AnyAsync(value => value.ProfileId == owner.Value))
            return null;

        var allowedInvitees = await FilterAllowedInviteesAsync(db.DbContext, owner, distinctInvitees);
        var group = new NCAgentGroup
        {
            OwnerProfileId = owner.Value,
            Name = normalizedName,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        group.Members.Add(new NCAgentGroupMember
        {
            ProfileId = owner.Value,
            Role = (byte) NCAgentGroupRole.Owner,
            JoinedAt = createdAt,
            LastReadAt = createdAt,
        });
        foreach (var invitee in allowedInvitees)
        {
            db.DbContext.NCAgentGroupInvites.Add(new NCAgentGroupInvite
            {
                Group = group,
                InviteeProfileId = invitee.Value,
                InviterProfileId = owner.Value,
                CreatedAt = createdAt,
                ExpiresAt = inviteExpiresAt,
            });
        }

        db.DbContext.NCAgentGroups.Add(group);
        await db.DbContext.SaveChangesAsync();
        return new NCAgentGroupCreationData(group.Id, group.Name, allowedInvitees);
    }

    public async Task<bool> InviteNCAgentGroupMemberAsync(
        NCCharacterId owner,
        long groupId,
        NCCharacterId invitee,
        DateTime createdAt,
        DateTime expiresAt,
        int maximumMembers)
    {
        if (!owner.IsValid || !invitee.IsValid || owner == invitee || groupId <= 0 || expiresAt <= createdAt)
            return false;

        await using var db = await GetDb();
        var group = await db.DbContext.NCAgentGroups.SingleOrDefaultAsync(value =>
            value.Id == groupId && value.OwnerProfileId == owner.Value);
        if (group == null ||
            await db.DbContext.NCAgentGroupMembers.CountAsync(value => value.GroupId == groupId) +
            await db.DbContext.NCAgentGroupInvites.CountAsync(value => value.GroupId == groupId && value.ExpiresAt > createdAt) >=
            maximumMembers ||
            await db.DbContext.NCAgentGroupMembers.AnyAsync(value => value.GroupId == groupId && value.ProfileId == invitee.Value) ||
            await db.DbContext.NCAgentGroupInvites.AnyAsync(value => value.GroupId == groupId && value.InviteeProfileId == invitee.Value) ||
            !(await FilterAllowedInviteesAsync(db.DbContext, owner, [invitee])).Any())
        {
            return false;
        }

        db.DbContext.NCAgentGroupInvites.Add(new NCAgentGroupInvite
        {
            GroupId = groupId,
            InviteeProfileId = invitee.Value,
            InviterProfileId = owner.Value,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
        });
        await db.DbContext.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RespondNCAgentGroupInviteAsync(
        NCCharacterId invitee,
        long groupId,
        bool accept,
        DateTime respondedAt,
        int maximumMembers)
    {
        if (!invitee.IsValid || groupId <= 0)
            return false;

        await using var db = await GetDb();
        var invite = await db.DbContext.NCAgentGroupInvites
            .Include(value => value.Group)
            .SingleOrDefaultAsync(value => value.GroupId == groupId && value.InviteeProfileId == invitee.Value);
        if (invite == null)
            return false;

        db.DbContext.NCAgentGroupInvites.Remove(invite);
        if (accept && invite.ExpiresAt > respondedAt &&
            await db.DbContext.NCAgentGroupMembers.CountAsync(value => value.GroupId == groupId) < maximumMembers &&
            !await db.DbContext.NCAgentBlocks.AnyAsync(value =>
                value.OwnerProfileId == invite.Group.OwnerProfileId && value.BlockedProfileId == invitee.Value ||
                value.OwnerProfileId == invitee.Value && value.BlockedProfileId == invite.Group.OwnerProfileId))
        {
            db.DbContext.NCAgentGroupMembers.Add(new NCAgentGroupMember
            {
                GroupId = groupId,
                ProfileId = invitee.Value,
                Role = (byte) NCAgentGroupRole.Member,
                JoinedAt = respondedAt,
                LastReadAt = respondedAt,
            });
            invite.Group.UpdatedAt = respondedAt;
        }
        else if (accept)
        {
            await db.DbContext.SaveChangesAsync();
            return false;
        }

        await db.DbContext.SaveChangesAsync();
        return true;
    }

    public async Task<NCAgentGroupMessageData?> CreateNCAgentGroupMessageAsync(
        NCCharacterId sender,
        long groupId,
        string body,
        DateTime sentAt,
        DateTime expiresAt,
        int maximumMessageLength)
    {
        var normalized = body.Trim();
        if (!sender.IsValid || groupId <= 0 || maximumMessageLength < 1 ||
            normalized.Length is < 1 || normalized.Length > maximumMessageLength || expiresAt <= sentAt)
            return null;

        await using var db = await GetDb();
        if (!await db.DbContext.NCAgentGroupMembers.AnyAsync(value =>
                value.GroupId == groupId && value.ProfileId == sender.Value))
        {
            return null;
        }

        var profile = await db.DbContext.NCAgentProfiles
            .Include(value => value.Profile)
            .SingleAsync(value => value.ProfileId == sender.Value);
        var group = await db.DbContext.NCAgentGroups.SingleAsync(value => value.Id == groupId);
        var message = new NCAgentGroupMessage
        {
            GroupId = groupId,
            SenderProfileId = sender.Value,
            Body = normalized,
            SentAt = sentAt,
            ExpiresAt = expiresAt,
        };
        group.UpdatedAt = sentAt;
        db.DbContext.NCAgentGroupMessages.Add(message);
        await db.DbContext.SaveChangesAsync();
        return new NCAgentGroupMessageData(
            message.Id,
            groupId,
            sender,
            profile.Alias ?? profile.Profile.CharacterName,
            message.Body,
            sentAt);
    }

    public async Task<IReadOnlyList<NCCharacterId>> GetNCAgentGroupRecipientIdsAsync(
        long groupId,
        NCCharacterId exclude)
    {
        if (groupId <= 0)
            return Array.Empty<NCCharacterId>();

        await using var db = await GetDb();
        return await db.DbContext.NCAgentGroupMembers
            .AsNoTracking()
            .Where(value => value.GroupId == groupId && value.ProfileId != exclude.Value)
            .Select(value => new NCCharacterId(value.ProfileId))
            .ToListAsync();
    }

    public async Task<bool> RemoveNCAgentGroupMemberAsync(
        NCCharacterId owner,
        long groupId,
        NCCharacterId member)
    {
        if (!owner.IsValid || !member.IsValid || owner == member || groupId <= 0)
            return false;

        await using var db = await GetDb();
        if (!await db.DbContext.NCAgentGroups.AnyAsync(value =>
                value.Id == groupId && value.OwnerProfileId == owner.Value))
        {
            return false;
        }

        var membership = await db.DbContext.NCAgentGroupMembers.SingleOrDefaultAsync(value =>
            value.GroupId == groupId && value.ProfileId == member.Value);
        if (membership == null)
            return false;

        db.DbContext.NCAgentGroupMembers.Remove(membership);
        await db.DbContext.SaveChangesAsync();
        return true;
    }

    public async Task<bool> LeaveNCAgentGroupAsync(NCCharacterId member, long groupId, DateTime changedAt)
    {
        if (!member.IsValid || groupId <= 0)
            return false;

        await using var db = await GetDb();
        var group = await db.DbContext.NCAgentGroups.SingleOrDefaultAsync(value => value.Id == groupId);
        var membership = await db.DbContext.NCAgentGroupMembers.SingleOrDefaultAsync(value =>
            value.GroupId == groupId && value.ProfileId == member.Value);
        if (group == null || membership == null)
            return false;

        if (group.OwnerProfileId == member.Value)
        {
            var successor = await db.DbContext.NCAgentGroupMembers
                .Where(value => value.GroupId == groupId && value.ProfileId != member.Value)
                .OrderBy(value => value.JoinedAt)
                .FirstOrDefaultAsync();
            if (successor == null)
            {
                db.DbContext.NCAgentGroups.Remove(group);
                await db.DbContext.SaveChangesAsync();
                return true;
            }

            successor.Role = (byte) NCAgentGroupRole.Owner;
            group.OwnerProfileId = successor.ProfileId;
        }

        group.UpdatedAt = changedAt;
        db.DbContext.NCAgentGroupMembers.Remove(membership);
        await db.DbContext.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ClearNCAgentGroupConversationAsync(
        NCCharacterId owner,
        long groupId,
        DateTime clearedAt)
    {
        if (!owner.IsValid || groupId <= 0)
            return false;

        await using var db = await GetDb();
        var changed = await db.DbContext.NCAgentGroupMembers
            .Where(value => value.GroupId == groupId && value.ProfileId == owner.Value)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.ClearedAt, clearedAt)
                .SetProperty(value => value.LastReadAt, clearedAt));
        return changed == 1;
    }

    public async Task<int> PurgeExpiredNCAgentGroupMessagesAsync(DateTime now)
    {
        await using var db = await GetDb();
        return await db.DbContext.NCAgentGroupMessages
            .Where(value => value.ExpiresAt <= now)
            .ExecuteDeleteAsync();
    }

    public async Task<int> PurgeExpiredNCAgentGroupInvitesAsync(DateTime now)
    {
        await using var db = await GetDb();
        return await db.DbContext.NCAgentGroupInvites
            .Where(value => value.ExpiresAt <= now)
            .ExecuteDeleteAsync();
    }

    private static NCAgentContact NewContact(NCCharacterId owner, NCCharacterId contact, DateTime createdAt)
    {
        return new NCAgentContact
        {
            OwnerProfileId = owner.Value,
            ContactProfileId = contact.Value,
            CreatedAt = createdAt,
        };
    }

    private static async Task<bool> ProfilesExistAsync(
        ServerDbContext context,
        NCCharacterId first,
        NCCharacterId second)
    {
        return await context.NCAgentProfiles.CountAsync(value =>
            value.ProfileId == first.Value || value.ProfileId == second.Value) == 2;
    }

    private static async Task<List<NCCharacterId>> FilterAllowedInviteesAsync(
        ServerDbContext context,
        NCCharacterId owner,
        IReadOnlyCollection<NCCharacterId> invitees)
    {
        var ids = invitees.Select(value => value.Value).Distinct().ToList();
        return await context.NCAgentContacts
            .AsNoTracking()
            .Where(value => value.OwnerProfileId == owner.Value && ids.Contains(value.ContactProfileId) &&
                !context.NCAgentBlocks.Any(block =>
                    block.OwnerProfileId == owner.Value && block.BlockedProfileId == value.ContactProfileId ||
                    block.OwnerProfileId == value.ContactProfileId && block.BlockedProfileId == owner.Value))
            .Select(value => new NCCharacterId(value.ContactProfileId))
            .ToListAsync();
    }
}
