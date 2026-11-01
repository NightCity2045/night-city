// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Cyberware.Borg;
using NUnit.Framework;

namespace Content.Tests.Server._NC.Medical;

[TestFixture]
public sealed class NCBorgThermalTest
{
    [TestCase(310.15f, 1000f, 0f, false, 0f, 310.15f)]
    [TestCase(310.15f, 10f, 0f, true, 0f, 310.35f)]
    [TestCase(310.15f, 10f, 10f, true, 0.1f, 310.15f)]
    [TestCase(318.15f, 100f, 100f, true, 1f, 316.15f)]
    [TestCase(310.15f, 100f, 100f, true, 100f, 310.15f)]
    public void HeatRequiresWorkAndFiniteCoolingRecoversTowardTheSetpoint(float initial, float elapsed,
        float powered, bool livingBrain, float coolant, float expected)
    {
        Assert.That(NCBorgThermalMath.Advance(initial, elapsed, powered, livingBrain, coolant),
            Is.EqualTo(expected).Within(0.001f));
    }
}
