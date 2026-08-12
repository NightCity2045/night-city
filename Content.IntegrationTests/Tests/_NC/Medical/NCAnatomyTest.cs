// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Function;
using Content.Shared.Body;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Random;
using Robust.Shared.Containers;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCAnatomySystem))]
public sealed class NCAnatomyTest : GameTest
{
    private static readonly ProtoId<NCAnatomyProfilePrototype> HumanAnatomy = "Human";
    private static readonly EntProtoId HumanMob = "MobHuman";
    private static readonly EntProtoId DwarfMob = "MobDwarf";
    private static readonly ProtoId<WeightedRandomSpeciesPrototype> SpeciesWeights = "SpeciesWeights";

    private static readonly HashSet<string> ExpectedZones =
    [
        "NCZoneHead", "NCZoneChest", "NCZoneAbdomen", "NCZoneGroin",
        "NCZoneArmLeft", "NCZoneArmRight", "NCZoneHandLeft", "NCZoneHandRight",
        "NCZoneLegLeft", "NCZoneLegRight", "NCZoneFootLeft", "NCZoneFootRight",
    ];

    private static readonly HashSet<string> ExpectedOrganCategories =
    [
        "Torso", "Head", "ArmLeft", "ArmRight", "HandLeft", "HandRight",
        "LegLeft", "LegRight", "FootLeft", "FootRight",
        "Thorax", "Abdomen", "PelvisGroin", "Skull", "RibCage", "PelvisBone",
        "ArmBoneLeft", "ArmBoneRight", "TendonSetLeftArm", "TendonSetRightArm",
        "LegBoneLeft", "LegBoneRight", "TendonSetLeftLeg", "TendonSetRightLeg",
        "Brain", "Eyes", "Tongue", "Appendix", "Ears", "Lungs", "Heart",
        "Stomach", "Liver", "Kidneys",
    ];

