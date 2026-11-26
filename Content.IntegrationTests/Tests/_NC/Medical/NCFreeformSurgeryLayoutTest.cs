// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared.Body;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>
/// Guards the data layout of the operative field: organs must stay large enough to click, and cyberware
/// slots must never lie on top of each other, which made some of them impossible to select.
/// </summary>
[TestFixture]
public sealed class NCFreeformSurgeryLayoutTest : GameTest
{
    private const float MinimumOrganSide = 0.12f;
    private const float MaximumSlotOverlap = 0.05f;

    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;
    [SidedDependency(Side.Server)] private IComponentFactory factory = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void StructuresAreClickableAndSlotsDoNotOverlap()
    {
        foreach (var profile in _prototypes.EnumeratePrototypes<NCFreeformSurgeryProfilePrototype>())
        {
            var slots = new List<(string Name, string Zone, Vector2 Position, Vector2 Size)>();
            foreach (var (category, structure) in profile.Structures)
            {
                var name = category.Id;
                if (name.StartsWith("NCSlot", StringComparison.Ordinal))
                {
                    foreach (var zone in structure.Zones)
                        slots.Add((name, zone.Id, structure.Position, structure.Size));
                    continue;
                }

                if (structure.Visual is NCFreeformSurgeryStructureVisual.Skull or NCFreeformSurgeryStructureVisual.RibCage
                    or NCFreeformSurgeryStructureVisual.Pelvis or NCFreeformSurgeryStructureVisual.LongBone
                    or NCFreeformSurgeryStructureVisual.Tendon)
                {
                    continue;
                }

                Assert.That(structure.Size.X, Is.GreaterThanOrEqualTo(MinimumOrganSide), $"{name} is too narrow to click");
                Assert.That(structure.Size.Y, Is.GreaterThanOrEqualTo(MinimumOrganSide), $"{name} is too short to click");
            }

            foreach (var a in slots)
            foreach (var b in slots)
            {
                if (string.CompareOrdinal(a.Name, b.Name) >= 0 || a.Zone != b.Zone)
                    continue;
                var overlap = Overlap(a.Position, a.Size, b.Position, b.Size);
                var smaller = MathF.Min(a.Size.X * a.Size.Y, b.Size.X * b.Size.Y);
                Assert.That(overlap / smaller, Is.LessThanOrEqualTo(MaximumSlotOverlap),
                    $"slots {a.Name} and {b.Name} overlap in {a.Zone}");
            }
        }
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void EyesAndEarsAreDisclosedInTheHeadWindowAsOverlays()
    {
        var profile = _prototypes.Index<NCFreeformSurgeryProfilePrototype>("NCHumanFreeformSurgery");
        var eyes = profile.Structures["Eyes"];
        var ears = profile.Structures["Ears"];
        Assert.Multiple(() =>
        {
            Assert.That(eyes.Zones.Select(zone => zone.Id), Does.Contain("NCZoneHead"));
            Assert.That(eyes.Layer, Is.EqualTo(NCFreeformSurgeryStructureLayer.Front), "eyes sit in the orbits");
            Assert.That(ears.Layer, Is.EqualTo(NCFreeformSurgeryStructureLayer.Behind), "ears stick out behind the skull");
            Assert.That(ears.Size.X, Is.GreaterThan(profile.Structures["Skull"].Size.X), "ears must protrude past the skull");
            Assert.That(eyes.MinimumDepth, Is.LessThanOrEqualTo(profile.Structures["Skull"].MinimumDepth + 0.001f),
                "eyes must stay reachable while the skull is closed");
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void EyesAreOnePairedOrganAndCyberopticsReplaceBothEyes()
    {
        var cyberEyes = _prototypes.Index<EntityPrototype>("NCCyberEyesStandard");
        Assert.That(cyberEyes.TryGetComponent<OrganComponent>(out var cyberOrgan, factory), Is.True);
        Assert.That(cyberOrgan!.Category?.Id, Is.EqualTo("Eyes"), "the paired implant takes the paired eye slot");
        Assert.That(cyberEyes.TryGetComponent<NCOrganFunctionComponent>(out var function, factory), Is.True);
        Assert.That(function!.BaseCapacity, Is.EqualTo(1f), "a pair of cybereyes gives the full vision");

        Assert.That(_prototypes.HasIndex<EntityPrototype>("NCCyberEyeLeftStandard"), Is.False);
        Assert.That(_prototypes.HasIndex<EntityPrototype>("NCOrganHumanEyesRight"), Is.False);
        var human = _prototypes.Index<EntityPrototype>("MobHuman");
        Assert.That(human.TryGetComponent<InitialBodyComponent>(out var initial, factory), Is.True);
        var organs = initial!.Organs;
        Assert.That(organs.ContainsKey("Eyes"), Is.True);
        Assert.That(organs.ContainsKey("EyesRight"), Is.False, "a human has one paired eye organ");
    }

    private static float Overlap(Vector2 positionA, Vector2 sizeA, Vector2 positionB, Vector2 sizeB)
    {
        var x = MathF.Min(positionA.X + sizeA.X / 2, positionB.X + sizeB.X / 2) -
                MathF.Max(positionA.X - sizeA.X / 2, positionB.X - sizeB.X / 2);
        var y = MathF.Min(positionA.Y + sizeA.Y / 2, positionB.Y + sizeB.Y / 2) -
                MathF.Max(positionA.Y - sizeA.Y / 2, positionB.Y - sizeB.Y / 2);
        return MathF.Max(0f, x) * MathF.Max(0f, y);
    }
}
