// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Content.Shared._NC.Identity;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCAgentProfileData(NCCharacterId CharacterId, string CharacterName, string Address);

public sealed record NCAgentDirectoryData(
    NCCharacterId CharacterId,
    string CharacterName,
    string Address,
    bool IsContact);

public sealed record NCAgentContactData(NCCharacterId CharacterId, string CharacterName, string Address);

public sealed record NCAgentMessageData(
    long MessageId,
    NCCharacterId SenderCharacterId,
    string SenderName,
    NCCharacterId RecipientCharacterId,
    string RecipientName,
    string Body,
    DateTime SentAt);

public sealed record NCCitiNetCommentData(
    long CommentId,
    NCCharacterId AuthorCharacterId,
    string AuthorName,
    string Body,
    DateTime CreatedAt);

public sealed record NCCitiNetPostData(
    long PostId,
    NCCharacterId AuthorCharacterId,
    string AuthorName,
    string Body,
    DateTime CreatedAt,
    int ReactionCount,
    bool ReactedByViewer,
    IReadOnlyList<NCCitiNetCommentData> Comments);

public sealed record NCAgentSnapshotData(
    NCAgentProfileData Profile,
    IReadOnlyList<NCAgentDirectoryData> Directory,
    IReadOnlyList<NCAgentContactData> Contacts,
    IReadOnlyList<NCAgentMessageData> Messages,
    IReadOnlyList<NCCitiNetPostData> Posts);

public partial interface IServerDbManager
{
    Task<NCAgentProfileData?> GetOrCreateNCAgentProfileAsync(NCCharacterId characterId, string addressPrefix);
    Task<NCAgentSnapshotData?> GetNCAgentSnapshotAsync(
        NCCharacterId characterId,
        NCCharacterId? selectedCharacterId,
        string directoryQuery,
        DateTime now,
        int directoryLimit,
        int messageLimit,
        int postLimit,
        int commentLimit);
    Task<bool> AddNCAgentContactAsync(NCCharacterId owner, NCCharacterId contact);
    Task<bool> RemoveNCAgentContactAsync(NCCharacterId owner, NCCharacterId contact);
    Task<NCAgentMessageData?> CreateNCAgentMessageAsync(
        NCCharacterId sender,
        NCCharacterId recipient,
        string body,
        DateTime sentAt,
        DateTime expiresAt);
    Task<NCCitiNetPostData?> CreateNCCitiNetPostAsync(NCCharacterId author, string body, DateTime createdAt);
    Task<NCCitiNetCommentData?> CreateNCCitiNetCommentAsync(
        NCCharacterId author,
        long postId,
        string body,
        DateTime createdAt);
    Task<bool?> ToggleNCCitiNetReactionAsync(NCCharacterId characterId, long postId, DateTime createdAt);
    Task<int> PurgeExpiredNCAgentMessagesAsync(DateTime now);
}

public sealed partial class ServerDbManager
{
    public Task<NCAgentProfileData?> GetOrCreateNCAgentProfileAsync(NCCharacterId characterId, string addressPrefix)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.GetOrCreateNCAgentProfileAsync(characterId, addressPrefix));
    }

    public Task<NCAgentSnapshotData?> GetNCAgentSnapshotAsync(
        NCCharacterId characterId,
        NCCharacterId? selectedCharacterId,
        string directoryQuery,
        DateTime now,
        int directoryLimit,
        int messageLimit,
        int postLimit,
        int commentLimit)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCAgentSnapshotAsync(
            characterId,
            selectedCharacterId,
            directoryQuery,
            now,
            directoryLimit,
            messageLimit,
            postLimit,
            commentLimit));
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

    public Task<NCCitiNetCommentData?> CreateNCCitiNetCommentAsync(
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

    public Task<int> PurgeExpiredNCAgentMessagesAsync(DateTime now)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.PurgeExpiredNCAgentMessagesAsync(now));
    }
}

public abstract partial class ServerDbBase
{
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
        string directoryQuery,
        DateTime now,
        int directoryLimit,
        int messageLimit,
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

        var contacts = await db.DbContext.NCAgentContacts
            .AsNoTracking()
            .Where(value => value.OwnerProfileId == characterId.Value)
            .OrderBy(value => value.Contact.Profile.CharacterName)
            .Select(value => new NCAgentContactData(
                new NCCharacterId(value.ContactProfileId),
                value.Contact.Profile.CharacterName,
                value.Contact.Address))
            .ToListAsync();

