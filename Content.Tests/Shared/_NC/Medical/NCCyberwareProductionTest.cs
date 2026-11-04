// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Cyberware;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Serialization.Manager;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCCyberwareProductionTest : ContentUnitTest
{
    [OneTimeSetUp]
    public void InitializeSerialization() => IoCManager.Resolve<ISerializationManager>().Initialize();

    [TestCase(1f, 20f, 0f, true)]
    [TestCase(.5f, 20f, 0f, false)]
    [TestCase(1f, 10f, 0f, false)]
    [TestCase(1f, 20f, 70f, false)]
    [TestCase(float.NaN, 20f, 0f, false)]
    public void BetaTestRequiresHealthyPartsRatedSupplyAndThermalHeadroom(float efficiency, float rating, float heat, bool expected)
    {
        Assert.That(NCCyberwareProductionMath.CanTest(efficiency, .9f, 13f, rating, heat, 2.15f, .4f, 12f, 80f), Is.EqualTo(expected));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FactoryPreparationPersistsSeparatelyFromPatientCalibration(bool prepared)
    {
        var serializer = IoCManager.Resolve<ISerializationManager>();
        var source = new NCCyberwareStateSnapshot { Profile = "NCImplantedLinearFrameBeta", FactoryPrepared = prepared,
            Calibrated = false, SkeletalSurgeryCompleted = NCSkeletalSurgeryStage.None };
        var data = serializer.WriteValue(source, alwaysWrite:true, notNullableOverride:true);
        var restored = serializer.Read<NCCyberwareStateSnapshot>(data, notNullableOverride:true);
        Assert.That(restored.FactoryPrepared, Is.EqualTo(prepared));
        Assert.That(restored.Calibrated, Is.False);
        Assert.That(restored.SkeletalSurgeryCompleted, Is.EqualTo(NCSkeletalSurgeryStage.None));
    }

    [Test]
    public void TimedPreparationCopiesItsExpectedRevision()
    {
        var source = new NCCyberwarePreparationDoAfterEvent { ServiceRevision = 17 };
        var clone = (NCCyberwarePreparationDoAfterEvent) source.Clone();
        clone.ServiceRevision++;
        Assert.That(source.ServiceRevision, Is.EqualTo(17));
    }
}
