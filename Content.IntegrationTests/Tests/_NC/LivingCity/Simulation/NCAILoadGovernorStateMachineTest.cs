// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using Content.Server._NC.LivingCity.Simulation;
using NUnit.Framework;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Simulation;

[TestFixture]
public sealed class NCAILoadGovernorStateMachineTest
{
    private static readonly NCAILoadGovernorThresholds Thresholds = new(
        DegradeSamples: 3,
        RecoverSamples: 2,
        ElevatedEnterFrameMilliseconds: 28f,
        ElevatedExitFrameMilliseconds: 22f,
        CriticalEnterFrameMilliseconds: 45f,
        CriticalExitFrameMilliseconds: 32f,
        ElevatedEnterWorkRatio: 0.12f,
        ElevatedExitWorkRatio: 0.08f,
        CriticalEnterWorkRatio: 0.18f,
        CriticalExitWorkRatio: 0.13f,
        ElevatedEnterQueueRatio: 0.6f,
        ElevatedExitQueueRatio: 0.35f,
        CriticalEnterQueueRatio: 0.85f,
        CriticalExitQueueRatio: 0.6f,
        ElevatedBacklogCount: 1,
        CriticalBacklogCount: 3,
        CriticalExitBacklogCount: 2);

    [Test]
    public void TransientPressureDoesNotDegrade()
    {
        var state = new NCAILoadGovernorStateMachine();
        var high = new NCAILoadGovernorSample(30f, 0f, 0f, 0);
        var normal = new NCAILoadGovernorSample(16f, 0f, 0f, 0);

        Assert.That(state.Step(high, NCAILoadPressureReason.FrameTime, Thresholds).State,
            Is.EqualTo(NCAILoadState.Normal));
        Assert.That(state.Step(normal, NCAILoadPressureReason.None, Thresholds).CandidateSamples, Is.Zero);
        Assert.That(state.State, Is.EqualTo(NCAILoadState.Normal));
    }

    [Test]
    public void SustainedPressureDegradesAndRecoversOneLevelAtATime()
    {
        var state = new NCAILoadGovernorStateMachine();
        var critical = new NCAILoadGovernorSample(50f, 0.2f, 0.9f, 3);
        var normal = new NCAILoadGovernorSample(16f, 0.05f, 0.1f, 0);

        for (var i = 0; i < 3; i++)
            state.Step(critical, NCAILoadPressureReason.FrameTime, Thresholds);
        Assert.That(state.State, Is.EqualTo(NCAILoadState.Critical));

        for (var i = 0; i < 2; i++)
            state.Step(normal, NCAILoadPressureReason.None, Thresholds);
        Assert.That(state.State, Is.EqualTo(NCAILoadState.Elevated));

        for (var i = 0; i < 2; i++)
            state.Step(normal, NCAILoadPressureReason.None, Thresholds);
        Assert.That(state.State, Is.EqualTo(NCAILoadState.Normal));
    }

    [Test]
    public void ElevatedStateEscalatesOnlyAfterHysteresis()
    {
        var state = new NCAILoadGovernorStateMachine();
        var elevated = new NCAILoadGovernorSample(30f, 0f, 0f, 0);
        var critical = new NCAILoadGovernorSample(50f, 0f, 0f, 0);

        for (var i = 0; i < 3; i++)
            state.Step(elevated, NCAILoadPressureReason.FrameTime, Thresholds);
        Assert.That(state.State, Is.EqualTo(NCAILoadState.Elevated));

        state.Step(critical, NCAILoadPressureReason.FrameTime, Thresholds);
        state.Step(critical, NCAILoadPressureReason.FrameTime, Thresholds);
        Assert.That(state.State, Is.EqualTo(NCAILoadState.Elevated));
        state.Step(critical, NCAILoadPressureReason.FrameTime, Thresholds);
        Assert.That(state.State, Is.EqualTo(NCAILoadState.Critical));
    }

    [Test]
    public void FractionalDispatchCreditsThrottleWithoutStarvation()
    {
        var credit = 0f;
        var dispatches = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var step = NCAILoadGovernorSystem.AdvanceDispatchCredit(credit, 1, 0.25f);
            dispatches.Add(step.Dispatches);
            credit = step.Remainder;
        }

        Assert.That(dispatches, Is.EqualTo(new[] { 0, 0, 0, 1 }));
        Assert.That(credit, Is.Zero);
    }
}
