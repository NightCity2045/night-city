// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Neural;
using NUnit.Framework;

namespace Content.Tests.Server._NC.LivingCity.Neural;

[TestFixture]
public sealed class NCNeuralConversationSystemTest
{
    private static readonly string[] Markers = ["убью", "пристрелю", "kill you", "shoot you"];

    [TestCase("Если не пойдёшь, я убью тебя", true)]
    [TestCase("Я тебя пристрелю", true)]
    [TestCase("I will kill you", true)]
    [TestCase("Куда убегаешь?", false)]
    [TestCase("Я ранил Приву", false)]
    public void VerbalThreatMarkersAreMatchedWithoutChangingNeutralDialogue(string utterance, bool expected)
    {
        Assert.That(NCNeuralConversationSystem.ContainsVerbalThreat(utterance, Markers), Is.EqualTo(expected));
    }
}
