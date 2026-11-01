// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Cyberware;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Serialization.Manager;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCCyberwareAbilitySerializationTest : ContentUnitTest
{
    [OneTimeSetUp]
    public void InitializeSerialization() => IoCManager.Resolve<ISerializationManager>().Initialize();

    [TestCase(0f)]
    [TestCase(23.5f)]
    public void RemainingCooldownRoundTripsWithoutRuntimeEntityOrClock(float remaining)
    {
        var source = new NCCyberwareStateSnapshot { Profile = "NCSandevistan", AbilityCooldownSeconds = remaining };
        var serialization = IoCManager.Resolve<ISerializationManager>();
        var node = serialization.WriteValue(source, alwaysWrite: true, notNullableOverride: true);
        var restored = serialization.Read<NCCyberwareStateSnapshot>(node, notNullableOverride: true);
        Assert.That(restored.AbilityCooldownSeconds, Is.EqualTo(remaining));
        Assert.That(restored.Profile, Is.EqualTo(source.Profile));
    }

    [Test]
    public void OrdinaryHardwareKeepsAnAbsentAbilityState()
    {
        var source = new NCCyberwareStateSnapshot { Profile = "NCCyberHeartMk1" };
        var serialization = IoCManager.Resolve<ISerializationManager>();
        var node = serialization.WriteValue(source, alwaysWrite: true, notNullableOverride: true);
        Assert.That(serialization.Read<NCCyberwareStateSnapshot>(node, notNullableOverride: true).AbilityCooldownSeconds, Is.Null);
    }
}
