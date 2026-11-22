// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Social;

[TestFixture]
[TestOf(typeof(NCConversationSessionSystem))]
[NonParallelizable]
public sealed class NCConversationSessionSystemTest : GameTest
{
    [Test]
    public async Task RecordsConversationTurnsAndClearsSessionsAtRoundEnd()
    {
        var server = Pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var conversations = server.System<NCConversationSessionSystem>();
        var source = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));
        var target = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));
        var errors = new List<string>();
        Assert.Multiple(() =>
        {
            Assert.That(conversations.ValidatePrototypes(errors), Is.True, string.Join(Environment.NewLine, errors));
        });

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world clock did not start.");

        var conversationId = NCConversationSessionId.New();
        Assert.That(conversations.TryStart(new NCConversationStartRequest(
            conversationId,
            [source, target],
            NCConversationChannel.InPerson,
            new NCInformationSubject(NCInformationSubjectKind.StableKey, "test-room"),
            ["violent-incident"],
            [],
            NCMemoryPrivacy.Personal,
            NCLanguage.Russian,
            NCLocale.RuRu), out _, out var conversationError), Is.True, conversationError);
        Assert.That(conversations.TryRecordTurn(new NCConversationTurnRequest(
            conversationId,
            source,
            NCSpeechActKind.Statement,
            "report-incident",
            "test-chat:1",
            null), out var activeConversation, out conversationError), Is.True, conversationError);
        Assert.Multiple(() =>
        {
            Assert.That(activeConversation.Status, Is.EqualTo(NCConversationStatus.Active));
            Assert.That(activeConversation.Turns.Single().ExternalReference, Is.EqualTo("test-chat:1"));
        });

        Assert.That(conversations.TryTransition(
            conversationId,
            NCConversationStatus.Ended,
            "completed",
            out _), Is.True);

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await WaitUntil(server, () => !clock.IsReady, "The active-world clock did not stop.");
        Assert.Multiple(() =>
        {
            Assert.That(conversations.ActiveSessionCount, Is.Zero);
            Assert.That(conversations.RecentSessionCount, Is.Zero);
        });
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
        for (var attempt = 0; attempt < attempts; attempt++)
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
