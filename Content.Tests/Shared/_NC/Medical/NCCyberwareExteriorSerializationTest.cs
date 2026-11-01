// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Visuals;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Manager;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCCyberwareExteriorSerializationTest : ContentUnitTest
{
    [OneTimeSetUp]
    public void InitializeSerialization() => IoCManager.Resolve<ISerializationManager>().Initialize();

    [TestCase("NCFinishChrome", false)]
    [TestCase("NCFinishChrome", true)]
    [TestCase("NCFinishMedical", true)]
    [TestCase("NCFinishSyntheticSkin", false)]
    [TestCase("NCFinishSyntheticSkin", true)]
    public void FinishAndRuptureSurviveIndependentRoundTrip(string finish, bool ruptured)
    {
        var source = new NCCyberwareStateSnapshot
        {
            Profile = "NCIndustrialCyberArm",
            Exterior = new NCCyberwareExteriorSnapshot { Finish = finish, Ruptured = ruptured, SkinColor = Color.FromHex("#855F40") },
        };
        var serialization = IoCManager.Resolve<ISerializationManager>();
        var node = serialization.WriteValue(source, alwaysWrite: true, notNullableOverride: true);
        var restored = serialization.Read<NCCyberwareStateSnapshot>(node, notNullableOverride: true);
        Assert.That(restored.Exterior, Is.Not.Null);
        Assert.That(restored.Exterior!.Finish.Id, Is.EqualTo(finish));
        Assert.That(restored.Exterior.Ruptured, Is.EqualTo(ruptured));
        Assert.That(restored.Exterior.SkinColor, Is.EqualTo(source.Exterior.SkinColor));
        restored.Exterior.Ruptured = !ruptured;
        Assert.That(source.Exterior.Ruptured, Is.EqualTo(ruptured));
    }

    [Test]
    public void HiddenInternalHardwareDoesNotAcquireExteriorState()
    {
        var source = new NCCyberwareStateSnapshot { Profile = "NCCyberHeartMk1" };
        var serialization = IoCManager.Resolve<ISerializationManager>();
        var node = serialization.WriteValue(source, alwaysWrite: true, notNullableOverride: true);
        Assert.That(serialization.Read<NCCyberwareStateSnapshot>(node, notNullableOverride: true).Exterior, Is.Null);
    }
}
