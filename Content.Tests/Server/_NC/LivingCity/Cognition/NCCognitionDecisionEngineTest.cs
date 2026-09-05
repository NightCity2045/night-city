// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using System;

namespace Content.Tests.Server._NC.LivingCity.Cognition;

[TestFixture]
public sealed class NCCognitionDecisionEngineTest : ContentUnitTest
{
    private const string PolicyId = "NCTestCognitionDecisionPolicy";

    private NCCognitionDecisionPolicyPrototype _policy = default!;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        IoCManager.Resolve<ISerializationManager>().Initialize();
        var prototypes = IoCManager.Resolve<IPrototypeManager>();
        prototypes.Initialize();
        prototypes.LoadString($$"""
            - type: ncCognitionDecisionPolicy
              id: {{PolicyId}}
              evaluationInterval: 1s
              minimumCommitment: 1s
              intentionLifetime: 1m
              beliefLifetime: 5m
              beliefAgingTime: 1m
              agingConfidenceMultiplier: 0.75
              staleConfidenceMultiplier: 0.5
              switchUtilityDelta: 0.1
              switchCost: 0.1
              minimumBeliefConfidence: 0.1
              maximumBeliefs: 8
              maximumDesires: 4
            """);
        prototypes.ResolveResults();
        _policy = prototypes.Index<NCCognitionDecisionPolicyPrototype>(PolicyId);
    }

    [Test]
    public void ProjectBeliefsDropsFutureObservationWithoutThrowing()
    {
        var actorId = NCActorId.New();
        var now = new NCActiveWorldTime(TimeSpan.FromMinutes(1).Ticks);
        var future = now.Advance(TimeSpan.FromMinutes(1));
        var belief = new NCBeliefSnapshot(
            Guid.NewGuid(),
            "test.future-belief",
            new NCCognitionSubjectRef(NCCognitionReferenceKind.StableKey, "test-subject"),
            NCCognitionDomain.Core,
            "test",
            1,
            NCCognitionValue.FromBoolean(true),
            1f,
            NCBeliefFreshness.Current,
            future,
            future.Advance(TimeSpan.FromMinutes(1)),
            1);

        NCBeliefSnapshot[] result = null;
        Assert.DoesNotThrow(() => result = NCCognitionDecisionEngine.ProjectBeliefs(
            actorId,
            [belief],
            [],
            _policy,
            now));
        Assert.That(result, Is.Empty);
    }
}
