// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client._NC.Medical.Wounds;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Function;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Random;
using Robust.Shared.Containers;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Maths;
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
    private static readonly ProtoId<NCTargetZonePrototype> HeadZone = "NCZoneHead";
    private static readonly ProtoId<NCTargetZonePrototype> EyesZone = "NCZoneEyes";
    private static readonly ProtoId<NCTargetZonePrototype> MouthZone = "NCZoneMouth";
    private static readonly ProtoId<NCTargetZonePrototype> GroinZone = "NCZoneGroin";
    private static readonly ProtoId<NCTargetZonePrototype> LeftLegZone = "NCZoneLegLeft";
    private static readonly ProtoId<NCTargetZonePrototype> RightLegZone = "NCZoneLegRight";

    private static readonly HashSet<string> ExpectedZones =
    [
        "NCZoneHead", "NCZoneEyes", "NCZoneMouth", "NCZoneChest", "NCZoneAbdomen", "NCZoneGroin",
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
    public void HumanProfileHasFourteenDataDrivenZonesAndDoll()
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
        var hitBounds = new List<(string Zone, Vector2 Position, Vector2 Size)>();
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
                Assert.That(zone.HitPriority, Is.GreaterThanOrEqualTo(0));
                Assert.That(float.IsFinite(zone.DownedWeightMultiplier), Is.True);
                Assert.That(zone.DownedWeightMultiplier, Is.GreaterThan(0f));
                Assert.That(zone.HitBounds, Is.Not.Null, $"Zone {zone.ID} has no physical hit bounds.");
            });

            var bounds = zone.HitBounds!;
            Assert.Multiple(() =>
            {
                Assert.That(bounds.Position.X, Is.InRange(0f, 1f), zone.ID);
                Assert.That(bounds.Position.Y, Is.InRange(0f, 1f), zone.ID);
                Assert.That(bounds.Size.X, Is.GreaterThan(0f), zone.ID);
                Assert.That(bounds.Size.Y, Is.GreaterThan(0f), zone.ID);
                Assert.That(bounds.Position.X + bounds.Size.X, Is.LessThanOrEqualTo(1f), zone.ID);
                Assert.That(bounds.Position.Y + bounds.Size.Y, Is.LessThanOrEqualTo(1f), zone.ID);
                Assert.That(zone.DirectionWeightMultipliers.Values.All(value =>
                    float.IsFinite(value) && value > 0f), Is.True, zone.ID);
            });
            hitBounds.Add((zone.ID, bounds.Position, bounds.Size));
        }

        for (var left = 0; left < hitBounds.Count; left++)
        {
            for (var right = left + 1; right < hitBounds.Count; right++)
            {
                var a = hitBounds[left];
                var b = hitBounds[right];
                const float epsilon = 0.00001f;
                var overlapWidth = MathF.Min(a.Position.X + a.Size.X, b.Position.X + b.Size.X) -
                                   MathF.Max(a.Position.X, b.Position.X);
                var overlapHeight = MathF.Min(a.Position.Y + a.Size.Y, b.Position.Y + b.Size.Y) -
                                    MathF.Max(a.Position.Y, b.Position.Y);
                var overlaps = overlapWidth > epsilon && overlapHeight > epsilon;
                if (!overlaps)
                    continue;

                var aPriority = _prototypes.Index<NCTargetZonePrototype>(a.Zone).HitPriority;
                var bPriority = _prototypes.Index<NCTargetZonePrototype>(b.Zone).HitPriority;
                Assert.That(aPriority, Is.Not.EqualTo(bPriority),
                    $"Physical hit bounds overlap without an explicit priority: {a.Zone} and {b.Zone}.");
            }
        }

        Assert.That(totalWeight, Is.EqualTo(100f).Within(0.001f));
        Assert.That(doll.StatusLayers.Single(layer => layer.Id == "torso").SourceZones.Select(zone => zone.Id),
            Is.EquivalentTo(new[] { "NCZoneChest", "NCZoneAbdomen" }));
        Assert.That(doll.StatusLayers.Single(layer => layer.Id == "head").SourceZones.Select(zone => zone.Id),
            Is.EquivalentTo(new[] { "NCZoneHead", "NCZoneEyes", "NCZoneMouth" }));
        Assert.Multiple(() =>
        {
            Assert.That(_prototypes.Index(EyesZone).HitPriority,
                Is.GreaterThan(_prototypes.Index(HeadZone).HitPriority));
            Assert.That(_prototypes.Index(MouthZone).HitPriority,
                Is.GreaterThan(_prototypes.Index(HeadZone).HitPriority));
            Assert.That(_prototypes.Index(GroinZone).HitPriority,
                Is.GreaterThan(_prototypes.Index(LeftLegZone).HitPriority));
            Assert.That(_prototypes.Index(GroinZone).HitPriority,
                Is.GreaterThan(_prototypes.Index(RightLegZone).HitPriority));
        });

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
                Assert.That(float.IsFinite(presentation.Position.X) && float.IsFinite(presentation.Position.Y),
                    Is.True, $"Zone {presentation.Zone} has a non-finite overlay position.");
                Assert.That(presentation.HitboxPadding.X, Is.GreaterThanOrEqualTo(0f), presentation.Zone.Id);
                Assert.That(presentation.HitboxPadding.Y, Is.GreaterThanOrEqualTo(0f), presentation.Zone.Id);
                Assert.That(presentation.WorldVisualLayers, Is.Not.Empty,
                    $"Zone {presentation.Zone} has no directional humanoid silhouette layers.");
                Assert.That(presentation.WorldVisualLayers, Is.Unique,
                    $"Zone {presentation.Zone} repeats a directional humanoid silhouette layer.");
                if (presentation.DisplaySize is { } displaySize)
                {
                    Assert.That(displaySize.X, Is.GreaterThan(0f), presentation.Zone.Id);
                    Assert.That(displaySize.Y, Is.GreaterThan(0f), presentation.Zone.Id);
                    Assert.That(presentation.Position.X + displaySize.X, Is.LessThanOrEqualTo(doll.CanvasSize.X),
                        presentation.Zone.Id);
                    Assert.That(presentation.Position.Y + displaySize.Y, Is.LessThanOrEqualTo(doll.CanvasSize.Y),
                        presentation.Zone.Id);
                }
            });
        }

        var expectedLimbGeometry = new Dictionary<string, (Vector2 Position, Vector2 Size)>
        {
            ["NCZoneArmRight"] = (new Vector2(7, 10), new Vector2(5, 6)),
            ["NCZoneHandRight"] = (new Vector2(7, 16), new Vector2(4, 4)),
            ["NCZoneArmLeft"] = (new Vector2(19, 10), new Vector2(5, 6)),
            ["NCZoneHandLeft"] = (new Vector2(20, 16), new Vector2(4, 4)),
            ["NCZoneLegRight"] = (new Vector2(11, 20), new Vector2(4, 6)),
            ["NCZoneFootRight"] = (new Vector2(9, 26), new Vector2(6, 3)),
            ["NCZoneLegLeft"] = (new Vector2(16, 20), new Vector2(4, 6)),
            ["NCZoneFootLeft"] = (new Vector2(16, 26), new Vector2(6, 3)),
        };
        foreach (var (zone, expected) in expectedLimbGeometry)
        {
            var presentation = doll.Zones.Single(entry => entry.Zone.Id == zone);
            Assert.Multiple(() =>
            {
                Assert.That(presentation.Position, Is.EqualTo(expected.Position), zone);
                Assert.That(presentation.DisplaySize, Is.EqualTo(expected.Size), zone);
            });
        }

        var eyesPresentation = doll.Zones.Single(entry => entry.Zone == EyesZone);
        var mouthPresentation = doll.Zones.Single(entry => entry.Zone == MouthZone);
        var groinPresentation = doll.Zones.Single(entry => entry.Zone == GroinZone);
        var groinInteractionRegion = groinPresentation.InteractionRegion!;
        var groinWoundRegion = groinPresentation.WoundRegion!;
        var groinWounds = UIBox2.FromDimensions(
            groinPresentation.Position + groinWoundRegion.Position,
            groinWoundRegion.Size);
        Assert.Multiple(() =>
        {
            Assert.That(eyesPresentation.Position, Is.EqualTo(new Vector2(13f, 5f)));
            Assert.That(mouthPresentation.Position, Is.EqualTo(new Vector2(14f, 7f)));
            Assert.That(groinPresentation.Position, Is.EqualTo(new Vector2(11f, 17f)));
            Assert.That(groinPresentation.Position.Y + groinInteractionRegion.Size.Y,
                Is.LessThanOrEqualTo(expectedLimbGeometry["NCZoneLegLeft"].Position.Y),
                "The groin interaction region must not steal actual left-leg pixels.");
            Assert.That(groinPresentation.Position.Y + groinInteractionRegion.Size.Y,
                Is.LessThanOrEqualTo(expectedLimbGeometry["NCZoneLegRight"].Position.Y),
                "The groin interaction region must not steal actual right-leg pixels.");
            Assert.That(groinWounds, Is.EqualTo(UIBox2.FromDimensions(
                new Vector2(11f, 17f),
                new Vector2(9f, 5f))));
        });

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
            Assert.That(
                Client.EntMan.GetComponent<NCVisibleWoundComponent>(clientBody).Markers,
                Is.EqualTo(Server.EntMan.GetComponent<NCVisibleWoundComponent>(serverBody).Markers));
        });

        await Server.WaitPost(() =>
        {
            var first = new NCCombatTraumaRequestedEvent(CreateRequest(
                serverBody,
                new Vector2(-0.25f, 0.25f),
                Vector2.UnitX));
            var second = new NCCombatTraumaRequestedEvent(CreateRequest(
                serverBody,
                new Vector2(0.25f, -0.25f),
                Vector2.UnitY));
            Server.EntMan.EventBus.RaiseLocalEvent(serverBody, ref first);
            Server.EntMan.EventBus.RaiseLocalEvent(serverBody, ref second);
            Assert.That(first.Applied, Is.True);
            Assert.That(second.Applied, Is.True);
        });
        await Pair.RunTicksSync(5);

        Assert.Multiple(() =>
        {
            var serverMarkers = Server.EntMan.GetComponent<NCVisibleWoundComponent>(serverBody).Markers;
            var clientMarkers = Client.EntMan.GetComponent<NCVisibleWoundComponent>(clientBody).Markers;
            // Connected test maps can deliver a real incidental physics impact while ticks advance. This
            // assertion addresses the two ballistic mutations owned by this test without masking replication
            // of any independently authoritative wound.
            var legMarkers = clientMarkers.Where(marker =>
                marker.Zone.Id == "NCZoneLegRight" && marker.Type == NCWoundType.Ballistic).ToList();
            var overlaySystem = Client.EntMan.System<NCWorldWoundOverlaySystem>();
            Assert.That(clientMarkers, Is.EqualTo(serverMarkers));
            Assert.That(legMarkers, Has.Count.EqualTo(2));
            Assert.That(legMarkers.Select(marker => (marker.VisualX, marker.VisualY)), Is.Unique);
            Assert.That(overlaySystem.IsIndexed(clientBody), Is.True,
                "A client body with replicated wound markers must be scheduled for world rendering.");
            var legBounds = Box2.FromDimensions(new Vector2(11f, 20f), new Vector2(4f, 6f));
            Assert.That(legMarkers.All(marker =>
            {
                var point = NCWoundMarkerProjection.ResolveBasePoint(marker, legBounds);
                return point.X >= legBounds.Left && point.X < legBounds.Right &&
                       point.Y >= legBounds.Bottom && point.Y < legBounds.Top;
            }), Is.True, "Every replicated wound must project inside its configured sprite zone.");
        });

        EntityUid scanner = default;
        await Server.WaitPost(() =>
        {
            scanner = Server.EntMan.SpawnAtPosition("MedicalScanner", map.GridCoords);
            var containers = Server.EntMan.System<SharedContainerSystem>();
            var bodySlot = containers.GetContainer(scanner, "scanner-bodyContainer");
            Assert.That(containers.Insert(serverBody, bodySlot, force: true), Is.True);
        });
        await Pair.RunTicksSync(5);

        Assert.That(
            Client.EntMan.System<NCWorldWoundOverlaySystem>().ShouldRenderWorldWounds(clientBody),
            Is.False,
            "World wound pixels must disappear with the contained humanoid sprite.");

        await Server.WaitPost(() =>
        {
            var containers = Server.EntMan.System<SharedContainerSystem>();
            var bodySlot = containers.GetContainer(scanner, "scanner-bodyContainer");
            Assert.That(
                containers.Remove(serverBody, bodySlot, force: true, destination: map.GridCoords),
                Is.True);
        });
        await Pair.RunTicksSync(5);

        Assert.That(
            Client.EntMan.System<NCWorldWoundOverlaySystem>().ShouldRenderWorldWounds(clientBody),
            Is.True,
            "World wound pixels must return after the body leaves the container.");

        await Server.WaitPost(() => Server.EntMan.DeleteEntity(map.MapUid));
    }

    private static NCCombatTraumaRequest CreateRequest(
        EntityUid body,
        Vector2 localHitPosition,
        Vector2 impactDirection)
    {
        return new NCCombatTraumaRequest(
            NCActionResolutionId.New(),
            1,
            1,
            body,
            null,
            null,
            null,
            "NCZoneLegRight",
            "LegRight",
            "BallisticProjectile",
            new DamageSpecifier(),
            FixedPoint2.New(9f),
            FixedPoint2.Zero,
            FixedPoint2.New(9f),
            FixedPoint2.Zero,
            localHitPosition,
            impactDirection,
            NCTraumaFlags.ArmorPenetrated,
            0.1f,
            TimeSpan.Zero);
    }
}
