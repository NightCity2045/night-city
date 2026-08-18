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
[TestOf(typeof(NCRelationshipRegistrySystem))]
[NonParallelizable]
public sealed class NCRelationshipRegistrySystemTest : GameTest
{
    [Test]
    public async Task DirectedEffectsReplayAfterLaterEventsAndRestoreWithBoundedIndex()
    {
        var server = Pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var relationships = server.System<NCRelationshipRegistrySystem>();
        var observer = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));
        var subject = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));
        var secondSubject = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));
        var prototypeErrors = new List<string>();
        Assert.That(relationships.ValidatePrototypes(prototypeErrors), Is.True,
            string.Join(Environment.NewLine, prototypeErrors));

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world clock did not start.");

        var assistedEvent = Guid.NewGuid();
        var assistedRequest = Request(observer, subject, "Assisted", assistedEvent, magnitude: 2);
        var assisted = await relationships.ApplyEffectAsync(assistedRequest);
        Assert.That(assisted.Status, Is.EqualTo(NCRelationshipMutationStatus.Applied));

        var threatened = await relationships.ApplyEffectAsync(Request(
            observer,
            subject,
            "Threatened",
            Guid.NewGuid()));
        Assert.That(threatened.Status, Is.EqualTo(NCRelationshipMutationStatus.Applied));

        // Replay the first event after a later event changed LastWorldEventId. The application marker, rather than a
        // bounded recent-ID list, must still make it an exact duplicate.
        var replayedAssistance = await relationships.ApplyEffectAsync(assistedRequest);
        Assert.That(replayedAssistance.Status, Is.EqualTo(NCRelationshipMutationStatus.Duplicate));
        AssertRelationship(replayedAssistance.Relationship, trust: 70, fear: 120, respect: 70, affection: 50,
            hostility: 80, debt: 100, obligation: 60, interactions: 2);
        Assert.That(await relationships.GetAsync(subject, observer, true), Is.Null,
            "A directed relationship must not manufacture its reverse direction.");

        var concurrent = await Task.WhenAll(
            relationships.ApplyEffectAsync(Request(observer, subject, "KeptPromise", Guid.NewGuid())),
            relationships.ApplyEffectAsync(Request(observer, secondSubject, "Assisted", Guid.NewGuid())));
        Assert.That(concurrent.All(result => result.Status == NCRelationshipMutationStatus.Applied), Is.True);

        var current = await relationships.GetAsync(observer, subject, true);
        AssertRelationship(current, trust: 170, fear: 120, respect: 130, affection: 50,
            hostility: 80, debt: 100, obligation: 35, interactions: 3);
        var page = await relationships.GetForObserverAsync(observer, 10, true);
        Assert.Multiple(() =>
        {
            Assert.That(page.Count, Is.EqualTo(2));
            Assert.That(page.Select(value => value.Snapshot.Subject),
                Is.EquivalentTo(new[] { subject, secondSubject }));
            Assert.That(page.Select(value => value.Snapshot.RelationshipId), Is.Unique);
        });

        var invalid = await relationships.ApplyEffectAsync(Request(observer, observer, "Assisted", Guid.NewGuid()));
        Assert.That(invalid.Status, Is.EqualTo(NCRelationshipMutationStatus.Rejected));

        var relationshipId = current!.Snapshot.RelationshipId;
        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await WaitUntil(server, () => !clock.IsReady, "The first social epoch did not close.");
        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PostRound, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The second social epoch did not start.");

        Assert.That(relationships.Unload(relationshipId), Is.True);
        Assert.That(relationships.UnloadObserverIndex(observer), Is.True);
        var restored = await relationships.GetAsync(observer, subject, true);
        AssertRelationship(restored, trust: 170, fear: 120, respect: 130, affection: 50,
            hostility: 80, debt: 100, obligation: 35, interactions: 3);
        Assert.That((await relationships.GetForObserverAsync(observer, 10, true)).Count, Is.EqualTo(2));

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
    }

    private static NCApplyRelationshipEffectRequest Request(
        NCPartyRef observer,
        NCPartyRef subject,
        ProtoId<NCRelationshipEffectPrototype> effect,
        Guid worldEventId,
        int magnitude = 1)
    {
        return new NCApplyRelationshipEffectRequest(
            Guid.NewGuid(),
            observer,
            subject,
            effect,
            worldEventId,
            magnitude,
            observer,
            Guid.NewGuid(),
            AuditSource: "relationship-integration-test");
    }

    private static void AssertRelationship(
        NCRelationshipRecord? record,
        int trust,
        int fear,
        int respect,
        int affection,
        int hostility,
        long debt,
        int obligation,
        int interactions)
    {
        Assert.That(record, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(record!.Snapshot.Trust, Is.EqualTo(trust));
            Assert.That(record.Snapshot.Fear, Is.EqualTo(fear));
            Assert.That(record.Snapshot.Respect, Is.EqualTo(respect));
            Assert.That(record.Snapshot.Affection, Is.EqualTo(affection));
            Assert.That(record.Snapshot.Hostility, Is.EqualTo(hostility));
            Assert.That(record.Snapshot.Debt, Is.EqualTo(debt));
            Assert.That(record.Snapshot.Obligation, Is.EqualTo(obligation));
            Assert.That(record.Snapshot.InteractionCount, Is.EqualTo(interactions));
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