    [SidedDependency(Side.Server)] private IComponentFactory _components = default!;
    [SidedDependency(Side.Server)] private ILocalizationManager _localization = default!;
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;
    [SidedDependency(Side.Server)] private IResourceManager _resources = default!;
    [SidedDependency(Side.Server)] private SharedContainerSystem _containers = default!;
    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void HumanProfileHasTwelveDataDrivenZonesAndDoll()
    {
        var profile = _prototypes.Index(HumanAnatomy);
        var doll = _prototypes.Index(profile.TargetDoll);

        Assert.Multiple(() =>
        {
            Assert.That(profile.TargetZones.Select(zone => zone.Id), Is.EquivalentTo(ExpectedZones));
            Assert.That(profile.TargetZones, Is.Unique);
            Assert.That(profile.AutomaticFallbackZone.Id, Is.EqualTo("NCZoneChest"));
            Assert.That(doll.CanvasSize.X, Is.GreaterThan(0f));
            Assert.That(doll.CanvasSize.Y, Is.GreaterThan(0f));
            Assert.That(doll.Zones.Select(entry => entry.Zone), Is.EquivalentTo(profile.TargetZones));
            Assert.That(doll.Zones.Select(entry => entry.Zone), Is.Unique);
            Assert.That(doll.StatusLayers, Has.Count.EqualTo(11));
            Assert.That(doll.StatusLayers.Select(layer => layer.Id), Is.Unique);
            Assert.That(doll.StatusLayers.SelectMany(layer => layer.SourceZones), Is.EquivalentTo(profile.TargetZones));
        });

        var totalWeight = 0f;
        foreach (var zoneId in profile.TargetZones)
        {
            var zone = _prototypes.Index(zoneId);
            totalWeight += zone.AutomaticWeight;
            Assert.Multiple(() =>
            {
                Assert.That(_localization.HasString(zone.DisplayName), Is.True,
                    $"Zone {zone.ID} is missing localization {zone.DisplayName}.");
                Assert.That(float.IsFinite(zone.AutomaticWeight), Is.True);
                Assert.That(zone.AutomaticWeight, Is.GreaterThan(0f));
                Assert.That(zone.Selectable, Is.True);
                Assert.That(zone.StandardCombatAllowed, Is.True);
            });
        }

        Assert.That(totalWeight, Is.EqualTo(100f).Within(0.001f));
        Assert.That(doll.StatusLayers.Single(layer => layer.Id == "torso").SourceZones.Select(zone => zone.Id),
            Is.EquivalentTo(new[] { "NCZoneChest", "NCZoneAbdomen" }));

        foreach (var layer in doll.StatusLayers)
        {
            Assert.That(layer.SourceZones, Is.Not.Empty, $"Status layer {layer.Id} has no source zones.");
            Assert.That(layer.SourceZones, Is.SubsetOf(profile.TargetZones),
                $"Status layer {layer.Id} references a zone outside the human anatomy profile.");
            Assert.That(_resources.ContentFileExists(layer.Sprite / "meta.json"), Is.True,
                $"Status layer {layer.Id} has no RSI metadata at {layer.Sprite}.");

            for (var level = 0; level <= 8; level++)
            {
                Assert.That(_resources.ContentFileExists(layer.Sprite / $"{layer.StatePrefix}_{level}.png"), Is.True,
                    $"Status layer {layer.Id} is missing severity state {level}.");
            }
        }

        foreach (var presentation in doll.Zones)
        {
            Assert.Multiple(() =>
            {
                Assert.That(_resources.ContentFileExists(presentation.SelectedTexture), Is.True,
                    $"Zone {presentation.Zone} is missing selected overlay {presentation.SelectedTexture}.");
                Assert.That(_resources.ContentFileExists(presentation.HoverTexture), Is.True,
                    $"Zone {presentation.Zone} is missing hover overlay {presentation.HoverTexture}.");
            });
        }

        AssertFallbackGraphIsAcyclic(profile);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void HumanProfileMatchesExpandedLiveOrganGraph()
    {
        var human = _prototypes.Index<EntityPrototype>(HumanMob);
        Assert.That(human.TryComp<NCMedicalBodyComponent>(out var medical, _components), Is.True);
        Assert.That(human.TryComp<BodyComponent>(out _, _components), Is.True);
        Assert.That(human.TryComp<InitialBodyComponent>(out var initial, _components), Is.True);
        Assert.That(medical!.Profile, Is.EqualTo(HumanAnatomy));

        var profile = _prototypes.Index(medical.Profile);
        var organs = initial!.Organs;
        Assert.That(organs, Has.Count.EqualTo(34));
        Assert.That(organs.Keys.Select(category => category.Id), Is.EquivalentTo(ExpectedOrganCategories));

        var actualCategories = new HashSet<ProtoId<OrganCategoryPrototype>>();
        foreach (var (declaredCategory, organId) in organs)
        {
            Assert.That(actualCategories.Add(declaredCategory), Is.True,
                $"Human body declares organ category {declaredCategory} more than once.");

            var organPrototype = _prototypes.Index(organId);
            Assert.That(organPrototype.TryComp<OrganComponent>(out var organ, _components), Is.True);
            Assert.That(organ!.Category.HasValue, Is.True,
                $"InitialBody key {declaredCategory} points to {organId}, whose Organ category is missing.");
            Assert.That(organ.Category!.Value, Is.EqualTo(declaredCategory),
                $"InitialBody key {declaredCategory} points to {organId}, whose Organ category is {organ.Category}.");
            Assert.That(organPrototype.TryComp<NCOrganIntegrityComponent>(out _, _components), Is.True,
                $"Authoritative human organ {organId} has no NC integrity state.");
        }

        foreach (var zoneId in profile.TargetZones)
        {
            var zone = _prototypes.Index(zoneId);
            Assert.Multiple(() =>
            {
                Assert.That(organs.ContainsKey(zone.ExternalOrganCategory), Is.True,
                    $"Zone {zone.ID} references absent external category {zone.ExternalOrganCategory}.");
                Assert.That(organs.ContainsKey(zone.MedicalRegionCategory), Is.True,
                    $"Zone {zone.ID} references absent medical region {zone.MedicalRegionCategory}.");
            });
        }

        Assert.That(initial.Relationships, Is.Not.Null);
        var relationships = initial.Relationships!;
        var childCategories = new HashSet<ProtoId<OrganCategoryPrototype>>();
        foreach (var (parent, children) in relationships)
        {
            Assert.That(organs.ContainsKey(parent), Is.True,
                $"Human relationship parent {parent} has no organ prototype.");
            foreach (var child in children)
            {
                Assert.That(organs.ContainsKey(child), Is.True,
                    $"Human relationship child {child} has no organ prototype.");
                Assert.That(childCategories.Add(child), Is.True,
                    $"Human organ category {child} has more than one parent.");
            }
        }

        Assert.That(organs.Keys.Except(childCategories).Select(category => category.Id),
            Is.EqualTo(new[] { "Torso" }));
        AssertRelationshipGraphIsAcyclic(organs.Keys, relationships);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void NonHumanHumanoidDoesNotInheritAuthoritativeHumanMedicine()
    {
        var dwarf = _prototypes.Index<EntityPrototype>(DwarfMob);

        Assert.Multiple(() =>
        {
            Assert.That(dwarf.TryComp<NCMedicalBodyComponent>(out _, _components), Is.False);
            Assert.That(dwarf.TryComp<NCBodyFunctionComponent>(out _, _components), Is.False);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void HumanIsTheOnlyPlayerSelectableAndRandomSpecies()
    {
        var selectable = _prototypes.EnumeratePrototypes<SpeciesPrototype>()
            .Where(species => species.RoundStart)
            .Select(species => species.ID);
        var random = _prototypes.Index(SpeciesWeights);

        Assert.Multiple(() =>
        {
            Assert.That(selectable, Is.EquivalentTo(new[] { "Human" }));
            Assert.That(random.Weights.Keys, Is.EquivalentTo(new[] { "Human" }));
            Assert.That(random.Weights["Human"], Is.GreaterThan(0f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void HumanBodyMaterializesAndZoneResolutionIsReproducible()
    {
        var body = SSpawn(HumanMob);
        var medical = SComp<NCMedicalBodyComponent>(body);
        var initial = SComp<InitialBodyComponent>(body);
        var container = _containers.GetContainer(body, BodyComponent.ContainerID);
        var profile = _prototypes.Index(medical.Profile);

        Assert.That(container.ContainedEntities, Has.Count.EqualTo(initial.Organs.Count));
        foreach (var organUid in container.ContainedEntities)
            Assert.That(SComp<OrganComponent>(organUid).Body, Is.EqualTo(body));

        foreach (var zoneId in profile.TargetZones)
            Assert.That(_anatomy.TryGetZone((body, medical), zoneId, out _, out _), Is.True);

        for (ulong seed = 0; seed < 256; seed++)
        {
            Assert.That(_anatomy.TryResolveAutomaticZone((body, medical), seed, out var first, out _), Is.True);
            Assert.That(_anatomy.TryResolveAutomaticZone((body, medical), seed, out var second, out _), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(first, Is.EqualTo(second));
                Assert.That(profile.TargetZones, Does.Contain(first));
            });
        }

        Assert.That(
            _anatomy.TryResolveSelectedZone((body, medical), "NCZoneAbdomen", out var abdomen, out var region),
            Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(abdomen.Id, Is.EqualTo("NCZoneAbdomen"));
            Assert.That(SComp<OrganComponent>(region).Category!.Value.Id, Is.EqualTo("Abdomen"));
        });
    }

    private void AssertFallbackGraphIsAcyclic(NCAnatomyProfilePrototype profile)
    {
        foreach (var start in profile.TargetZones)
        {
            var visited = new HashSet<ProtoId<NCTargetZonePrototype>>();
            ProtoId<NCTargetZonePrototype>? current = start;
            while (current is { } zoneId)
            {
                Assert.That(visited.Add(zoneId), Is.True, $"Target-zone fallback cycle starts at {start}.");
                var zone = _prototypes.Index(zoneId);
                current = zone.FallbackZone;
            }
        }
    }

    private static void AssertRelationshipGraphIsAcyclic(
        IEnumerable<ProtoId<OrganCategoryPrototype>> categories,
        IReadOnlyDictionary<ProtoId<OrganCategoryPrototype>, HashSet<ProtoId<OrganCategoryPrototype>>> relationships)
    {
        var active = new HashSet<ProtoId<OrganCategoryPrototype>>();
        var complete = new HashSet<ProtoId<OrganCategoryPrototype>>();

        foreach (var category in categories)
            Visit(category, relationships, active, complete);
    }

    private static void Visit(
        ProtoId<OrganCategoryPrototype> category,
        IReadOnlyDictionary<ProtoId<OrganCategoryPrototype>, HashSet<ProtoId<OrganCategoryPrototype>>> relationships,
        HashSet<ProtoId<OrganCategoryPrototype>> active,
        HashSet<ProtoId<OrganCategoryPrototype>> complete)
    {
        if (complete.Contains(category))
            return;

        Assert.That(active.Add(category), Is.True, $"Human organ graph contains a cycle at {category}.");
        if (relationships.TryGetValue(category, out var children))
        {
            foreach (var child in children)
                Visit(child, relationships, active, complete);
        }

        active.Remove(category);
        complete.Add(category);
    }
}

[TestFixture]
[TestOf(typeof(NCMedicalBodyComponent))]
public sealed class NCMedicalBodyNetworkTest : GameTest
{
    [Test]
    public async Task HumanAuthoritativeProfileReplicatesToClient()
    {
        var map = await Pair.CreateTestMap();
        EntityUid serverBody = default;

        await Server.WaitPost(() =>
            serverBody = Server.EntMan.SpawnAtPosition("MobHuman", map.GridCoords));
        await Pair.RunTicksSync(5);

        var clientBody = Pair.ToClientUid(serverBody);
        Assert.Multiple(() =>
        {
            Assert.That(Server.EntMan.GetComponent<NCMedicalBodyComponent>(serverBody).Profile.Id,
                Is.EqualTo("Human"));
            Assert.That(Client.EntMan.GetComponent<NCMedicalBodyComponent>(clientBody).Profile.Id,
                Is.EqualTo("Human"));
        });

        await Server.WaitPost(() => Server.EntMan.DeleteEntity(map.MapUid));
    }
}
