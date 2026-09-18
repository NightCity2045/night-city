// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable enable

using Content.Server._NC.LivingCity.Neural;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Cognition;
using Content.Shared.FixedPoint;
using Content.Shared.Roles;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Content.Tests.Server._NC.LivingCity.Neural;

[TestFixture]
public sealed class NCWorldOpportunityBusSystemTest
{
    private static readonly NCWorldOpportunityLimits Limits = new(32, 8, 8, 8, 4, 6, 2);

    [Test]
    public void ExpiredOpportunityIsRemovedOnActorQuery()
    {
        var store = new NCWorldOpportunityStore();
        var actor = NCActorId.New();
        var opportunity = Opportunity(actor, "subject-1", reward: 500, expiresAt: 20);

        Assert.That(store.TryPublish(opportunity, new NCActiveWorldTime(10), Limits, out _),
            Is.EqualTo(NCWorldOpportunityPublishStatus.Added));
        Assert.That(Query(store, actor, new NCActiveWorldTime(20), new HashSet<string> { "subject-1" }), Is.Empty);
        Assert.That(store.Count, Is.Zero);
    }

    [Test]
    public void StableIdDeduplicatesAndRefreshesEphemeralTargetHandle()
    {
        var store = new NCWorldOpportunityStore();
        var actor = NCActorId.New();
        var correlation = Guid.NewGuid();
        var first = Opportunity(actor, "station-destination:first", reward: 700, expiresAt: 30,
            source: "assignment", kind: "task.report-to-assigned-workplace", correlation: correlation,
            targetKind: NCWorldOpportunityTargetKind.Location, assignment: "job:CargoTechnician",
            requiredCapabilities: ["NCCognitionMoveToStationDestination"]);
        var refreshed = first with
        {
            TargetHandle = "station-destination:refreshed",
            ExpiresAt = new NCActiveWorldTime(40),
        };

        Assert.Multiple(() =>
        {
            Assert.That(store.TryPublish(first, new NCActiveWorldTime(10), Limits, out _),
                Is.EqualTo(NCWorldOpportunityPublishStatus.Added));
            Assert.That(store.TryPublish(first, new NCActiveWorldTime(10), Limits, out _),
                Is.EqualTo(NCWorldOpportunityPublishStatus.Duplicate));
            Assert.That(store.TryPublish(refreshed, new NCActiveWorldTime(11), Limits, out _),
                Is.EqualTo(NCWorldOpportunityPublishStatus.Updated));
            Assert.That(store.Count, Is.EqualTo(1));
        });

        var result = store.Query(
            actor,
            new HashSet<string>(),
            new HashSet<string> { refreshed.TargetHandle },
            new HashSet<string> { "NCCognitionMoveToStationDestination" },
            new HashSet<string> { "job:CargoTechnician" },
            new NCActiveWorldTime(12),
            8,
            Limits);

        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Length.EqualTo(1));
            Assert.That(result[0].OpportunityId, Is.EqualTo(first.OpportunityId.ToString()));
            Assert.That(result[0].TargetHandle, Is.EqualTo(refreshed.TargetHandle));
        });
    }

    [Test]
    public void QueryIsolatesActorVisibilityCapabilityAndAssignment()
    {
        var store = new NCWorldOpportunityStore();
        var actor = NCActorId.New();
        var other = NCActorId.New();
        var now = new NCActiveWorldTime(10);
        var privateOpportunity = Opportunity(actor, "subject-1", reward: 500, expiresAt: 30,
            requiredCapabilities: ["NCCognitionRespondToThreat"]);
        var otherOpportunity = Opportunity(other, "subject-1", reward: 900, expiresAt: 30);
        var assignment = Opportunity(actor, "station-destination:work", reward: 700, expiresAt: 30,
            source: "assignment", kind: "task.report-to-assigned-workplace",
            targetKind: NCWorldOpportunityTargetKind.Location, assignment: "job:Engineer");
        store.TryPublish(privateOpportunity, now, Limits, out _);
        store.TryPublish(otherOpportunity, now, Limits, out _);
        store.TryPublish(assignment, now, Limits, out _);

        var hidden = store.Query(
            actor,
            new HashSet<string> { "subject-1" },
            new HashSet<string> { "station-destination:work" },
            new HashSet<string>(),
            new HashSet<string>(),
            now,
            8,
            Limits);
        var eligible = store.Query(
            actor,
            new HashSet<string> { "subject-1" },
            new HashSet<string> { "station-destination:work" },
            new HashSet<string> { "NCCognitionRespondToThreat" },
            new HashSet<string> { "job:Engineer" },
            now,
            8,
            Limits);

        Assert.Multiple(() =>
        {
            Assert.That(hidden, Is.Empty);
            Assert.That(eligible.Select(value => value.OpportunityId),
                Is.EquivalentTo(new[] { privateOpportunity.OpportunityId.ToString(), assignment.OpportunityId.ToString() }));
            Assert.That(eligible.Select(value => value.OpportunityId),
                Has.None.EqualTo(otherOpportunity.OpportunityId.ToString()));
        });
    }

    [Test]
    public void WeightedScoreOrdersHigherRewardFirstWhenOtherFactorsMatch()
    {
        var store = new NCWorldOpportunityStore();
        var actor = NCActorId.New();
        var now = new NCActiveWorldTime(10);
        var low = Opportunity(actor, "subject-low", reward: 200, expiresAt: 30);
        var high = Opportunity(actor, "subject-high", reward: 800, expiresAt: 30);
        store.TryPublish(low, now, Limits, out _);
        store.TryPublish(high, now, Limits, out _);

        var result = Query(store, actor, now, new HashSet<string> { "subject-low", "subject-high" });

        Assert.That(result.Select(value => value.OpportunityId),
            Is.EqualTo(new[] { high.OpportunityId.ToString(), low.OpportunityId.ToString() }));
    }

    [Test]
    public void VerifiedResolutionRemovesAndTombstonesExactOpportunity()
    {
        var store = new NCWorldOpportunityStore();
        var actor = NCActorId.New();
        var now = new NCActiveWorldTime(10);
        var opportunity = Opportunity(actor, "subject-1", reward: 500, expiresAt: 30);
        store.TryPublish(opportunity, now, Limits, out _);

        Assert.Multiple(() =>
        {
            Assert.That(store.TryResolve(actor, opportunity.OpportunityId, Guid.NewGuid(), now, Limits), Is.False);
            Assert.That(store.TryResolve(actor, opportunity.OpportunityId, opportunity.CorrelationId, now, Limits), Is.True);
            Assert.That(store.TryPublish(opportunity, now, Limits, out _),
                Is.EqualTo(NCWorldOpportunityPublishStatus.Resolved));
            Assert.That(store.Count, Is.Zero);
        });
    }

    [Test]
    public void QueryProjectionFeedsTheExistingActivePerceptionContext()
    {
        var store = new NCWorldOpportunityStore();
        var actor = NCActorId.New();
        var now = new NCActiveWorldTime(10);
        var opportunity = Opportunity(actor, "item-1", reward: 500, expiresAt: 30);
        store.TryPublish(opportunity, now, Limits, out _);
        var projected = Query(store, actor, now, new HashSet<string> { "item-1" });

        var senseResults = NCNeuralContextBuilderSystem.BuildSenseResults(
            [new NCNeuralSenseRequest("query-nearby-affordances", string.Empty)],
            [new NCNeuralPerceivedSubject("item-1", "visible item", "item", 1f, 30)],
            [],
            null,
            worldOpportunities: projected);

        Assert.Multiple(() =>
        {
            Assert.That(projected[0].Kind, Is.EqualTo("nearby-actionable-affordance"));
            Assert.That(projected[0].Reward, Is.EqualTo(500));
            Assert.That(senseResults[0].OpportunityTargetHandles, Is.EqualTo(new[] { "item-1" }));
        });
    }

    [Test]
    public void MedicalRuleRequiresMatchingJobEventAndVisibleTarget()
    {
        var actor = NCActorId.New();
        var now = new NCActiveWorldTime(10);
        var rule = MedicalRule();
        var perceived = new NCCityLocalPerceivedEvent(
            Guid.NewGuid(),
            NCCityLocalPerceivedEventKind.SevereInjury,
            EntityUid.Invalid,
            "patient-1",
            EntityUid.Invalid,
            "patient-1",
            1f,
            now,
            new NCActiveWorldTime(30));

        Assert.Multiple(() =>
        {
            Assert.That(NCWorldOpportunityBusSystem.TryCreateObservedEventOpportunity(
                actor,
                new ProtoId<JobPrototype>("MedicalDoctor"),
                new HashSet<string> { "patient-1" },
                perceived,
                rule,
                now,
                out var opportunity), Is.True);
            Assert.That(opportunity.Kind, Is.EqualTo("task.treat-visible-patient"));
            Assert.That(opportunity.TargetHandle, Is.EqualTo("patient-1"));
            Assert.That(opportunity.AssignmentKey, Is.EqualTo("job:MedicalDoctor"));
            Assert.That(opportunity.RequiredCapabilityIds,
                Is.EqualTo(new[] { "NCCognitionApproachPerson", "NCCognitionUseHeldOnPerson" }));
            Assert.That(opportunity.CompletionCapabilityIds,
                Is.EqualTo(new[] { "NCCognitionUseHeldOnPerson" }));

            Assert.That(NCWorldOpportunityBusSystem.TryCreateObservedEventOpportunity(
                actor,
                new ProtoId<JobPrototype>("Passenger"),
                new HashSet<string> { "patient-1" },
                perceived,
                rule,
                now,
                out _), Is.False);
            Assert.That(NCWorldOpportunityBusSystem.TryCreateObservedEventOpportunity(
                actor,
                new ProtoId<JobPrototype>("MedicalDoctor"),
                new HashSet<string>(),
                perceived,
                rule,
                now,
                out _), Is.False);
            Assert.That(NCWorldOpportunityBusSystem.TryCreateObservedEventOpportunity(
                actor,
                new ProtoId<JobPrototype>("MedicalDoctor"),
                new HashSet<string> { "patient-1" },
                perceived with { Kind = NCCityLocalPerceivedEventKind.BodyDiscovered },
                rule,
                now,
                out _), Is.False);
        });
    }

    [Test]
    public void MedicalOpportunityFiltersBothCapabilitiesAndResolvesOnlyOnCompletedTreatment()
    {
        var store = new NCWorldOpportunityStore();
        var actor = NCActorId.New();
        var other = NCActorId.New();
        var now = new NCActiveWorldTime(10);
        var opportunity = Opportunity(
            actor,
            "patient-1",
            850,
            30,
            kind: "task.treat-visible-patient",
            assignment: "job:MedicalDoctor",
            requiredCapabilities: ["NCCognitionApproachPerson", "NCCognitionUseHeldOnPerson"],
            completionCapabilities: ["NCCognitionUseHeldOnPerson"]);
        Assert.That(store.TryPublish(opportunity, now, Limits, out _),
            Is.EqualTo(NCWorldOpportunityPublishStatus.Added));

        var missingTreatmentCapability = store.Query(
            actor,
            new HashSet<string> { "patient-1" },
            new HashSet<string>(),
            new HashSet<string> { "NCCognitionApproachPerson" },
            new HashSet<string> { "job:MedicalDoctor" },
            now,
            8,
            Limits);
        var eligible = store.Query(
            actor,
            new HashSet<string> { "patient-1" },
            new HashSet<string>(),
            new HashSet<string> { "NCCognitionApproachPerson", "NCCognitionUseHeldOnPerson" },
            new HashSet<string> { "job:MedicalDoctor" },
            now,
            8,
            Limits);

        Assert.Multiple(() =>
        {
            Assert.That(missingTreatmentCapability, Is.Empty);
            Assert.That(eligible, Has.Length.EqualTo(1));
            Assert.That(store.TryFindUniqueCommandMatch(
                actor,
                "patient-1",
                "NCCognitionApproachPerson",
                now,
                Limits,
                out _), Is.False, "approach must not retire treatment work");
            Assert.That(store.TryFindUniqueCommandMatch(
                other,
                "patient-1",
                "NCCognitionUseHeldOnPerson",
                now,
                Limits,
                out _), Is.False, "another actor cannot claim this doctor's work");
            Assert.That(store.TryFindUniqueCommandMatch(
                actor,
                "patient-1",
                "NCCognitionUseHeldOnPerson",
                now,
                Limits,
                out var match), Is.True);
            Assert.That(match, Is.EqualTo(opportunity.OpportunityId));
            Assert.That(NCWorldOpportunityBusSystem.CanResolveCommandResult("failed", now.Ticks, now), Is.False);
            Assert.That(NCWorldOpportunityBusSystem.CanResolveCommandResult("completed", now.Ticks + 1, now), Is.False);
            Assert.That(store.Count, Is.EqualTo(1));
            Assert.That(NCWorldOpportunityBusSystem.CanResolveCommandResult("completed", now.Ticks, now), Is.True);
            Assert.That(store.TryResolve(actor, match, null, now, Limits), Is.True);
            Assert.That(store.Count, Is.Zero);
        });
    }

    [Test]
    public void TreatmentCompletionRequiresAuthoritativeDamageReduction()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NCHumanVanillaActionAdapterSystem.HasAuthoritativeDamageReduction(
                false,
                FixedPoint2.New(20),
                FixedPoint2.New(10)), Is.False);
            Assert.That(NCHumanVanillaActionAdapterSystem.HasAuthoritativeDamageReduction(
                true,
                FixedPoint2.New(20),
                FixedPoint2.New(20)), Is.False);
            Assert.That(NCHumanVanillaActionAdapterSystem.HasAuthoritativeDamageReduction(
                true,
                FixedPoint2.New(20),
                FixedPoint2.New(10)), Is.True);
        });
    }

    private static NCNeuralWorldOpportunity[] Query(
        NCWorldOpportunityStore store,
        NCActorId actor,
        NCActiveWorldTime now,
        IReadOnlySet<string> visible) =>
        store.Query(actor, visible, new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), now, 8,
            Limits);

    private static NCWorldOpportunity Opportunity(
        NCActorId actor,
        string target,
        int reward,
        long expiresAt,
        string source = "perception",
        string kind = "nearby-actionable-affordance",
        Guid? correlation = null,
        NCWorldOpportunityTargetKind targetKind = NCWorldOpportunityTargetKind.Subject,
        string assignment = "",
        IReadOnlyList<string>? requiredCapabilities = null,
        IReadOnlyList<string>? completionCapabilities = null)
    {
        var resolvedCorrelation = correlation ?? Guid.NewGuid();
        return new NCWorldOpportunity(
            NCWorldOpportunityBusSystem.CreateStableId(actor, source, resolvedCorrelation, kind),
            actor,
            kind,
            targetKind,
            target,
            ["object.actionable"],
            requiredCapabilities ?? [],
            reward,
            500,
            100,
            new NCActiveWorldTime(10),
            new NCActiveWorldTime(expiresAt),
            source,
            resolvedCorrelation,
            assignment,
            completionCapabilities);
    }

    private static NCObservedEventOpportunityRule MedicalRule() =>
        new(
            "medical-doctor-treat-severe-injury",
            new ProtoId<JobPrototype>("MedicalDoctor"),
            NCCityLocalPerceivedEventKind.SevereInjury,
            "task.treat-visible-patient",
            ["medical.treat-visible-patient"],
            ["NCCognitionApproachPerson", "NCCognitionUseHeldOnPerson"],
            ["NCCognitionUseHeldOnPerson"],
            850,
            950,
            200,
            TimeSpan.FromMinutes(2));
}
