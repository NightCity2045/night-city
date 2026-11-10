// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Reflection;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using NUnit.Framework;

namespace Content.Tests._NC.Medical.Pharmacology;

[TestFixture]
public sealed class NCReactorMathTests
{
    private static NCPharmaReactionProfilePrototype Profile(params (string Name, object Value)[] values)
    {
        var instance = new NCPharmaReactionProfilePrototype();
        foreach (var (name, value) in values)
        {
            typeof(NCPharmaReactionProfilePrototype)
                .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(instance, value);
        }

        return instance;
    }

    private static NCPharmaReactionProfilePrototype Exothermic() =>
        Profile((nameof(NCPharmaReactionProfilePrototype.Exothermicity), 1.6f),
            (nameof(NCPharmaReactionProfilePrototype.Volatility), 1.3f));

    private static NCReactorState Run(
        NCPharmaReactionProfilePrototype profile,
        Func<NCReactorState, NCReactorControls> controller,
        int seconds)
    {
        var state = NCReactorMath.Start(profile);
        for (var i = 0; i < seconds && state.Progress < 1f; i++)
            NCReactorMath.Step(profile, state, controller(state), 1f);
        return state;
    }

    /// <summary>A competent operator: heat hard to the window, hold with a low step, cool when it climbs.</summary>
    private static NCReactorControls Careful(NCPharmaReactionProfilePrototype profile, NCReactorState state)
    {
        var heater = state.Temperature < profile.OptimalMin + 2f ? 3 : state.Temperature < profile.OptimalMax - 4f ? 1 : 0;
        var cooler = state.Temperature > profile.OptimalMax - 3f ? 2 : 0;
        return new NCReactorControls(heater, cooler, true, false);
    }

    [Test]
    public void AStableReactionHeldInItsWindowFinishesCleanly()
    {
        var profile = Profile();
        var state = Run(profile, s => Careful(profile, s), 900);

        Assert.That(state.Progress, Is.EqualTo(1f));
        Assert.That(state.PeakInstability, Is.LessThan(profile.WarningInstability));
        Assert.That(NCReactorMath.Evaluate(state).PurityFactor, Is.GreaterThan(0.8f));
    }

    [Test]
    public void ACarefulOperatorAlsoHoldsAnExothermicReaction()
    {
        var profile = Exothermic();
        var state = Run(profile, s => Careful(profile, s), 1200);

        Assert.That(state.Progress, Is.EqualTo(1f));
        Assert.That(state.PeakInstability, Is.LessThan(profile.DegradeInstability));
    }

    [Test]
    public void NeglectedHeatingEndsInAnAccident()
    {
        var profile = Profile();
        var state = Run(profile, _ => new NCReactorControls(3, 0, false, false), 900);

        Assert.That(state.PeakInstability, Is.GreaterThanOrEqualTo(profile.DegradeInstability));
    }

    private static NCPharmaReactionProfilePrototype Volatile() => Profile(
        (nameof(NCPharmaReactionProfilePrototype.OptimalMin), 50f),
        (nameof(NCPharmaReactionProfilePrototype.OptimalMax), 62f),
        (nameof(NCPharmaReactionProfilePrototype.SafeMin), 38f),
        (nameof(NCPharmaReactionProfilePrototype.SafeMax), 75f),
        (nameof(NCPharmaReactionProfilePrototype.Exothermicity), 2.4f),
        (nameof(NCPharmaReactionProfilePrototype.Volatility), 1.8f));

    [Test]
    public void AVolatileReactionRunsAwayWithoutCoolingAndIsHeldWithIt()
    {
        var profile = Volatile();
        var uncooled = NCReactorMath.Start(profile);
        var cooled = NCReactorMath.Start(profile);
        uncooled.Temperature = 55f;
        cooled.Temperature = 55f;
        var uncooledPeak = 0f;
        var cooledPeak = 0f;
        for (var i = 0; i < 300; i++)
        {
            NCReactorMath.Step(profile, uncooled, new NCReactorControls(0, 0, true, false), 1f);
            NCReactorMath.Step(profile, cooled, new NCReactorControls(0, 2, true, false), 1f);
            uncooledPeak = MathF.Max(uncooledPeak, uncooled.Temperature);
            cooledPeak = MathF.Max(cooledPeak, cooled.Temperature);
        }

        Assert.That(uncooledPeak, Is.GreaterThan(profile.SafeMax));
        Assert.That(cooledPeak, Is.LessThanOrEqualTo(profile.SafeMax));
        Assert.That(uncooled.PeakInstability, Is.GreaterThan(cooled.PeakInstability));
    }

    [Test]
    public void AnExothermicReactionPeaksHotterWithoutCooling()
    {
        var profile = Exothermic();
        var uncooled = NCReactorMath.Start(profile);
        var cooled = NCReactorMath.Start(profile);
        uncooled.Temperature = 62f;
        cooled.Temperature = 62f;
        var uncooledPeak = 0f;
        var cooledPeak = 0f;
        for (var i = 0; i < 200; i++)
        {
            NCReactorMath.Step(profile, uncooled, new NCReactorControls(0, 0, true, false), 1f);
            NCReactorMath.Step(profile, cooled, new NCReactorControls(0, 2, true, false), 1f);
            uncooledPeak = MathF.Max(uncooledPeak, uncooled.Temperature);
            cooledPeak = MathF.Max(cooledPeak, cooled.Temperature);
        }

        Assert.That(uncooledPeak, Is.GreaterThan(cooledPeak));
    }

