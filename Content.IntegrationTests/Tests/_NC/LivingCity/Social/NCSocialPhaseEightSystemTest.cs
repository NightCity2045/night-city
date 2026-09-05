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
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Social;

[TestFixture]
[TestOf(typeof(NCRumorRegistrySystem))]
[TestOf(typeof(NCSocialProfileRegistrySystem))]
[TestOf(typeof(NCSocialObligationRegistrySystem))]
[TestOf(typeof(NCConversationSessionSystem))]
[NonParallelizable]
public sealed class NCSocialPhaseEightSystemTest : GameTest
{
    [Test]
    public async Task RumorConversationPrivacyAndObligationConsequencesAreBoundedAndReplayable()
    {
        Checkpoint("test-started");
        var server = Pair.Server;
        Checkpoint("server-acquired");
        var database = server.ResolveDependency<IServerDbManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var profiles = server.System<NCSocialProfileRegistrySystem>();
        var rumors = server.System<NCRumorRegistrySystem>();
        var obligations = server.System<NCSocialObligationRegistrySystem>();
        var conversations = server.System<NCConversationSessionSystem>();
        var redaction = server.System<NCSocialContextRedactionSystem>();
        var relationships = server.System<NCRelationshipRegistrySystem>();
        var memories = server.System<NCMemoryRegistrySystem>();
        var source = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));
        var target = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));
        var errors = new List<string>();
        Assert.Multiple(() =>
        {
            Assert.That(profiles.ValidatePrototypes(errors), Is.True, string.Join(Environment.NewLine, errors));
            Assert.That(rumors.ValidatePrototypes(errors), Is.True, string.Join(Environment.NewLine, errors));
            Assert.That(obligations.ValidatePrototypes(errors), Is.True, string.Join(Environment.NewLine, errors));
            Assert.That(conversations.ValidatePrototypes(errors), Is.True, string.Join(Environment.NewLine, errors));
            Assert.That(redaction.ValidatePrototypes(errors), Is.True, string.Join(Environment.NewLine, errors));
        });
        Checkpoint("prototypes-validated");

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world clock did not start.");
        Checkpoint("round-started");

        var observedEvent = Guid.NewGuid();
        var learn = new NCLearnInformationRequest(
            Guid.NewGuid(),
            source,
            new ProtoId<NCRumorTopicPrototype>("NCRumorViolentIncident"),
            new NCInformationSubject(NCInformationSubjectKind.Party, target.ToString()),
            [observedEvent],
            NCRumorSourceKind.DirectWitness,
            source,
            null,
            null,
            source,
            Guid.NewGuid(),
            AuditSource: "social-phase-eight-test");
        var learned = await rumors.LearnAsync(learn);
        Assert.That(learned.Status, Is.EqualTo(NCRumorMutationStatus.Applied));
        Checkpoint("information-learned");
        Assert.That((await rumors.LearnAsync(learn)).Status, Is.EqualTo(NCRumorMutationStatus.Duplicate));
        Assert.That(await rumors.GetKnownAsync(source, NCMemoryPrivacy.Public, 10), Is.Empty);

        var propagate = new NCPropagateInformationRequest(
            Guid.NewGuid(),
            source,
            target,
            learned.Knowledge!.KnowledgeId,
            NCRumorSourceKind.Conversation,
            Guid.NewGuid(),
            source,
            Guid.NewGuid(),
            AuditSource: "social-phase-eight-test");
        var propagated = await rumors.PropagateAsync(propagate);
        Assert.Multiple(() =>
        {
            Assert.That(propagated.Status, Is.EqualTo(NCRumorMutationStatus.Applied));
            Assert.That(propagated.Knowledge?.InformationId, Is.EqualTo(learned.Knowledge.InformationId));
            Assert.That(propagated.Knowledge?.Confidence, Is.EqualTo(722));
            Assert.That(propagated.Knowledge?.Distortion, Is.EqualTo(100));
            Assert.That(propagated.Knowledge?.HopCount, Is.EqualTo(1));
            Assert.That(propagated.Knowledge?.ImmediateSource, Is.EqualTo(source));
        });
        Assert.That((await rumors.PropagateAsync(propagate)).Status, Is.EqualTo(NCRumorMutationStatus.Duplicate));
        Checkpoint("information-propagated");

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

        var internalContext = await redaction.BuildAsync(source, NCSocialContextPurpose.InternalReasoning);
        var conversationContext = await redaction.BuildAsync(
            source,
            NCSocialContextPurpose.ConversationRendering,
            conversationId);
        Assert.Multiple(() =>
        {
            Assert.That(internalContext, Is.Not.Null);
            Assert.That(internalContext!.Facts.Any(value => value.Domain == "rumor"), Is.True);
            Assert.That(conversationContext, Is.Not.Null);
            Assert.That(conversationContext!.Facts.Any(value => value.Domain == "rumor"), Is.False);
            Assert.That(conversationContext.RedactedRumorCount, Is.EqualTo(1));
        });
        Assert.That(conversations.TryTransition(
            conversationId,
            NCConversationStatus.Ended,
            "completed",
            out _), Is.True);
        Checkpoint("conversation-redaction-verified");

        var obligationId = NCSocialObligationId.New();
        var createObligation = new NCCreateSocialObligationRequest(
            Guid.NewGuid(),
            obligationId,
            NCSocialObligationKind.Promise,
            target,
            source,
            new NCSocialObligationTerms(
                "deliver-medical-supplies",
                3,
                "crate",
                new NCInformationSubject(NCInformationSubjectKind.Party, source.ToString())),
            NCMemoryPrivacy.Restricted,
            Guid.NewGuid(),
            null,
            source,
            Guid.NewGuid(),
            AuditSource: "social-phase-eight-test");
        var created = await obligations.CreateAsync(createObligation);
        Assert.That(created.Status, Is.EqualTo(NCSocialObligationMutationStatus.Applied));
        Assert.That((await obligations.CreateAsync(createObligation)).Status,
            Is.EqualTo(NCSocialObligationMutationStatus.Duplicate));
        Checkpoint("obligation-created");
        await WaitForRelationship(server, relationships, source, target, 1);
        Checkpoint("creation-consequence-delivered");

        var resolutionEvent = Guid.NewGuid();
        var collidingResolve = await obligations.ResolveAsync(new NCResolveSocialObligationRequest(
            learn.CommandId,
            obligationId,
            NCSocialObligationStatus.Breached,
            resolutionEvent,
            source,
            Guid.NewGuid(),
            AuditSource: "social-phase-eight-test"));
        var afterCollision = await obligations.GetAsync(obligationId);
        Assert.Multiple(() =>
        {
            Assert.That(collidingResolve.Status, Is.EqualTo(NCSocialObligationMutationStatus.Rejected));
            Assert.That(afterCollision!.Snapshot.Status, Is.EqualTo(NCSocialObligationStatus.Active));
        });
        var resolve = new NCResolveSocialObligationRequest(
            Guid.NewGuid(),
            obligationId,
            NCSocialObligationStatus.Breached,
            resolutionEvent,
            source,
            Guid.NewGuid(),
            AuditSource: "social-phase-eight-test");
        var breached = await obligations.ResolveAsync(resolve);
        Assert.That(breached.Status, Is.EqualTo(NCSocialObligationMutationStatus.Applied));
        Assert.That((await obligations.ResolveAsync(resolve)).Status,
            Is.EqualTo(NCSocialObligationMutationStatus.Duplicate));
        Checkpoint("obligation-breached");
        var relationship = await WaitForRelationship(server, relationships, source, target, 2);
        var memory = await WaitForMemoryCount(server, memories, source, 2);
        Checkpoint("breach-consequence-delivered");
        var indexedObligations = await obligations.GetForPartyAsync(source, 10, true);
        Assert.Multiple(() =>
        {
            Assert.That(relationship.Snapshot.Trust, Is.EqualTo(-140));
            Assert.That(relationship.Snapshot.Hostility, Is.EqualTo(70));
            Assert.That(memory.Snapshot.Episodes.Select(value => value.EventTypeKey),
                Is.EquivalentTo(new[] { "obligation-created", "betrayal" }));
            Assert.That(indexedObligations.Single().Snapshot.Status,
                Is.EqualTo(NCSocialObligationStatus.Breached));
        });

        Assert.That(rumors.Unload(target), Is.True);
        var restoredRumor = await rumors.GetAsync(target, true);
        Assert.That(restoredRumor?.Snapshot.Knowledge.Single().ImmediateSource, Is.EqualTo(source));
        Checkpoint("persistence-restored");

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await WaitUntil(server, () => !clock.IsReady, "The active-world clock did not stop.");
        Assert.Multiple(() =>
        {
            Assert.That(conversations.ActiveSessionCount, Is.Zero);
            Assert.That(conversations.RecentSessionCount, Is.Zero);
        });
        Checkpoint("test-completed");
    }

    private static void Checkpoint(string value) =>
        TestContext.Progress.WriteLine($"NCSocialPhaseEightSystemTest: {value}");

    private static async Task<NCRelationshipRecord> WaitForRelationship(
        RobustIntegrationTest.ServerIntegrationInstance server,
        NCRelationshipRegistrySystem relationships,
        NCPartyRef observer,
        NCPartyRef subject,
        int interactions)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            await server.WaitRunTicks(1);
            var value = await relationships.GetAsync(observer, subject, true);
            if (value?.Snapshot.InteractionCount >= interactions)
                return value;
        }
        Assert.Fail("The obligation relationship consequence was not delivered.");
        return default!;
    }

    private static async Task<NCMemoryBookRecord> WaitForMemoryCount(
        RobustIntegrationTest.ServerIntegrationInstance server,
        NCMemoryRegistrySystem memories,
        NCPartyRef owner,
        int count)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            await server.WaitRunTicks(1);
            var value = await memories.GetAsync(owner, true);
            if (value != null && value.Snapshot.Episodes.Count >= count)
                return value;
        }
        Assert.Fail("The obligation memory consequence was not delivered.");
        return default!;
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
