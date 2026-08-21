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
[TestOf(typeof(NCMemoryRegistrySystem))]
[NonParallelizable]
public sealed class NCMemoryRegistrySystemTest : GameTest
{
    [Test]
    public async Task StructuredMemoryConsolidatesReplaysFiltersAndRestores()
    {
        Checkpoint("test body started");
        var server = Pair.Server;
        Checkpoint("integration server acquired");
        var database = server.ResolveDependency<IServerDbManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var memory = server.System<NCMemoryRegistrySystem>();
        var owner = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));
        var participant = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));
        var prototypeErrors = new List<string>();
        Assert.That(memory.ValidatePrototypes(prototypeErrors), Is.True,
            string.Join(Environment.NewLine, prototypeErrors));
        Checkpoint("memory prototypes validated");

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world clock did not start.");
        Checkpoint("active-world clock started");

        var firstEvent = Guid.NewGuid();
        var firstRequest = Remember(owner, participant, "MemoryRoutineInteraction", firstEvent);
        Assert.That((await memory.RememberAsync(firstRequest)).Status, Is.EqualTo(NCMemoryMutationStatus.Applied));
        Assert.That((await memory.RememberAsync(Remember(
            owner,
            participant,
            "MemoryRoutineInteraction",
            Guid.NewGuid()))).Status, Is.EqualTo(NCMemoryMutationStatus.Applied));
        Assert.That((await memory.RememberAsync(Remember(
            owner,
            participant,
            "MemoryBetrayal",
            Guid.NewGuid()))).Status, Is.EqualTo(NCMemoryMutationStatus.Applied));
        Checkpoint("three memories committed");

        var personalEpisodes = await memory.GetRelevantAsync(owner, new NCMemoryQuery(
            [participant], ["routine-interaction"], [], NCMemoryPrivacy.Personal, 10), true);
        Assert.Multiple(() =>
        {
            Assert.That(personalEpisodes.Count, Is.EqualTo(2));
            Assert.That(personalEpisodes.All(item => item.Episode != null && item.Summary == null), Is.True);
        });
        Assert.That(await memory.GetRelevantAsync(owner, new NCMemoryQuery(
            [], [], [], NCMemoryPrivacy.Public, 10), true), Is.Empty,
            "Personal and secret memory must not cross a public context boundary.");
        Checkpoint("privacy queries verified");

        var consolidated = await memory.ConsolidateAsync(new NCConsolidateMemoryRequest(
            Guid.NewGuid(), owner, Guid.NewGuid(), owner, Guid.NewGuid(),
            AuditSource: "memory-integration-test"));
        Assert.That(consolidated.Status, Is.EqualTo(NCMemoryMutationStatus.Applied));
        Checkpoint("memory book consolidated");
        Assert.That(consolidated.Book, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(consolidated.Book!.Snapshot.Episodes.Count, Is.EqualTo(1));
            Assert.That(consolidated.Book.Snapshot.Episodes.Single().Critical, Is.True,
                "A critical permanent betrayal must survive deterministic consolidation.");
            Assert.That(consolidated.Book.Snapshot.Summaries.Count, Is.EqualTo(1));
            Assert.That(consolidated.Book.Snapshot.Summaries.Single().OccurrenceCount, Is.EqualTo(2));
            Assert.That(consolidated.Book.Snapshot.TotalRemembered, Is.EqualTo(3));
            Assert.That(consolidated.Book.Snapshot.TotalConsolidated, Is.EqualTo(2));
            Assert.That(consolidated.Book.Snapshot.TotalForgotten, Is.Zero);
        });

        // The immutable event application remains an exact duplicate even after its episode became a summary.
        var replay = await memory.RememberAsync(firstRequest);
        Assert.That(replay.Status, Is.EqualTo(NCMemoryMutationStatus.Duplicate));
        Assert.That(replay.Book!.Snapshot.Summaries.Count, Is.EqualTo(1));
        var collision = firstRequest with
        {
            CommandId = Guid.NewGuid(),
            EventType = new ProtoId<NCMemoryEventPrototype>("MemoryAssistanceReceived"),
        };
        Assert.That((await memory.RememberAsync(collision)).Status, Is.EqualTo(NCMemoryMutationStatus.Rejected),
            "A reused world-event identity may not be rebound to different immutable semantics.");
        Checkpoint("event replay and collision verified");

        var summary = replay.Book.Snapshot.Summaries.Single();
        var recalled = await memory.RecordRecallAsync(new NCRecordMemoryRecallRequest(
            Guid.NewGuid(), owner, Guid.NewGuid(), [], [summary.SummaryId], owner, Guid.NewGuid(),
            AuditSource: "memory-integration-test"));
        Assert.That(recalled.Status, Is.EqualTo(NCMemoryMutationStatus.Applied));
        Checkpoint("summary recall committed");
        Assert.Multiple(() =>
        {
            var recalledSummary = recalled.Book!.Snapshot.Summaries.Single();
            Assert.That(recalledSummary.RecallCount, Is.EqualTo(1));
            Assert.That(recalledSummary.LastRecalledAt, Is.Not.Null);
            Assert.That(recalledSummary.SourceWorldEventIds, Does.Contain(firstEvent));
        });

        var personalSummary = await memory.GetRelevantAsync(owner, new NCMemoryQuery(
            [participant], ["routine-interaction"], [], NCMemoryPrivacy.Personal, 10), true);
        Assert.Multiple(() =>
        {
            Assert.That(personalSummary.Count, Is.EqualTo(1));
            Assert.That(personalSummary.Single().Episode, Is.Null);
            Assert.That(personalSummary.Single().Summary!.SourceMemoryIds.Count, Is.EqualTo(2));
        });

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await WaitUntil(server, () => !clock.IsReady, "The first memory epoch did not close.");
        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PostRound, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The second memory epoch did not start.");
        Checkpoint("memory epoch restarted");

        Assert.That(memory.Unload(owner), Is.True);
        var restored = await memory.GetAsync(owner, true);
        Assert.That(restored, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(restored!.Snapshot.Episodes.Single().Critical, Is.True);
            Assert.That(restored.Snapshot.Summaries.Single().RecallCount, Is.EqualTo(1));
            Assert.That(restored.Snapshot.TotalRemembered, Is.EqualTo(3));
        });
        Checkpoint("memory book restored from persistence");

        // A group larger than one summary leaves its undersized non-expired tail intact instead of silently forgetting
        // it at the batch boundary.
        for (var i = 0; i < 33; i++)
        {
            Assert.That((await memory.RememberAsync(Remember(
                owner,
                participant,
                "MemoryRoutineInteraction",
                Guid.NewGuid()))).Status, Is.EqualTo(NCMemoryMutationStatus.Applied));
        }
        var boundedConsolidation = await memory.ConsolidateAsync(new NCConsolidateMemoryRequest(
            Guid.NewGuid(), owner, Guid.NewGuid(), owner, Guid.NewGuid(),
            AuditSource: "memory-integration-test"));
        Assert.That(boundedConsolidation.Status, Is.EqualTo(NCMemoryMutationStatus.Applied));
        Assert.Multiple(() =>
        {
            Assert.That(boundedConsolidation.Book!.Snapshot.Episodes.Count, Is.EqualTo(2));
            Assert.That(boundedConsolidation.Book.Snapshot.Episodes.Count(episode => !episode.Critical), Is.EqualTo(1));
            Assert.That(boundedConsolidation.Book.Snapshot.Summaries.Count, Is.EqualTo(2));
            Assert.That(boundedConsolidation.Book.Snapshot.TotalRemembered, Is.EqualTo(36));
            Assert.That(boundedConsolidation.Book.Snapshot.TotalConsolidated, Is.EqualTo(34));
            Assert.That(boundedConsolidation.Book.Snapshot.TotalForgotten, Is.Zero);
        });
        Checkpoint("undersized consolidation tail retained");

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
    }

    private static void Checkpoint(string message) =>
        TestContext.Progress.WriteLine($"NCMemoryRegistrySystemTest: {message}");

    private static NCRememberWorldEventRequest Remember(
        NCPartyRef owner,
        NCPartyRef participant,
        ProtoId<NCMemoryEventPrototype> eventType,
        Guid worldEventId)
    {
        return new NCRememberWorldEventRequest(
            Guid.NewGuid(),
            owner,
            eventType,
            worldEventId,
            [],
            [participant],
            new NCMemorySource(NCMemorySourceKind.DirectPerception, default),
            [],
            null,
            null,
            null,
            owner,
            Guid.NewGuid(),
            AuditSource: "memory-integration-test");
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
