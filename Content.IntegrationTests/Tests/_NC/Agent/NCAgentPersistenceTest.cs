// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Shared._NC.Agent;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.Identity;
using Content.Shared.Preferences;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.Agent;

[TestFixture]
[TestOf(typeof(ServerDbBase))]
public sealed class NCAgentPersistenceTest : GameTest
{
    private static readonly ProtoId<NCAgentConfigurationPrototype> DefaultConfiguration = "Default";

    [Test]
    public void DefaultAgentPolicyLoadsFromYaml()
    {
        var configuration = Pair.Server.ResolveDependency<IPrototypeManager>()
            .Index(DefaultConfiguration);

        Assert.Multiple(() =>
        {
            Assert.That(configuration.AddressPrefix, Is.EqualTo("NC"));
            Assert.That(configuration.MessageRetention, Is.EqualTo(TimeSpan.FromDays(30)));
            Assert.That(configuration.MaximumMessageLength, Is.EqualTo(1024));
            Assert.That(configuration.MinimumAliasLength, Is.EqualTo(3));
            Assert.That(configuration.MaximumAliasLength, Is.EqualTo(32));
            Assert.That(configuration.ConversationPeerLimit, Is.EqualTo(50));
            Assert.That(configuration.IncomingCallTimeout, Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(configuration.CallHistoryRetention, Is.EqualTo(TimeSpan.FromDays(30)));
            Assert.That(configuration.CallHistoryLimit, Is.EqualTo(50));
            Assert.That(configuration.MaximumGroupMembers, Is.EqualTo(20));
            Assert.That(configuration.GroupConversationLimit, Is.EqualTo(30));
            Assert.That(configuration.GroupMessageLimit, Is.EqualTo(100));
            Assert.That(configuration.NotificationOverlayLimit, Is.EqualTo(3));
            Assert.That(configuration.NotificationOverlayDuration, Is.EqualTo(TimeSpan.FromSeconds(6)));
            Assert.That(configuration.ContactShareRange, Is.EqualTo(2));
            Assert.That(configuration.CitiNetPostRetention, Is.EqualTo(TimeSpan.FromDays(30)));
            Assert.That(configuration.CitiNetPostCooldown, Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(configuration.CitiNetCommentCooldown, Is.EqualTo(TimeSpan.FromSeconds(3)));
            Assert.That(configuration.CitiNetReactionCooldown, Is.EqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(configuration.MaximumMentionsPerEntry, Is.EqualTo(10));
            Assert.That(configuration.FeedPostLimit, Is.EqualTo(20));
        });
    }

    [Test]
    public async Task CitiNetRespectsPrivacyPagingDeletionMentionsAndRetention()
    {
        var db = GetDb(Pair.Server);
        var (_, viewer) = await CreateCharacterAsync(db, "Feed Viewer");
        var (_, firstAuthor) = await CreateCharacterAsync(db, "First Author");
        var (_, blockedAuthor) = await CreateCharacterAsync(db, "Blocked Author");
        var (_, newestAuthor) = await CreateCharacterAsync(db, "Newest Author");
        var now = DateTime.UtcNow;

        foreach (var character in new[] { viewer, firstAuthor, blockedAuthor, newestAuthor })
            Assert.That(await db.GetOrCreateNCAgentProfileAsync(character, "NC"), Is.Not.Null);
        Assert.That(await db.UpdateNCAgentAliasAsync(firstAuthor, "Chrome Ghost", "CHROME GHOST"),
            Is.EqualTo(NCAgentAliasUpdateResult.Updated));
        Assert.That(await db.UpdateNCAgentAliasAsync(newestAuthor, "StaticBloom", "STATICBLOOM"),
            Is.EqualTo(NCAgentAliasUpdateResult.Updated));

        var firstPost = await db.CreateNCCitiNetPostAsync(firstAuthor, "First visible post", now);
        var blockedPost = await db.CreateNCCitiNetPostAsync(blockedAuthor, "Hidden post", now.AddMinutes(1));
        var newestPost = await db.CreateNCCitiNetPostAsync(newestAuthor, "Newest visible post", now.AddMinutes(2));
        Assert.That(firstPost, Is.Not.Null);
        Assert.That(blockedPost, Is.Not.Null);
        Assert.That(newestPost, Is.Not.Null);

        Assert.That(await db.SetNCAgentBlockedAsync(viewer, blockedAuthor, true, now.AddMinutes(3)), Is.True);
        Assert.That(await db.CreateNCCitiNetCommentAsync(
            blockedAuthor,
            newestPost!.PostId,
            "Hidden comment",
            now.AddMinutes(4)), Is.Not.Null);
        Assert.That(await db.CreateNCCitiNetCommentAsync(
            firstAuthor,
            newestPost.PostId,
            "Visible comment",
            now.AddMinutes(5)), Is.Not.Null);

        var firstPage = await db.GetNCAgentSnapshotAsync(
            viewer, null, null, string.Empty, now.AddMinutes(6), 30, 50, 100, 50, 30, 100, 0, 1, 200);
        var secondPage = await db.GetNCAgentSnapshotAsync(
            viewer, null, null, string.Empty, now.AddMinutes(6), 30, 50, 100, 50, 30, 100, 1, 1, 200);
        Assert.Multiple(() =>
        {
            Assert.That(firstPage!.Posts.Single().PostId, Is.EqualTo(newestPost.PostId));
            Assert.That(firstPage.Posts.Single().Comments.Single().Body, Is.EqualTo("Visible comment"));
            Assert.That(firstPage.FeedHasOlderPosts, Is.True);
            Assert.That(secondPage!.Posts.Single().PostId, Is.EqualTo(firstPost!.PostId));
            Assert.That(secondPage.FeedHasOlderPosts, Is.False);
        });

        Assert.That(await db.CreateNCCitiNetCommentAsync(
            viewer,
            blockedPost!.PostId,
            "Must be rejected",
            now.AddMinutes(7)), Is.Null);
        Assert.That(await db.ToggleNCCitiNetReactionAsync(viewer, blockedPost.PostId, now.AddMinutes(7)), Is.Null);

        var mentionTargets = await db.ResolveNCCitiNetMentionRecipientsAsync(
            viewer,
            new[] { "CHROME GHOST", "STATICBLOOM", "BLOCKED AUTHOR" });
        Assert.That(mentionTargets, Is.EquivalentTo(new[] { firstAuthor, newestAuthor }));

        Assert.That(await db.DeleteNCCitiNetPostAsync(viewer, firstPost!.PostId), Is.False);
        Assert.That(await db.DeleteNCCitiNetPostAsync(firstAuthor, firstPost.PostId), Is.True);
        Assert.That(await db.PurgeExpiredNCCitiNetPostsAsync(now.AddDays(31)), Is.EqualTo(2));
    }

    [Test]
    public async Task PrivacyPresenceAndGroupsRemainCharacterScoped()
    {
        var db = GetDb(Pair.Server);
        var (_, owner) = await CreateCharacterAsync(db, "Rogue Signal");
        var (_, member) = await CreateCharacterAsync(db, "Neon Shade");
        var (_, invitee) = await CreateCharacterAsync(db, "Static Bloom");
        var now = DateTime.UtcNow;

        Assert.That(await db.GetOrCreateNCAgentProfileAsync(owner, "NC"), Is.Not.Null);
        Assert.That(await db.GetOrCreateNCAgentProfileAsync(member, "NC"), Is.Not.Null);
        Assert.That(await db.GetOrCreateNCAgentProfileAsync(invitee, "NC"), Is.Not.Null);
        Assert.That(await db.UpdateNCAgentPresenceAsync(member, NCAgentPresenceStatus.DoNotDisturb), Is.True);
        Assert.That(await db.UpdateNCAgentNotificationSettingsAsync(member, false, false), Is.True);
        Assert.That(await db.AddNCAgentMutualContactsAsync(owner, member, now), Is.True);
        Assert.That(await db.AddNCAgentMutualContactsAsync(owner, invitee, now), Is.True);

        var memberSnapshot = await GetSnapshotAsync(db, member, owner, null, string.Empty, now);
        Assert.Multiple(() =>
        {
            Assert.That(memberSnapshot!.Profile.Presence, Is.EqualTo(NCAgentPresenceStatus.DoNotDisturb));
            Assert.That(memberSnapshot.Profile.OverlayNotificationsEnabled, Is.False);
            Assert.That(memberSnapshot.Profile.NotificationSoundEnabled, Is.False);
            Assert.That(memberSnapshot.Contacts.Single().CharacterId, Is.EqualTo(owner));
        });

        Assert.That(await db.CreateNCAgentMessageAsync(
            owner,
            member,
            "Burn after reading.",
            now.AddSeconds(1),
            now.AddDays(30)), Is.Not.Null);
        Assert.That(await db.ClearNCAgentConversationAsync(member, owner, now.AddSeconds(2)), Is.True);
        memberSnapshot = await GetSnapshotAsync(db, member, owner, null, string.Empty, now.AddSeconds(3));
        Assert.Multiple(() =>
        {
            Assert.That(memberSnapshot!.Messages, Is.Empty);
            Assert.That(memberSnapshot.UnreadMessageCount, Is.EqualTo(0));
        });

        Assert.That(await db.SetNCAgentBlockedAsync(member, owner, true, now.AddSeconds(4)), Is.True);
        Assert.That(await db.AreNCAgentProfilesBlockedAsync(owner, member), Is.True);
        Assert.That(await db.CreateNCAgentMessageAsync(
            owner,
            member,
            "This must not arrive.",
            now.AddSeconds(5),
            now.AddDays(30)), Is.Null);
        memberSnapshot = await GetSnapshotAsync(db, member, null, null, "Rogue", now.AddSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(memberSnapshot!.Blocked.Single().CharacterId, Is.EqualTo(owner));
            Assert.That(memberSnapshot.Directory, Is.Empty);
            Assert.That(memberSnapshot.Contacts, Is.Empty);
        });
        Assert.That(await db.SetNCAgentBlockedAsync(member, owner, false, now.AddSeconds(6)), Is.True);

        var group = await db.CreateNCAgentGroupAsync(
            owner,
            "Afterlife Crew",
            new[] { member, invitee },
            now.AddSeconds(7),
            now.AddDays(1),
            48,
            20);
        Assert.That(group, Is.Not.Null);
        Assert.That(group!.InvitedCharacterIds, Is.EquivalentTo(new[] { member, invitee }));

        memberSnapshot = await GetSnapshotAsync(db, member, null, null, string.Empty, now.AddSeconds(8));
        Assert.That(memberSnapshot!.GroupInvites.Single().GroupId, Is.EqualTo(group.GroupId));
        Assert.That(await db.RespondNCAgentGroupInviteAsync(
            member,
            group.GroupId,
            true,
            now.AddSeconds(9),
            20), Is.True);
        Assert.That(await db.CreateNCAgentGroupMessageAsync(
            owner,
            group.GroupId,
            "One crew, one encrypted channel.",
            now.AddSeconds(10),
            now.AddDays(30),
            1024), Is.Not.Null);

        memberSnapshot = await GetSnapshotAsync(db, member, null, group.GroupId, string.Empty, now.AddSeconds(11));
        Assert.Multiple(() =>
        {
            Assert.That(memberSnapshot!.Groups.Single().UnreadCount, Is.EqualTo(1));
            Assert.That(memberSnapshot.UnreadGroupMessageCount, Is.EqualTo(1));
            Assert.That(memberSnapshot.GroupMessages.Single().Body, Is.EqualTo("One crew, one encrypted channel."));
            Assert.That(memberSnapshot.GroupMembers, Has.Count.EqualTo(2));
        });

        Assert.That(await db.ClearNCAgentGroupConversationAsync(member, group.GroupId, now.AddSeconds(12)), Is.True);
        memberSnapshot = await GetSnapshotAsync(db, member, null, group.GroupId, string.Empty, now.AddSeconds(13));
        Assert.Multiple(() =>
        {
            Assert.That(memberSnapshot!.GroupMessages, Is.Empty);
            Assert.That(memberSnapshot.UnreadGroupMessageCount, Is.EqualTo(0));
        });

        Assert.That(await db.LeaveNCAgentGroupAsync(owner, group.GroupId, now.AddSeconds(14)), Is.True);
        memberSnapshot = await GetSnapshotAsync(db, member, null, group.GroupId, string.Empty, now.AddSeconds(15));
        Assert.That(memberSnapshot!.Groups.Single().Role, Is.EqualTo(NCAgentGroupRole.Owner));
        Assert.That(await db.PurgeExpiredNCAgentGroupMessagesAsync(now.AddDays(31)), Is.EqualTo(1));
        Assert.That(await db.PurgeExpiredNCAgentGroupInvitesAsync(now.AddDays(2)), Is.EqualTo(1));
    }

    [Test]
    public async Task AgentServicesPersistAndMessagesExpire()
    {
        var db = GetDb(Pair.Server);
        var (_, first) = await CreateCharacterAsync(db, "Mara Velez");
        var (_, second) = await CreateCharacterAsync(db, "Victor Chen");
        var now = DateTime.UtcNow;

        var firstProfile = await db.GetOrCreateNCAgentProfileAsync(first, "NC");
        var repeatedProfile = await db.GetOrCreateNCAgentProfileAsync(first, "NC");
        var secondProfile = await db.GetOrCreateNCAgentProfileAsync(second, "NC");
        Assert.That(firstProfile, Is.Not.Null);
        Assert.That(secondProfile, Is.Not.Null);

        Assert.That(await db.AddNCAgentContactAsync(first, second), Is.True);
        Assert.That(await db.CreateNCAgentMessageAsync(first, second, "Meet at the Afterlife.", now, now.AddDays(30)),
            Is.Not.Null);
        var post = await db.CreateNCCitiNetPostAsync(second, "Fresh chrome on the market.", now);
        Assert.That(post, Is.Not.Null);
        Assert.That(await db.CreateNCCitiNetCommentAsync(first, post!.PostId, "Send details.", now.AddMinutes(1)),
            Is.Not.Null);
        Assert.That(await db.ToggleNCCitiNetReactionAsync(first, post.PostId, now.AddMinutes(2)), Is.True);

        // Existing network content resolves its visible names from the current profile alias.
        Assert.That(await db.UpdateNCAgentAliasAsync(first, "Neon Raven", "NEON RAVEN"),
            Is.EqualTo(NCAgentAliasUpdateResult.Updated));
        Assert.That(await db.UpdateNCAgentAliasAsync(second, "Chrome Ghost", "CHROME GHOST"),
            Is.EqualTo(NCAgentAliasUpdateResult.Updated));
        Assert.That(await db.UpdateNCAgentAliasAsync(first, "chrome ghost", "CHROME GHOST"),
            Is.EqualTo(NCAgentAliasUpdateResult.Duplicate));

        var snapshot = await db.GetNCAgentSnapshotAsync(
            first, second, null, "chrome", now, 30, 50, 100, 50, 30, 100, 0, 50, 200);
        var recipientSnapshot = await db.GetNCAgentSnapshotAsync(
            second, first, null, string.Empty, now, 30, 50, 100, 50, 30, 100, 0, 50, 200);
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(recipientSnapshot, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(repeatedProfile!.Address, Is.EqualTo(firstProfile!.Address));
            Assert.That(firstProfile.Address, Does.StartWith("NC-"));
            Assert.That(firstProfile.Address, Is.Not.EqualTo(secondProfile!.Address));
            Assert.That(snapshot!.Profile.Alias, Is.EqualTo("Neon Raven"));
            Assert.That(snapshot!.Directory.Single().CharacterId, Is.EqualTo(second));
            Assert.That(snapshot.Directory.Single().Alias, Is.EqualTo("Chrome Ghost"));
            Assert.That(snapshot.Contacts.Single().CharacterId, Is.EqualTo(second));
            Assert.That(snapshot.Contacts.Single().Alias, Is.EqualTo("Chrome Ghost"));
            Assert.That(snapshot.Conversations.Single().Alias, Is.EqualTo("Chrome Ghost"));
            Assert.That(snapshot.Conversations.Single().UnreadCount, Is.EqualTo(0));
            Assert.That(snapshot.Messages.Single().Body, Is.EqualTo("Meet at the Afterlife."));
            Assert.That(snapshot.Messages.Single().SenderAlias, Is.EqualTo("Neon Raven"));
            Assert.That(snapshot.Messages.Single().RecipientAlias, Is.EqualTo("Chrome Ghost"));
            Assert.That(snapshot.Posts.Single().ReactedByViewer, Is.True);
            Assert.That(snapshot.Posts.Single().ReactionCount, Is.EqualTo(1));
            Assert.That(snapshot.Posts.Single().AuthorAlias, Is.EqualTo("Chrome Ghost"));
            Assert.That(snapshot.Posts.Single().Comments.Single().Body, Is.EqualTo("Send details."));
            Assert.That(snapshot.Posts.Single().Comments.Single().AuthorAlias, Is.EqualTo("Neon Raven"));
            Assert.That(recipientSnapshot!.Contacts, Is.Empty);
            Assert.That(recipientSnapshot.Conversations.Single().Alias, Is.EqualTo("Neon Raven"));
            Assert.That(recipientSnapshot.Conversations.Single().UnreadCount, Is.EqualTo(1));
            Assert.That(recipientSnapshot.UnreadMessageCount, Is.EqualTo(1));
            Assert.That(recipientSnapshot.Messages.Single().Body, Is.EqualTo("Meet at the Afterlife."));
        });

        // Reading is scoped to the recipient and selected peer; it must not acknowledge another character's mail.
        Assert.That(await db.MarkNCAgentConversationReadAsync(second, first, now.AddMinutes(3)), Is.EqualTo(1));
        Assert.That(await db.MarkNCAgentConversationReadAsync(first, second, now.AddMinutes(3)), Is.EqualTo(0));

        var completedCall = await db.CreateNCAgentCallLogAsync(
            first,
            second,
            now.AddMinutes(4),
            now.AddMinutes(5),
            now.AddMinutes(6),
            NCAgentCallOutcome.Completed);
        var missedCall = await db.CreateNCAgentCallLogAsync(
            first,
            second,
            now.AddMinutes(7),
            null,
            now.AddMinutes(8),
            NCAgentCallOutcome.Missed);
        Assert.That(completedCall, Is.Not.Null);
        Assert.That(missedCall, Is.Not.Null);

        recipientSnapshot = await db.GetNCAgentSnapshotAsync(
            second,
            first,
            null,
            string.Empty,
            now.AddMinutes(9),
            30,
            50,
            100,
            50,
            30,
            100,
            0,
            50,
            200);
        Assert.Multiple(() =>
        {
            Assert.That(recipientSnapshot!.Conversations.Single().UnreadCount, Is.EqualTo(0));
            Assert.That(recipientSnapshot.UnreadMessageCount, Is.EqualTo(0));
            Assert.That(recipientSnapshot.CallHistory, Has.Count.EqualTo(2));
            Assert.That(recipientSnapshot.CallHistory.Count(value => value.IsUnread), Is.EqualTo(1));
            Assert.That(recipientSnapshot.MissedCallCount, Is.EqualTo(1));
            Assert.That(recipientSnapshot.CallHistory.First().Outcome, Is.EqualTo(NCAgentCallOutcome.Missed));
            Assert.That(recipientSnapshot.CallHistory.First().RemoteAlias, Is.EqualTo("Neon Raven"));
        });

        Assert.That(await db.MarkNCAgentCallHistoryReadAsync(second, now.AddMinutes(10)), Is.EqualTo(1));
        recipientSnapshot = await db.GetNCAgentSnapshotAsync(
            second,
            first,
            null,
            string.Empty,
            now.AddMinutes(10),
            30,
            50,
            100,
            50,
            30,
            100,
            0,
            50,
            200);
        Assert.Multiple(() =>
        {
            Assert.That(recipientSnapshot!.CallHistory, Has.None.Matches<NCAgentCallHistoryData>(value => value.IsUnread));
            Assert.That(recipientSnapshot.MissedCallCount, Is.EqualTo(0));
        });

        Assert.That(await db.PurgeExpiredNCAgentMessagesAsync(now.AddDays(31)), Is.EqualTo(1));
        Assert.That(await db.PurgeExpiredNCAgentCallLogsAsync(now.AddDays(31)), Is.EqualTo(2));
        var expiredSnapshot = await db.GetNCAgentSnapshotAsync(
            first,
            second,
            null,
            string.Empty,
            now.AddDays(31),
            30,
            50,
            100,
            50,
            30,
            100,
            0,
            50,
            200);
        Assert.That(expiredSnapshot!.Messages, Is.Empty);
        Assert.That(expiredSnapshot.Conversations, Is.Empty);
        Assert.That(expiredSnapshot.CallHistory, Is.Empty);
        Assert.That(expiredSnapshot.UnreadMessageCount, Is.EqualTo(0));
        Assert.That(expiredSnapshot.MissedCallCount, Is.EqualTo(0));
    }

    [Test]
    public async Task CharacterDeletionCascadesAgentData()
    {
        var db = GetDb(Pair.Server);
        var (userId, characterId) = await CreateCharacterAsync(db, "Disposable Edge");
        var (_, peerId) = await CreateCharacterAsync(db, "Patient Zero");
        Assert.That(await db.GetOrCreateNCAgentProfileAsync(characterId, "NC"), Is.Not.Null);
        Assert.That(await db.GetOrCreateNCAgentProfileAsync(peerId, "NC"), Is.Not.Null);
        var now = DateTime.UtcNow;
        Assert.That(await db.CreateNCAgentCallLogAsync(
            peerId,
            characterId,
            now,
            null,
            now.AddSeconds(30),
            NCAgentCallOutcome.Missed), Is.Not.Null);

        // Agent records are character-owned, so the existing profile cascade is the deletion boundary.
        await db.SaveCharacterSlotAsync(userId, null, 0);

        var snapshot = await db.GetNCAgentSnapshotAsync(
            characterId,
            null,
            null,
            string.Empty,
            DateTime.UtcNow,
            30,
            50,
            100,
            50,
            30,
            100,
            0,
            50,
            200);
        Assert.That(snapshot, Is.Null);

        // Both call-log foreign keys cascade, so the surviving peer cannot retain a dangling history row.
        var peerSnapshot = await db.GetNCAgentSnapshotAsync(
            peerId,
            null,
            null,
            string.Empty,
            now.AddMinutes(1),
            30,
            50,
            100,
            50,
            30,
            100,
            0,
            50,
            200);
        Assert.That(peerSnapshot!.CallHistory, Is.Empty);
    }

    private static async Task<(NetUserId UserId, NCCharacterId CharacterId)> CreateCharacterAsync(
        ServerDbSqlite db,
        string name)
    {
        var userId = new NetUserId(Guid.NewGuid());
        var preference = await db.InitPrefsAsync(userId, new HumanoidCharacterProfile { Name = name });
        return (userId, new NCCharacterId(preference.Profiles.Single().Id));
    }

    private static Task<NCAgentSnapshotData?> GetSnapshotAsync(
        ServerDbSqlite db,
        NCCharacterId owner,
        NCCharacterId? selectedPeer,
        long? selectedGroup,
        string query,
        DateTime now)
    {
        return db.GetNCAgentSnapshotAsync(
            owner,
            selectedPeer,
            selectedGroup,
            query,
            now,
            30,
            50,
            100,
            50,
            30,
            100,
            0,
            50,
            200);
    }

    private static ServerDbSqlite GetDb(RobustIntegrationTest.ServerIntegrationInstance server)
    {
        var configuration = server.ResolveDependency<IConfigurationManager>();
        var serialization = server.ResolveDependency<ISerializationManager>();
        var operationsLog = server.ResolveDependency<ILogManager>().GetSawmill("db.ops");
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>();
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        options.UseSqlite(connection);
        return new ServerDbSqlite(() => options.Options, true, configuration, true, operationsLog, serialization);
    }
}
