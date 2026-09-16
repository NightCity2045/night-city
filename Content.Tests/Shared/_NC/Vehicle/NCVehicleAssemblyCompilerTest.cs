// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Numerics;
using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle.Prototypes;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Vehicle;

// Deliberately malformed prototype fixtures exercise pure validation without starting a server/prototype manager.
#pragma warning disable RA0039

[TestFixture]
[TestOf(typeof(NCVehicleAssemblyCompiler))]
public sealed class NCVehicleAssemblyCompilerTest
{
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(-1f)]
    [TestCase(0f)]
    public void InvalidCalibrationCannotEnterTheForceSolver(float invalid)
    {
        Assert.That(NCVehicleCalibrationValidator.IsValid(new NCVehicleTuningPrototype()), Is.True);
        Assert.That(NCVehicleCalibrationValidator.IsValid(new NCVehicleTuningPrototype { EngineMultiplier = invalid }), Is.False);
        Assert.That(NCVehicleCalibrationValidator.IsValid(new NCVehicleTuningPrototype { FrontBrakeMultiplier = invalid }), Is.False);
        Assert.That(NCVehicleCalibrationValidator.IsValid(new NCVehicleTuningPrototype { FuelMultiplier = invalid }), Is.False);
    }

    [Test]
    public void SeriesCircuitsUseWeakestPartAndMissingOptionalConversionDoesNotDisableStockCar()
    {
        var chassis = Chassis();
        chassis.Slots["engine"].Functions.Add(NCVehicleFunction.Cooling);
        chassis.Slots["gearbox"].Functions.Add(NCVehicleFunction.Cooling);
        chassis.Slots["conversion"] = new NCVehicleSlotDefinition
        {
            Mount = "conversion", OptionalFunctions = true,
            Functions = new HashSet<NCVehicleFunction> { NCVehicleFunction.Transmission },
        };
        var installed = new Dictionary<string, NCVehicleInstalledPart>
        {
            ["engine"] = new("Engine", 0.8f), ["gearbox"] = new("Gearbox", 0.6f),
        };
        var part = new NCVehiclePartPrototype { Mount = "test", Mass = 1 };
        var values = NCVehiclePerformanceCompiler.Compile(chassis, installed, _ => part, 4);
        Assert.That(NCVehiclePerformanceCompiler.Get(values, NCVehicleFunction.Cooling), Is.EqualTo(0.6f));
        Assert.That(NCVehiclePerformanceCompiler.Get(values, NCVehicleFunction.Transmission), Is.EqualTo(1));
        installed["gearbox"] = installed["gearbox"] with { Connected = false };
        values = NCVehiclePerformanceCompiler.Compile(chassis, installed, _ => part, 4);
        Assert.That(NCVehiclePerformanceCompiler.Get(values, NCVehicleFunction.Cooling), Is.Zero);
    }

    private static NCVehicleChassisPrototype Chassis() => new()
    {
        Mass = 100,
        Inertia = 20,
        Slots = new Dictionary<string, NCVehicleSlotDefinition>
        {
            ["engine"] = new() { Mount = "engine", Required = true, Position = new Vector2(0, 2) },
            ["gearbox"] = new() { Mount = "gearbox", Requires = new HashSet<string> { "engine" } },
        },
    };

    [Test]
    public void OffsetPartUsesCombinedCenterAndParallelAxisInertia()
    {
        var installed = new Dictionary<string, NCVehicleInstalledPart>
        {
            ["engine"] = new("Engine", 1),
        };
        var part = new NCVehiclePartPrototype { Mount = "engine", Mass = 100, Inertia = 10 };

        Assert.That(NCVehicleAssemblyCompiler.TryCompile(Chassis(), installed, _ => part,
            out var profile, out _), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(profile.Mass, Is.EqualTo(200));
            Assert.That(profile.CenterOfMass, Is.EqualTo(new Vector2(0, 1)));
            Assert.That(profile.Inertia, Is.EqualTo(230).Within(0.001));
            Assert.That(profile.RequiredSlotsFilled, Is.True);
        });
    }

    [Test]
    public void EmptyRequiredSlotIsIncompleteButPhysicallyValid()
    {
        Assert.That(NCVehicleAssemblyCompiler.TryCompile(Chassis(), new Dictionary<string, NCVehicleInstalledPart>(),
            _ => null, out var profile, out _), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(profile.RequiredSlotsFilled, Is.False);
            Assert.That(profile.Mass, Is.EqualTo(100));
        });
    }

    [Test]
    public void DestroyedInstalledPartStillHasMass()
    {
        var installed = new Dictionary<string, NCVehicleInstalledPart> { ["engine"] = new("Engine", 0) };
        Assert.That(NCVehicleAssemblyCompiler.TryCompile(Chassis(), installed,
            _ => new NCVehiclePartPrototype { Mount = "engine", Mass = 100 }, out var profile, out _), Is.True);
        Assert.That(profile.Mass, Is.EqualTo(200));
    }

    [Test]
    public void CycleInEmptyBranchIsRejected()
    {
        var chassis = Chassis();
        chassis.Slots["engine"].Requires.Add("gearbox");
        Assert.That(NCVehicleAssemblyCompiler.TryCompile(chassis, new Dictionary<string, NCVehicleInstalledPart>(),
            _ => null, out var profile, out var failure), Is.False);
        Assert.That(failure.Error, Is.EqualTo(NCVehicleAssemblyError.DependencyCycle));
        Assert.That(profile, Is.EqualTo(default(NCVehicleAssemblyProfile)));
    }

    [Test]
    public void MissingDependencyDefinitionIsRejected()
    {
        var chassis = Chassis();
        chassis.Slots["engine"].Requires.Add("missing");
        Assert.That(NCVehicleAssemblyCompiler.TryCompile(chassis, new Dictionary<string, NCVehicleInstalledPart>(),
            _ => null, out _, out var failure), Is.False);
        Assert.That(failure.Error, Is.EqualTo(NCVehicleAssemblyError.MissingDependency));
    }

    [Test]
    public void InstalledPartRequiresOccupiedPrerequisite()
    {
        var installed = new Dictionary<string, NCVehicleInstalledPart> { ["gearbox"] = new("Gearbox", 1) };
        Assert.That(NCVehicleAssemblyCompiler.TryCompile(Chassis(), installed,
            _ => new NCVehiclePartPrototype { Mount = "gearbox", Mass = 20 }, out _, out var failure), Is.False);
        Assert.That(failure.Error, Is.EqualTo(NCVehicleAssemblyError.UnoccupiedDependency));
    }

    [Test]
    public void IncompatibleMountDoesNotPublishPartialProfile()
    {
        var installed = new Dictionary<string, NCVehicleInstalledPart> { ["engine"] = new("WrongPart", 1) };
        Assert.That(NCVehicleAssemblyCompiler.TryCompile(Chassis(), installed,
            _ => new NCVehiclePartPrototype { Mount = "other", Mass = 20 }, out var profile, out var failure), Is.False);
        Assert.That(failure.Error, Is.EqualTo(NCVehicleAssemblyError.IncompatibleMount));
        Assert.That(profile, Is.EqualTo(default(NCVehicleAssemblyProfile)));
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(-1f)]
    [TestCase(1.1f)]
    public void InvalidConditionIsRejected(float condition)
    {
        var installed = new Dictionary<string, NCVehicleInstalledPart> { ["engine"] = new("Engine", condition) };
        Assert.That(NCVehicleAssemblyCompiler.TryCompile(Chassis(), installed,
            _ => new NCVehiclePartPrototype { Mount = "engine", Mass = 20 }, out _, out var failure), Is.False);
        Assert.That(failure.Error, Is.EqualTo(NCVehicleAssemblyError.InvalidCondition));
    }

    [Test]
    public void AggregateOverflowIsRejected()
    {
        var chassis = Chassis();
        chassis.Mass = float.MaxValue;
        var installed = new Dictionary<string, NCVehicleInstalledPart> { ["engine"] = new("Engine", 1) };
        Assert.That(NCVehicleAssemblyCompiler.TryCompile(chassis, installed,
            _ => new NCVehiclePartPrototype { Mount = "engine", Mass = float.MaxValue }, out _, out var failure), Is.False);
        Assert.That(failure.Error, Is.EqualTo(NCVehicleAssemblyError.InvalidMassProperties));
    }

    [Test]
    public void RecompileDoesNotRefillRemovedDefaultPart()
    {
        var chassis = Chassis();
        chassis.Slots["engine"].DefaultPart = "Engine";
        var installed = new Dictionary<string, NCVehicleInstalledPart>();
        Assert.That(NCVehicleAssemblyCompiler.TryCompile(chassis, installed, _ => null, out var profile, out _), Is.True);
        Assert.That(installed, Is.Empty);
        Assert.That(profile.RequiredSlotsFilled, Is.False);
    }

    [Test]
    public void UnknownPrototypeIsRejected()
    {
        var installed = new Dictionary<string, NCVehicleInstalledPart> { ["engine"] = new("Missing", 1) };
        Assert.That(NCVehicleAssemblyCompiler.TryCompile(Chassis(), installed, _ => null, out _, out var failure), Is.False);
        Assert.That(failure.Error, Is.EqualTo(NCVehicleAssemblyError.UnknownPart));
    }

    [Test]
    public void UnknownSlotIsRejectedBeforeLookingUpItsPart()
    {
        var installed = new Dictionary<string, NCVehicleInstalledPart> { ["missing"] = new("Engine", 1) };
        Assert.That(NCVehicleAssemblyCompiler.TryCompile(Chassis(), installed, _ => null, out _, out var failure), Is.False);
        Assert.That(failure.Error, Is.EqualTo(NCVehicleAssemblyError.UnknownSlot));
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(-1f)]
    [TestCase(0f)]
    public void InvalidPartMassIsRejected(float mass)
    {
        var installed = new Dictionary<string, NCVehicleInstalledPart> { ["engine"] = new("Engine", 1) };
        Assert.That(NCVehicleAssemblyCompiler.TryCompile(Chassis(), installed,
            _ => new NCVehiclePartPrototype { Mount = "engine", Mass = mass }, out _, out var failure), Is.False);
        Assert.That(failure.Error, Is.EqualTo(NCVehicleAssemblyError.InvalidPart));
    }
}

#pragma warning restore RA0039
