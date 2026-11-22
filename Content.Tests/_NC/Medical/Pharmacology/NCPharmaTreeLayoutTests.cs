// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using NUnit.Framework;

namespace Content.Tests._NC.Medical.Pharmacology;

[TestFixture]
public sealed class NCPharmaTreeLayoutTests
{
    // A real console: about thirty cores, nine delivery forms, a few modifiers per branch.
    private static readonly int[] Realistic = [30, 9, 8, 4, 13];

    private static List<Vector2> AllNodes(int[] counts)
    {
        var (branches, children, _) = NCPharmaTreeLayout.Compute(counts);
        var all = new List<Vector2> { Vector2.Zero };
        all.AddRange(branches);
        foreach (var group in children)
            all.AddRange(group);
        return all;
    }

    [TestCase(30, 9, 8, 4, 13)]
    [TestCase(5, 9, 8, 4, 13)]
    [TestCase(60, 9, 8, 4, 13)]
    [TestCase(1, 1, 1, 1, 1)]
    public void NoTwoNodesSitCloserThanALabelIsWide(int core, int delivery, int kinetics, int safety, int production)
    {
        var nodes = AllNodes([core, delivery, kinetics, safety, production]);
        var closest = float.MaxValue;
        for (var a = 0; a < nodes.Count; a++)
        {
            for (var b = a + 1; b < nodes.Count; b++)
                closest = MathF.Min(closest, Vector2.Distance(nodes[a], nodes[b]));
        }

        Assert.That(closest, Is.GreaterThanOrEqualTo(100f), "labels about a hundred units wide must not overlap");
    }

    [Test]
    public void AnEmptyBranchStillGetsAPlace()
    {
        var (branches, children, extent) = NCPharmaTreeLayout.Compute([4, 0, 0, 0, 0]);
        Assert.That(branches, Has.Length.EqualTo(5));
        Assert.That(children.Select(group => group.Length), Is.EqualTo(new[] { 4, 0, 0, 0, 0 }));
        Assert.That(extent, Is.GreaterThan(0f));
    }
}