    [Test]
    public void StirringSpeedsTheReactionUp()
    {
        var profile = Profile();
        var stirred = NCReactorMath.Start(profile);
        var still = NCReactorMath.Start(profile);
        stirred.Temperature = 65f;
        still.Temperature = 65f;
        for (var i = 0; i < 60; i++)
        {
            NCReactorMath.Step(profile, stirred, new NCReactorControls(1, 0, true, false), 1f);
            NCReactorMath.Step(profile, still, new NCReactorControls(1, 0, false, false), 1f);
        }

        Assert.That(stirred.Progress, Is.GreaterThan(still.Progress));
    }

    [Test]
    public void VentingLowersPressureAtTheCostOfSpeed()
    {
        var profile = Profile((nameof(NCPharmaReactionProfilePrototype.Volatility), 1.8f));
        var closed = NCReactorMath.Start(profile);
        var vented = NCReactorMath.Start(profile);
        closed.Temperature = 65f;
        vented.Temperature = 65f;
        for (var i = 0; i < 80; i++)
        {
            NCReactorMath.Step(profile, closed, new NCReactorControls(1, 0, true, false), 1f);
            NCReactorMath.Step(profile, vented, new NCReactorControls(1, 0, true, true), 1f);
        }

        Assert.That(vented.Pressure, Is.LessThan(closed.Pressure));
        Assert.That(vented.Progress, Is.LessThan(closed.Progress));
    }

    [Test]
    public void ACatalystSpeedsTheReactionAndAddsHeat()
    {
        var profile = Exothermic();
        var plain = NCReactorMath.Start(profile);
        var catalysed = NCReactorMath.Start(profile);
        plain.Temperature = 62f;
        catalysed.Temperature = 62f;
        catalysed.Catalyst = 1f;
        for (var i = 0; i < 30; i++)
        {
            NCReactorMath.Step(profile, plain, new NCReactorControls(0, 1, true, false), 1f);
            NCReactorMath.Step(profile, catalysed, new NCReactorControls(0, 1, true, false), 1f);
        }

        Assert.That(catalysed.Progress, Is.GreaterThan(plain.Progress));
        Assert.That(catalysed.Temperature, Is.GreaterThan(plain.Temperature));
    }

    [Test]
    public void TheSameControlsAlwaysGiveTheSameRun()
    {
        var profile = Exothermic();
        var first = Run(profile, s => Careful(profile, s), 300);
        var second = Run(profile, s => Careful(profile, s), 300);

        Assert.That(second.Temperature, Is.EqualTo(first.Temperature));
        Assert.That(second.Pressure, Is.EqualTo(first.Pressure));
        Assert.That(second.Progress, Is.EqualTo(first.Progress));
        Assert.That(second.Instability, Is.EqualTo(first.Instability));
    }

    [Test]
    public void TimeOutsideTheWindowCostsPurity()
    {
        var clean = new NCReactorState { Elapsed = 100f, SecondsOptimal = 100f, MixingIntegral = 100f };
        var sloppy = new NCReactorState
        {
            Elapsed = 100f, SecondsOptimal = 40f, SecondsSafe = 30f, SecondsOutside = 30f, MixingIntegral = 100f,
        };

        Assert.That(NCReactorMath.Evaluate(sloppy).PurityFactor, Is.LessThan(NCReactorMath.Evaluate(clean).PurityFactor));
    }

    [Test]
    public void PoorMixingCostsHomogeneity()
    {
        var mixed = new NCReactorState { Elapsed = 100f, MixingIntegral = 95f };
        var still = new NCReactorState { Elapsed = 100f, MixingIntegral = 30f };

        Assert.That(NCReactorMath.Evaluate(still).HomogeneityFactor, Is.LessThan(NCReactorMath.Evaluate(mixed).HomogeneityFactor));
    }

    [Test]
    public void DegradedAndQuenchedRunsCostYield()
    {
        var degraded = new NCReactorState { Elapsed = 100f, Progress = 1f, Degraded = true };
        var quenched = new NCReactorState { Elapsed = 100f, Progress = 0.6f, Quenched = true };
        var clean = new NCReactorState { Elapsed = 100f, Progress = 1f };

        Assert.That(NCReactorMath.Evaluate(clean).YieldFactor, Is.EqualTo(1f));
        Assert.That(NCReactorMath.Evaluate(degraded).YieldFactor, Is.EqualTo(0.5f));
        Assert.That(NCReactorMath.Evaluate(quenched).YieldFactor, Is.LessThan(0.5f));
    }

    [Test]
    public void WarningsAppearBeforeTheAccident()
    {
        var profile = Exothermic();
        var state = NCReactorMath.Start(profile);
        state.Temperature = 82f;
        state.Mixing = 0.1f;

        var warnings = NCReactorMath.ResolveWarnings(profile, state);

        Assert.That(warnings.HasFlag(NCReactorWarnings.HighTemperature), Is.True);
        Assert.That(warnings.HasFlag(NCReactorWarnings.PoorMixing), Is.True);
    }

    [Test]
    public void ARunOnlyProgressesInsideTheSafeWindow()
    {
        var profile = Profile();
        var cold = NCReactorMath.Start(profile);
        for (var i = 0; i < 30; i++)
            NCReactorMath.Step(profile, cold, new NCReactorControls(0, 0, true, false), 1f);

        Assert.That(cold.Progress, Is.EqualTo(0f));
    }
}
