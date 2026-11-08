// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Cyberware;
using System.Linq;
using Content.Shared._NC.Medical.Cyberware.Arms;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Serialization.Manager;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCArmHardwareTest : ContentUnitTest
{
    [OneTimeSetUp]
    public void InitializeSerialization() => IoCManager.Resolve<ISerializationManager>().Initialize();

    [TestCase(0, 4, true)] // A launcher consumes the entire arm.
    [TestCase(1, 3, true)] // Mantis blades plus a hand module.
    [TestCase(2, 3, false)]
    [TestCase(4, 0, true)] // A wrist connector consumes no module volume.
    [TestCase(-1, 1, false)]
    [TestCase(0, float.NaN, false)]
    [TestCase(float.PositiveInfinity, 1, false)]
    public void SharedArmVolumeIsBounded(float used, float added, bool fits)
        => Assert.That(NCArmHardwareMath.Fits(4, used, added), Is.EqualTo(fits));

    [Test]
    public void AmmunitionAndPocketPayloadsSurviveMedicalSerialization()
    {
        var source = new NCCyberwareStateSnapshot
        {
            Profile = "NCArmPistol",
            ArmContents = [new() { Slot = "integral", Yaml = "entities:\n- uid: 1\n" }],
        };
        var manager = IoCManager.Resolve<ISerializationManager>();
        var restored = manager.Read<NCCyberwareStateSnapshot>(manager.WriteValue(source, alwaysWrite: true));
        Assert.That(restored.ArmContents.Single().Yaml, Is.EqualTo(source.ArmContents.Single().Yaml));
        Assert.That(restored.ArmContents.Single().Slot, Is.EqualTo("integral"));
        Assert.That(new NCCyberwareStateSnapshot().ArmContents, Is.Empty);
    }
}
