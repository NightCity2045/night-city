// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared._NC.Medical.Surgery;
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

    private static float Overlap(Vector2 positionA, Vector2 sizeA, Vector2 positionB, Vector2 sizeB)
    {
        var x = MathF.Min(positionA.X + sizeA.X / 2, positionB.X + sizeB.X / 2) -
                MathF.Max(positionA.X - sizeA.X / 2, positionB.X - sizeB.X / 2);
        var y = MathF.Min(positionA.Y + sizeA.Y / 2, positionB.Y + sizeB.Y / 2) -
                MathF.Max(positionA.Y - sizeA.Y / 2, positionB.Y - sizeB.Y / 2);
        return MathF.Max(0f, x) * MathF.Max(0f, y);
    }
}
