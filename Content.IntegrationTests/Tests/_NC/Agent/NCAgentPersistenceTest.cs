// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Shared._NC.Agent;
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
        });
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

        var snapshot = await db.GetNCAgentSnapshotAsync(first, second, "chrome", now, 30, 100, 50, 200);
        Assert.That(snapshot, Is.Not.Null);
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
            Assert.That(snapshot.Messages.Single().Body, Is.EqualTo("Meet at the Afterlife."));
            Assert.That(snapshot.Messages.Single().SenderAlias, Is.EqualTo("Neon Raven"));
            Assert.That(snapshot.Messages.Single().RecipientAlias, Is.EqualTo("Chrome Ghost"));
            Assert.That(snapshot.Posts.Single().ReactedByViewer, Is.True);
            Assert.That(snapshot.Posts.Single().ReactionCount, Is.EqualTo(1));
            Assert.That(snapshot.Posts.Single().AuthorAlias, Is.EqualTo("Chrome Ghost"));
            Assert.That(snapshot.Posts.Single().Comments.Single().Body, Is.EqualTo("Send details."));
            Assert.That(snapshot.Posts.Single().Comments.Single().AuthorAlias, Is.EqualTo("Neon Raven"));
        });

        Assert.That(await db.PurgeExpiredNCAgentMessagesAsync(now.AddDays(31)), Is.EqualTo(1));
        var expiredSnapshot = await db.GetNCAgentSnapshotAsync(first, second, string.Empty, now.AddDays(31), 30, 100, 50, 200);
        Assert.That(expiredSnapshot!.Messages, Is.Empty);
    }

    [Test]
    public async Task CharacterDeletionCascadesAgentData()
    {
        var db = GetDb(Pair.Server);
        var (userId, characterId) = await CreateCharacterAsync(db, "Disposable Edge");
        Assert.That(await db.GetOrCreateNCAgentProfileAsync(characterId, "NC"), Is.Not.Null);

        // Agent records are character-owned, so the existing profile cascade is the deletion boundary.
        await db.SaveCharacterSlotAsync(userId, null, 0);

        var snapshot = await db.GetNCAgentSnapshotAsync(
            characterId,
            null,
            string.Empty,
            DateTime.UtcNow,
            30,
            100,
            50,
            200);
        Assert.That(snapshot, Is.Null);
    }

    private static async Task<(NetUserId UserId, NCCharacterId CharacterId)> CreateCharacterAsync(
        ServerDbSqlite db,
        string name)
    {
        var userId = new NetUserId(Guid.NewGuid());
        var preference = await db.InitPrefsAsync(userId, new HumanoidCharacterProfile { Name = name });
        return (userId, new NCCharacterId(preference.Profiles.Single().Id));
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
