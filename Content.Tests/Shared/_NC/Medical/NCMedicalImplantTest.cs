// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Internal;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Serialization.Manager;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCMedicalImplantTest : ContentUnitTest
{
    [OneTimeSetUp]
    public void InitializeSerialization() => IoCManager.Resolve<ISerializationManager>().Initialize();

    [TestCase(2f, 8f, 10f, 0f, 2f, true)]
    [TestCase(2f, 9f, 10f, 0f, 30f, false)]
    [TestCase(2f, 0f, 10f, 1f, 30f, false)]
    [TestCase(2f, 0f, 10f, 0f, 1f, false)]
    [TestCase(float.NaN, 0f, 10f, 0f, 30f, false)]
    public void ActualDoseMustFitBudgetStockAndCooldown(float dose, float delivered, float limit, float cooldown, float stock, bool expected) =>
        Assert.That(NCMedicalImplantMath.CanDose(dose, delivered, limit, cooldown, stock), Is.EqualTo(expected));

    [Test]
    public void CoarsePumpCannotHideItsActualDoseFromBudgetChecks()
    {
        var actual = NCMedicalImplantMath.MeteredDose(2, 5);
        Assert.That(actual, Is.EqualTo(2.5f));
        Assert.That(NCMedicalImplantMath.CanDose(actual, 8, 10, 0, 30), Is.False);
        Assert.That(NCMedicalImplantMath.MeteredDose(2, 1), Is.EqualTo(2));
    }

    [Test]
    public void MalformedSettingsFailClosed()
    {
        Assert.That(NCMedicalImplantMath.ValidSettings(2, 60, 10, 70, NCMedicalTrigger.OxygenBelow), Is.True);
        Assert.That(NCMedicalImplantMath.ValidSettings(2, 0, 10, 70, NCMedicalTrigger.OxygenBelow), Is.False);
        Assert.That(NCMedicalImplantMath.ValidSettings(5, 60, 2, 70, NCMedicalTrigger.OxygenBelow), Is.False);
        Assert.That(NCMedicalImplantMath.ValidSettings(2, 60, 10, float.PositiveInfinity, NCMedicalTrigger.OxygenBelow), Is.False);
        Assert.That(NCMedicalImplantMath.ValidSettings(2, 60, 10, 70, (NCMedicalTrigger) 255), Is.False);
    }

    [Test]
    public void ImplantBudgetAndControllerFirmwareSurviveSerialization()
    {
        var state = new NCCyberwareStateSnapshot
        {
            Profile = "NCAutoInjector", StableInstanceId = Guid.NewGuid(),
            MedicalImplant = new() { Delivered = 8, CooldownSeconds = 45, Automatic = true },
            Assembly = new() { Parts = [new() { Slot = "NCMedicalControllerSlot", EntityPrototype = "NCMedicalControllerPremium", MedicalFirmware = "NCMedicalFirmwareInjector" }] },
        };
        var serialization = IoCManager.Resolve<ISerializationManager>();
        var copy = serialization.Read<NCCyberwareStateSnapshot>(serialization.WriteValue(state, alwaysWrite: true), notNullableOverride: true);
        Assert.Multiple(() =>
        {
            Assert.That(copy.MedicalImplant!.Delivered, Is.EqualTo(8));
            Assert.That(copy.MedicalImplant.CooldownSeconds, Is.EqualTo(45));
            Assert.That(copy.Assembly!.Parts[0].MedicalFirmware!.Value.Id, Is.EqualTo("NCMedicalFirmwareInjector"));
            Assert.That(copy.Assembly.Parts[0].EntityPrototype, Is.EqualTo("NCMedicalControllerPremium"));
        });
        var clone = NCMedicalImplantMath.Clone(copy.MedicalImplant!);
        clone.Delivered = 0;
        Assert.That(copy.MedicalImplant!.Delivered, Is.EqualTo(8));
        Assert.That(clone.Cartridge, Is.Not.SameAs(copy.MedicalImplant.Cartridge));
    }
}
