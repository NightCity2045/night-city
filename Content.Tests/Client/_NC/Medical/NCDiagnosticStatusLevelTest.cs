// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client._NC.Medical.Diagnostics;
using Content.Shared._NC.Medical.Diagnostics.Detail;
using NUnit.Framework;

namespace Content.Tests.Client._NC.Medical;

/// <summary>
/// Protects the diagnostic doll from displaying healthy regions as critically damaged.
/// </summary>
[TestFixture]
[TestOf(typeof(NCDiagnosticWindow))]
public sealed class NCDiagnosticStatusLevelTest
{
    [TestCase(NCDiagnosticBand.None, (byte) 0)]
    [TestCase(NCDiagnosticBand.Low, (byte) 2)]
    [TestCase(NCDiagnosticBand.Moderate, (byte) 4)]
    [TestCase(NCDiagnosticBand.High, (byte) 6)]
    [TestCase(NCDiagnosticBand.Critical, (byte) 8)]
    public void ImpairmentBandMapsDirectlyToStatusFrame(NCDiagnosticBand band, byte expected)
    {
        Assert.That(NCDiagnosticWindow.DiagnosticStatusLevel(band), Is.EqualTo(expected));
    }
}
