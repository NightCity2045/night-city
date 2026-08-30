// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using NUnit.Framework;
using System;

namespace Content.Tests.Server._NC.LivingCity.Cognition;

[TestFixture]
public sealed class NCCognitionDecisionEngineTest
{
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
            new NCCognitionDecisionPolicyPrototype(),
            now));
        Assert.That(result, Is.Empty);
    }
}
