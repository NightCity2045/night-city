// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Preferences;
using Content.Shared.Preferences;
using NUnit.Framework;

namespace Content.IntegrationTests.Tests._NC.Preferences;

[TestFixture]
public sealed class NCHumanProfileRulesTest
{
    [Test]
    public void NonHumanPlayerProfileIsNormalizedToHuman()
    {
        var supplied = new HumanoidCharacterProfile().WithSpecies("Reptilian");

        var normalized = NCHumanProfileRules.Normalize(supplied);

        Assert.That(normalized.Species, Is.EqualTo(HumanoidCharacterProfile.DefaultSpecies));
        Assert.That(supplied.Species, Is.Not.EqualTo(HumanoidCharacterProfile.DefaultSpecies),
            "Normalization must not mutate the profile supplied by the client.");
    }

    [Test]
    public void HumanPlayerProfileIsKeptWithoutAllocation()
    {
        var supplied = new HumanoidCharacterProfile();

        Assert.That(NCHumanProfileRules.Normalize(supplied), Is.SameAs(supplied));
    }
}
