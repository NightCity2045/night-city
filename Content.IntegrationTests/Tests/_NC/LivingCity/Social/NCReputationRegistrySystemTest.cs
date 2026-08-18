// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Social;

[TestFixture]
[TestOf(typeof(NCReputationRegistrySystem))]
[NonParallelizable]
public sealed class NCReputationRegistrySystemTest : GameTest
{
    [Test]
    public async Task ChannelsReplayOldEventsAndRestoreWithoutRelationshipConflation()
    {
        var server = Pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var directory = server.System<NCPartyDirectorySystem>();
        var reputation = server.System<NCReputationRegistrySystem>();
        var subject = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));
        var personalAudience = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));
        var faction = NCPartyRef.ForOrganization(NCOrganizationId.New());
        var prototypeErrors = new List<string>();
        Assert.That(reputation.ValidatePrototypes(prototypeErrors), Is.True,
            string.Join(Environment.NewLine, prototypeErrors));

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world clock did not start.");
        var factionRegistration = await directory.RegisterAsync(new NCPartyDirectoryRegisterRequest(
            Guid.NewGuid(), faction, "Reputation Test Faction", Guid.NewGuid(), AuditSource: "reputation-test"));
        Assert.That(factionRegistration.Status, Is.EqualTo(NCPartyDirectoryMutationStatus.Applied));

        var publicEvent = Guid.NewGuid();
        var originalPublic = Request(
            subject,
            new NCReputationAudienceRequest(NCReputationChannel.Public, default, "NightCityPublic"),
            "ReputationReliableAction",
            publicEvent,
            magnitude: 2);
        Assert.That((await reputation.ApplyEffectAsync(originalPublic)).Status,
            Is.EqualTo(NCReputationMutationStatus.Applied));
        Assert.That((await reputation.ApplyEffectAsync(Request(
            subject,
            new NCReputationAudienceRequest(NCReputationChannel.Public, default, "NightCityPublic"),
            "ReputationPublicViolence",
            Guid.NewGuid()))).Status, Is.EqualTo(NCReputationMutationStatus.Applied));
        Assert.That((await reputation.ApplyEffectAsync(Request(
            subject,
            new NCReputationAudienceRequest(NCReputationChannel.Personal, personalAudience),
            "ReputationReliableAction",
            Guid.NewGuid()))).Status, Is.EqualTo(NCReputationMutationStatus.Applied));
        Assert.That((await reputation.ApplyEffectAsync(Request(
            subject,
            new NCReputationAudienceRequest(NCReputationChannel.Faction, faction),
            "ReputationBetrayal",
            Guid.NewGuid()))).Status, Is.EqualTo(NCReputationMutationStatus.Applied));
        Assert.That((await reputation.ApplyEffectAsync(Request(
            subject,
            new NCReputationAudienceRequest(NCReputationChannel.Professional, default,
                "GeneralProfessional"),
            "ReputationReliableAction",
            Guid.NewGuid()))).Status, Is.EqualTo(NCReputationMutationStatus.Applied));

        // A marker per audience/event keeps an old event exactly-once after a newer event changed that entry.
        var replay = await reputation.ApplyEffectAsync(originalPublic);
        Assert.That(replay.Status, Is.EqualTo(NCReputationMutationStatus.Duplicate));
        var publicEntry = replay.Reputation!.Snapshot.Entries.Single(entry =>
            entry.Audience.Channel == NCReputationChannel.Public);
        Assert.Multiple(() =>
        {
            Assert.That(publicEntry.Score, Is.EqualTo(70));
            Assert.That(publicEntry.Visibility, Is.EqualTo(170));
            Assert.That(publicEntry.Evidence, Is.EqualTo(3));
            Assert.That(publicEntry.EventCount, Is.EqualTo(2));
            Assert.That(replay.Reputation.Snapshot.Entries.Select(entry => entry.Audience.Channel),
                Is.EquivalentTo(new[]
                {
                    NCReputationChannel.Personal,
                    NCReputationChannel.Faction,
                    NCReputationChannel.Professional,
                    NCReputationChannel.Public,
                }));
        });
        Assert.That((await reputation.GetEntriesAsync(subject, NCReputationChannel.Public, 10, true)).Single(),
            Is.EqualTo(publicEntry));

        var invalidFaction = await reputation.ApplyEffectAsync(Request(
            subject,
            new NCReputationAudienceRequest(NCReputationChannel.Faction, personalAudience),
            "ReputationReliableAction",
            Guid.NewGuid()));
        Assert.That(invalidFaction.Status, Is.EqualTo(NCReputationMutationStatus.Rejected));

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await WaitUntil(server, () => !clock.IsReady, "The first reputation epoch did not close.");
        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PostRound, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The second reputation epoch did not start.");

        Assert.That(reputation.Unload(subject), Is.True);
        var restored = await reputation.GetAsync(subject, true);
        Assert.That(restored, Is.Not.Null);
        Assert.That(restored!.Snapshot.Entries.Count, Is.EqualTo(4));
        Assert.That(restored.Snapshot.Entries.Single(entry =>
            entry.Audience.Channel == NCReputationChannel.Public), Is.EqualTo(publicEntry));

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
    }

    private static NCApplyReputationEffectRequest Request(
        NCPartyRef subject,
        NCReputationAudienceRequest audience,
        ProtoId<NCReputationEffectPrototype> effect,
        Guid worldEventId,
        int magnitude = 1)
    {
        return new NCApplyReputationEffectRequest(
            Guid.NewGuid(),
            subject,
            audience,
            effect,
            worldEventId,
            magnitude,
            subject,
            Guid.NewGuid(),
            AuditSource: "reputation-integration-test");
    }

    private static async Task<NCCharacterId> CreateCharacterAsync(IServerDbManager database)
    {
        var preference = await database.InitPrefsAsync(
            new NetUserId(Guid.NewGuid()),
            new HumanoidCharacterProfile(),
            CancellationToken.None);
        return new NCCharacterId(preference.Profiles.Single().Id);
    }

    private static async Task WaitUntil(
        RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate,
        string failure,
        int attempts = 180)
    {
        for (var i = 0; i < attempts; i++)
        {
            await server.WaitRunTicks(1);
            var complete = false;
            await server.WaitPost(() => complete = predicate());
            if (complete)
                return;
        }

        Assert.Fail(failure);
    }
}
