// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.IO;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Prototypes;
using Content.Shared._NC.City.Zones.Serialization;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using YamlDotNet.RepresentationModel;

namespace Content.Tests.Shared._NC.City;

[TestFixture]
[TestOf(typeof(NCZoneDefinition))]
public sealed class NCZoneParentSerializationTest : ContentUnitTest
{
    [OneTimeSetUp]
    public void InitializeSerialization() => IoCManager.Resolve<ISerializationManager>().Initialize();

    [TestCase(false)]
    [TestCase(true)]
    public void RootParentRemainsEmptyThroughGeneratedDefaultAndExplicitRoundTrip(bool explicitParent)
    {
        var serialization = IoCManager.Resolve<ISerializationManager>();
        var id = Guid.NewGuid();
        var source = serialization.Read<NCZoneDefinition>(Node(id.ToString("D"), explicitParent ? Guid.Empty.ToString("D") : null));
        Assert.That(source.Id.Value, Is.EqualTo(id));
        Assert.That(source.Parent, Is.EqualTo(default(NCZoneId)));
        var written = (MappingDataNode) serialization.WriteValue(source, alwaysWrite: true, notNullableOverride: true);
        var parent = written.Get<ValueDataNode>("parent");
        Assert.That(serialization.ValidateNode<NCZoneId, ValueDataNode, NCZoneParentIdSerializer>(parent).Valid, Is.True,
            "The generated default parent node is a valid root sentinel.");
        Assert.That(serialization.ValidateNode<NCZoneId, ValueDataNode, NCZoneIdSerializer>(parent).Valid, Is.False,
            "The root sentinel must remain invalid wherever a required zone identity is expected.");
        var restored = serialization.Read<NCZoneDefinition>(written, notNullableOverride: true);
        Assert.That(restored.Id, Is.EqualTo(source.Id));
        Assert.That(restored.Parent, Is.EqualTo(default(NCZoneId)));
    }

    [Test]
    public void NonRootParentRetainsItsExactIdentity()
    {
        var serialization = IoCManager.Resolve<ISerializationManager>();
        var parent = Guid.NewGuid();
        var source = serialization.Read<NCZoneDefinition>(Node(Guid.NewGuid().ToString("D"), parent.ToString("D")));
        var written = serialization.WriteValue(source, alwaysWrite: true, notNullableOverride: true);
        Assert.That(serialization.Read<NCZoneDefinition>(written, notNullableOverride: true).Parent.Value, Is.EqualTo(parent));
    }

    [Test]
    public void EmptyRequiredIdAndMalformedParentAreRejected()
    {
        var serialization = IoCManager.Resolve<ISerializationManager>();
        Assert.Catch(() => serialization.Read<NCZoneDefinition>(Node(Guid.Empty.ToString("D"), null)));
        Assert.Catch(() => serialization.Read<NCZoneId>(new ValueDataNode(Guid.Empty.ToString("D"))));
        Assert.Catch(() => serialization.Read<NCZoneDefinition>(Node(Guid.NewGuid().ToString("D"), "not-a-guid")));
        Assert.That(NCZoneId.TryParse(Guid.Empty.ToString("D"), out _), Is.False);
    }

    private static MappingDataNode Node(string id, string parent)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader($"id: {id}\nkind: District\nname: Serialization fixture\n" +
            (parent == null ? "" : $"parent: {parent}\n")));
        return new((YamlMappingNode) yaml.Documents[0].RootNode);
    }
}