        var contactIds = contacts.Select(value => value.CharacterId.Value).ToList();
        var normalizedQuery = directoryQuery.Trim().ToLowerInvariant();
        IReadOnlyList<NCAgentDirectoryData> directory = Array.Empty<NCAgentDirectoryData>();
        if (normalizedQuery.Length > 0)
        {
            directory = await db.DbContext.NCAgentProfiles
                .AsNoTracking()
                .Where(value => value.ProfileId != characterId.Value &&
                    (value.Address.ToLower().Contains(normalizedQuery) ||
                     value.Profile.CharacterName.ToLower().Contains(normalizedQuery)))
                .OrderBy(value => value.Profile.CharacterName)
                .Take(Math.Clamp(directoryLimit, 1, 100))
                .Select(value => new NCAgentDirectoryData(
                    new NCCharacterId(value.ProfileId),
                    value.Profile.CharacterName,
                    value.Address,
                    contactIds.Contains(value.ProfileId)))
                .ToListAsync();
        }

        IReadOnlyList<NCAgentMessageData> messages = Array.Empty<NCAgentMessageData>();
        if (selectedCharacterId is { IsValid: true } selected &&
            await db.DbContext.NCAgentProfiles.AnyAsync(value => value.ProfileId == selected.Value))
        {
            var newestFirst = await db.DbContext.NCAgentMessages
                .AsNoTracking()
                .Where(value => value.ExpiresAt > now &&
                    (value.SenderProfileId == characterId.Value && value.RecipientProfileId == selected.Value ||
                     value.SenderProfileId == selected.Value && value.RecipientProfileId == characterId.Value))
                .OrderByDescending(value => value.SentAt)
                .Take(Math.Clamp(messageLimit, 1, 500))
                .Select(value => new NCAgentMessageData(
                    value.Id,
                    new NCCharacterId(value.SenderProfileId),
                    value.Sender.Profile.CharacterName,
                    new NCCharacterId(value.RecipientProfileId),
                    value.Recipient.Profile.CharacterName,
                    value.Body,
                    value.SentAt))
                .ToListAsync();
            newestFirst.Reverse();
            messages = newestFirst;
        }

        var posts = await db.DbContext.NCCitiNetPosts
            .AsNoTracking()
            .OrderByDescending(value => value.CreatedAt)
            .Take(Math.Clamp(postLimit, 1, 200))
            .Select(value => new NCCitiNetPostData(
                value.Id,
                new NCCharacterId(value.AuthorProfileId),
                value.Author.Profile.CharacterName,
                value.Body,
                value.CreatedAt,
                value.Reactions.Count,
                value.Reactions.Any(reaction => reaction.ProfileId == characterId.Value),
                Array.Empty<NCCitiNetCommentData>()))
            .ToListAsync();

        var postIds = posts.Select(value => value.PostId).ToList();
        var comments = await db.DbContext.NCCitiNetComments
            .AsNoTracking()
            .Where(value => postIds.Contains(value.PostId))
            .OrderByDescending(value => value.CreatedAt)
            .Take(Math.Clamp(commentLimit, 1, 1000))
            .Select(value => new
            {
                value.PostId,
                Data = new NCCitiNetCommentData(
                    value.Id,
                    new NCCharacterId(value.AuthorProfileId),
                    value.Author.Profile.CharacterName,
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

        return new NCAgentSnapshotData(ToProfileData(profile), directory, contacts, messages, postsWithComments);
    }

    public async Task<bool> AddNCAgentContactAsync(NCCharacterId owner, NCCharacterId contact)
    {
        if (!owner.IsValid || !contact.IsValid || owner == contact)
            return false;

        await using var db = await GetDb();
        if (!await db.DbContext.NCAgentProfiles.AnyAsync(value => value.ProfileId == owner.Value) ||
            !await db.DbContext.NCAgentProfiles.AnyAsync(value => value.ProfileId == contact.Value))
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
            senderProfile.Profile.CharacterName,
            recipient,
            recipientProfile.Profile.CharacterName,
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
            profile.Profile.CharacterName,
            post.Body,
            post.CreatedAt,
            0,
            false,
            Array.Empty<NCCitiNetCommentData>());
    }

    public async Task<NCCitiNetCommentData?> CreateNCCitiNetCommentAsync(
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
        if (profile == null || !await db.DbContext.NCCitiNetPosts.AnyAsync(value => value.Id == postId))
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
        return new NCCitiNetCommentData(
            comment.Id,
            author,
            profile.Profile.CharacterName,
            comment.Body,
            comment.CreatedAt);
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
            !await db.DbContext.NCCitiNetPosts.AnyAsync(value => value.Id == postId))
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

    public async Task<int> PurgeExpiredNCAgentMessagesAsync(DateTime now)
    {
        await using var db = await GetDb();
        return await db.DbContext.NCAgentMessages
            .Where(value => value.ExpiresAt <= now)
            .ExecuteDeleteAsync();
    }

    private static NCAgentProfileData ToProfileData(NCAgentProfile profile)
    {
        return new NCAgentProfileData(
            new NCCharacterId(profile.ProfileId),
            profile.Profile.CharacterName,
            profile.Address);
    }
}
