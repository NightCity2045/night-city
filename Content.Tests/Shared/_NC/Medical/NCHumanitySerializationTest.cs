// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.IO;
using Content.Shared._NC.Medical.Humanity;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using YamlDotNet.RepresentationModel;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCHumanitySerializationTest : ContentUnitTest
{
    [OneTimeSetUp]
    public void InitializeSerialization() => IoCManager.Resolve<ISerializationManager>().Initialize();

    [Test]
    public void RequiredStateFieldsRoundTripIncludingZeroesAndRemainingCooldowns()
    {
        var serialization = IoCManager.Resolve<ISerializationManager>();
        var source = new NCHumanityStateSnapshot { Current = 0, Stress = 0, StabilizingSeconds = 12 };
        source.Adaptation["motor:left"] = 6;
        source.RecentImpulses.Add(new() { Source = Guid.NewGuid(), Profile = "NCHumanityNeuralTrauma", RemainingSeconds = 9 });
        var node = serialization.WriteValue(source, alwaysWrite: true, notNullableOverride: true);
        var restored = serialization.Read<NCHumanityStateSnapshot>(node, notNullableOverride: true);
        Assert.That(restored.Current, Is.Zero);
        Assert.That(restored.Stress, Is.Zero);
        Assert.That(restored.Adaptation["motor:left"], Is.EqualTo(6));
        Assert.That(restored.StabilizingSeconds, Is.EqualTo(12));
        Assert.That(restored.RecentImpulses[0].RemainingSeconds, Is.EqualTo(9));
    }

    [Test]
    public void MissingCurrentCannotSilentlyInitializeAnExistingSaveToOneHundred()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader("burden: 0\nstress: 0\nstage: Stable\nstabilizingSeconds: 0\nadaptation: {}\nrecentImpulses: []\n"));
        var node = new MappingDataNode((YamlMappingNode) yaml.Documents[0].RootNode);
        var serialization = IoCManager.Resolve<ISerializationManager>();
        Assert.Catch(() => serialization.Read<NCHumanityStateSnapshot>(node, notNullableOverride: true));
    }
}
