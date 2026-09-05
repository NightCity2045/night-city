// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Neural;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Physiology;
using NUnit.Framework;

namespace Content.Tests.Server._NC.LivingCity.Neural;

[TestFixture]
public sealed class NCNeuralEmbodimentContextSystemTest
{
    [TestCase(NCPainLevel.None, "none")]
    [TestCase(NCPainLevel.Mild, "mild")]
    [TestCase(NCPainLevel.Moderate, "moderate")]
    [TestCase(NCPainLevel.Severe, "severe")]
    [TestCase(NCPainLevel.Excruciating, "excruciating")]
    [TestCase(NCPainLevel.Overwhelming, "overwhelming")]
    public void NcPainUsesAuthoritativePhysiology(NCPainLevel level, string expected)
    {
        Assert.That(NCNeuralEmbodimentContextSystem.PainStateKey(level), Is.EqualTo(expected));
    }

    [Test]
    public void NcBleedingUsesAuthoritativeSourcesAndAggregates()
    {
        var bleeding = new NCBleedingComponent();
        Assert.That(NCNeuralEmbodimentContextSystem.HasActiveBleeding(bleeding), Is.False);

        bleeding.Sources.Add(new NCBleedingSourceRecord { CurrentRate = 0.25f });
        Assert.That(NCNeuralEmbodimentContextSystem.HasActiveBleeding(bleeding), Is.True);

        bleeding.Sources.Clear();
        bleeding.OverflowInternalRate = 0.1f;
        Assert.That(NCNeuralEmbodimentContextSystem.HasActiveBleeding(bleeding), Is.True);
    }
}
