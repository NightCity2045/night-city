// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Neural;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using NUnit.Framework;
using Robust.Shared.Prototypes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Content.Tests.Server._NC.LivingCity.Neural;

[TestFixture]
public sealed class NCNeuralContextBuilderSystemTest
{
    [Test]
    public void OrdinaryStrategicDecisionUsesFullPerRequestOutputBudget()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NCNeuralContextBuilderSystem.ResolveMaximumOutputTokens(
                false, NCNeuralRequestKind.StrategicDecision, 2048, 384), Is.EqualTo(2048));
            Assert.That(NCNeuralContextBuilderSystem.ResolveMaximumOutputTokens(
                false, NCNeuralRequestKind.DialogueContinuation, 2048, 384), Is.EqualTo(384));
            Assert.That(NCNeuralContextBuilderSystem.ResolveMaximumOutputTokens(
                true, NCNeuralRequestKind.DialogueContinuation, 2048, 384), Is.EqualTo(2048));
        });
    }

    [Test]
    public void FactPacketKeepsThreatFailureObligationAndRelationshipBeforeRecentNoise()
    {
        NCNeuralFactCandidate[] candidates =
        [
            Candidate("rumor", "recent-noise", "someone", 1f, 500),
            Candidate("relationship", "distrust", "known-person-1", 0.8f, 100),
            Candidate("obligation", "repay", "known-person-2", 1f, 100),
            Candidate("command-result", "failed", "blocked", 1f, 100),
            Candidate("perception", "assault", "threat-1", 1f, 1),
        ];

        var packet = NCNeuralContextBuilderSystem.BuildFactPacket(
            candidates,
            4,
            new HashSet<string>(StringComparer.Ordinal) { "threat-1" });

        Assert.That(packet.Select(value => value.Domain), Is.EqualTo(new[]
        {
            "perception",
            "command-result",
            "obligation",
            "relationship",
        }));
        Assert.That(packet.Select(value => value.Handle), Is.EqualTo(new[]
        {
            "fact-001",
            "fact-002",
            "fact-003",
            "fact-004",
        }));
    }

    [Test]
    public void ObservableDescriptionNeverInventsAnEntityName()
    {
        Assert.That(NCNeuralContextBuilderSystem.ObservableDescription(NCCityLocalObservationKind.Neutral),
            Is.EqualTo("observable person"));
        Assert.That(NCNeuralContextBuilderSystem.ObservableDescription(
                NCCityLocalObservationKind.Neutral, "Raymond Feuer"),
            Is.EqualTo("observable person"), "A physical name is not recognition evidence.");
        Assert.That(NCNeuralContextBuilderSystem.ObservableDescription(NCCityLocalObservationKind.MedicalDevice),
            Is.EqualTo("observable medical device"));
        Assert.That(NCNeuralContextBuilderSystem.ObservableDescription(
                NCCityLocalObservationKind.Item, "gaechniy klyuch"),
            Is.EqualTo("gaechniy klyuch"), "Visible objects should expose their ordinary in-world name.");
    }

    [Test]
    public void PersonNameRequiresDurableServerOwnedSocialLink()
    {
        var anonymous = Subject("person-1", "neutral");
        NCRedactedSocialFact[] rumorOnly =
        [
            new("rumor", "introduced-as-averill", "person-1", 1f, NCMemoryPrivacy.Personal, 100),
        ];
        NCRedactedSocialFact[] relationship =
        [
            new("relationship", "known-relationship", "person-1", 1f, NCMemoryPrivacy.Personal, 100),
        ];

        var stillAnonymous = NCNeuralContextBuilderSystem.RecognizeKnownPerson(
            anonymous, "Averill Shaw", rumorOnly);
        var recognized = NCNeuralContextBuilderSystem.RecognizeKnownPerson(
            anonymous, "Averill Shaw", relationship);
        var objectSubject = NCNeuralContextBuilderSystem.RecognizeKnownPerson(
            Subject("person-1", "item"), "Averill Shaw", relationship);

        Assert.Multiple(() =>
        {
            Assert.That(stillAnonymous.DisplayName, Is.EqualTo("visible neutral"));
            Assert.That(stillAnonymous.IdentityKnown, Is.False);
            Assert.That(recognized.DisplayName, Is.EqualTo("Averill Shaw"));
            Assert.That(recognized.IdentityKnown, Is.True);
            Assert.That(objectSubject.DisplayName, Is.EqualTo("visible item"));
            Assert.That(objectSubject.IdentityKnown, Is.False);
        });
    }

    [Test]
    public void SenseResultsConsumeEveryKindAndOnlyReferenceCurrentPerception()
    {
        var body = new NCNeuralBodyContext("alive", "fed", "hydrated", "mobile", "none", false);
        NCNeuralPerceivedSubject[] perceived =
        [
            Subject("self", "self"),
            Subject("person-1", "neutral"),
            Subject("item-1", "item"),
            Subject("held-1", "helditem"),
            Subject("equipped-1", "equippeditem"),
        ];
        NCNeuralPerceivedEvent[] events =
        [
            new("weapondisplayed", "person-1", "item-1", 1f, 10, 20),
        ];

        var lookAndSubject = NCNeuralContextBuilderSystem.BuildSenseResults(
            [new("look-around", ""), new("inspect-subject", "person-1")], perceived, events, body);
        var inventoryAndBody = NCNeuralContextBuilderSystem.BuildSenseResults(
            [new("check-inventory", ""), new("check-body", "")], perceived, events, body);
        var objectInspection = NCNeuralContextBuilderSystem.BuildSenseResults(
            [new("inspect-object", "item-1")], perceived, events, body);

        Assert.Multiple(() =>
        {
            Assert.That(lookAndSubject[0].Kind, Is.EqualTo("look-around"));
            Assert.That(lookAndSubject[0].ObservedHandles, Does.Not.Contain("self"));
            Assert.That(lookAndSubject[1].ObservedHandles, Is.EqualTo(new[] { "person-1", "item-1" }));
            Assert.That(inventoryAndBody[0].ObservedHandles,
                Is.EqualTo(new[] { "held-1", "equipped-1" }));
            Assert.That(inventoryAndBody[1].Body, Is.EqualTo(body));
            Assert.That(objectInspection[0].Status, Is.EqualTo("completed"));
            Assert.That(objectInspection[0].ObservedHandles, Is.EqualTo(new[] { "item-1" }));
            Assert.That(lookAndSubject.SelectMany(value => value.ObservedHandles)
                .Concat(inventoryAndBody.SelectMany(value => value.ObservedHandles))
                .Concat(objectInspection.SelectMany(value => value.ObservedHandles))
                .Except(perceived.Select(value => value.Handle)), Is.Empty);
        });
    }

    [Test]
    public void TargetedSenseReportsLostOrWrongKindWithoutInventingDetails()
    {
        NCNeuralPerceivedSubject[] perceived =
        [
            Subject("self", "self"),
            Subject("person-1", "neutral"),
            Subject("item-1", "item"),
        ];

        var results = NCNeuralContextBuilderSystem.BuildSenseResults(
            [new("inspect-subject", "missing-1"), new("inspect-object", "person-1")],
            perceived,
            [],
            null);

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Status, Is.EqualTo("not-visible"));
            Assert.That(results[0].ObservedHandles, Is.Empty);
            Assert.That(results[1].Status, Is.EqualTo("wrong-target-kind"));
            Assert.That(results[1].ObservedHandles, Is.EqualTo(new[] { "person-1" }));
            Assert.That(results.SelectMany(value => value.ObservedHandles),
                Has.None.EqualTo("missing-1"));
        });
    }

    [Test]
    public void ActiveQueriesReturnOnlyRequestScopedFactsLocationsOpportunitiesAndRoutes()
    {
        NCNeuralPerceivedSubject[] perceived =
        [
            Subject("self", "self"),
            Subject("person-1", "neutral"),
            Subject("item-1", "item"),
        ];
        NCNeuralFact[] facts =
        [
            new("memory", "helped-before", "person-1", 1f, "Personal", "fact-1"),
            new("memory", "unrelated", "person-2", 1f, "Personal", "fact-2"),
        ];
        NCNeuralKnownLocation[] locations =
        [
            new("station-destination:work", "Cargo workplace", "assigned-workplace"),
        ];
        NCNeuralWorldOpportunity[] opportunities =
        [
            new("item-1", ["object.tool"], true, string.Empty),
            new("station-destination:work", ["task.report-to-assigned-workplace"], true, string.Empty),
        ];
        var routes = new HashSet<string>(StringComparer.Ordinal) { "station-destination:work" };

        var discovery = NCNeuralContextBuilderSystem.BuildSenseResults(
            [new("query-nearby-affordances", ""), new("query-known-locations", "")],
            perceived, [], null, facts, locations, opportunities, routes);
        var retrieval = NCNeuralContextBuilderSystem.BuildSenseResults(
            [new("recall-memories", "person-1"), new("examine-route", "station-destination:work")],
            perceived, [], null, facts, locations, opportunities, routes);
        var lostRoute = NCNeuralContextBuilderSystem.BuildSenseResults(
            [new("examine-route", "station-destination:work")],
            perceived, [], null, facts, locations, opportunities, new HashSet<string>(StringComparer.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(discovery[0].OpportunityTargetHandles, Is.EqualTo(new[] { "item-1" }));
            Assert.That(discovery[1].LocationHandles, Is.EqualTo(new[] { "station-destination:work" }));
            Assert.That(retrieval[0].FactHandles, Is.EqualTo(new[] { "fact-1" }));
            Assert.That(retrieval[1].Status, Is.EqualTo("completed"));
            Assert.That(retrieval[1].LocationHandles, Is.EqualTo(new[] { "station-destination:work" }));
            Assert.That(retrieval[1].DetailKeys, Is.EqualTo(new[] { "route.resolvable" }));
            Assert.That(lostRoute[0].Status, Is.EqualTo("unavailable"));
            Assert.That(lostRoute[0].LocationHandles, Is.Empty);
            Assert.That(lostRoute[0].DetailKeys, Is.EqualTo(new[] { "route.unavailable" }));
            Assert.That(discovery.SelectMany(value => value.OpportunityTargetHandles),
                Has.None.EqualTo("station-destination:work"));
            Assert.That(retrieval.SelectMany(value => value.FactHandles), Has.None.EqualTo("fact-2"));
        });
    }

    [Test]
    public void SenseFollowupRejectsUnknownOrMalformedTargetsBeforeRescan()
    {
        var request = new NCNeuralRequestEnvelope(
            Guid.NewGuid(),
            NCNeuralRequestKind.StrategicDecision,
            NCNeuralPriority.P1,
            new NCActorId(Guid.NewGuid()),
            new NCSessionEpoch(Guid.NewGuid()),
            1,
            new NCActiveWorldTime(100),
            new ProtoId<NCNeuralPromptProfilePrototype>("NCLivingCityDefault"),
            "living-city-default",
            3,
            "physical-gpt-alife-v3",
            [],
            ["NCCognitionHoldPosition"],
            [],
            "ru-RU",
            PerceivedSubjects: [Subject("person-1", "neutral")]);

        Assert.Multiple(() =>
        {
            Assert.That(NCNeuralContextBuilderSystem.ValidSenseFollowup(request,
                [new("look-around", ""), new("inspect-subject", "person-1")]), Is.True);
            Assert.That(NCNeuralContextBuilderSystem.ValidSenseFollowup(request,
                [new("query-nearby-affordances", ""), new("recall-memories", "person-1")]), Is.True);
            Assert.That(NCNeuralContextBuilderSystem.ValidSenseFollowup(request,
                [new("inspect-object", "zone:remembered")]), Is.False);
            Assert.That(NCNeuralContextBuilderSystem.ValidSenseFollowup(request,
                [new("examine-route", "zone:remembered")]), Is.False);
            Assert.That(NCNeuralContextBuilderSystem.ValidSenseFollowup(request,
                [new("check-body", "person-1")]), Is.False);
            Assert.That(NCNeuralContextBuilderSystem.ValidSenseFollowup(request,
                [new("look-around", ""), new("check-body", ""), new("check-inventory", "")]), Is.False);
        });
    }

    [Test]
    public void DirectAssaultBypassesCompletedCooldownButAmbientAndEqualSeverityDoNot()
    {
        var now = new NCActiveWorldTime(100);
        var next = new NCActiveWorldTime(200);

        Assert.That(NCNeuralContextBuilderSystem.ShouldSuppressIncidentCooldown(
            true, NCNeuralPriority.P0, NCNeuralPriority.P0, next, now), Is.False);
        Assert.That(NCNeuralContextBuilderSystem.ShouldSuppressIncidentCooldown(
            false, NCNeuralPriority.P2, NCNeuralPriority.P1, next, now), Is.True);
        Assert.That(NCNeuralContextBuilderSystem.ShouldSuppressIncidentCooldown(
            false, NCNeuralPriority.P0, NCNeuralPriority.P1, next, now), Is.False);
    }

    private static NCNeuralFactCandidate Candidate(
        string domain,
        string key,
        string subject,
        float confidence,
        long observedAt) =>
        new(new NCNeuralFact(domain, key, subject, confidence, "Personal"), observedAt);

    private static NCNeuralPerceivedSubject Subject(string handle, string kind) =>
        new(handle, $"visible {kind}", kind, 1f, 100);
}
