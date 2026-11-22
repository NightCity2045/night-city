// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;

namespace Content.Shared._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Where the nodes of the formula map stand. Every branch gets a sector of the circle as wide as its share of the
/// nodes, and its nodes stand on arcs inside it, spaced so that no two labels meet. Pure arithmetic, so the layout
/// can be checked without a screen.
/// </summary>
public static class NCPharmaTreeLayout
{
    public const float BranchDistance = 150f;
    public const float FirstRing = 280f;
    public const float RingStep = 112f;
    public const float NodeSpacing = 138f;
    /// <summary>The room kept free on each side of a sector, so the edge nodes of two branches keep their labels apart.</summary>
    public const float EdgeClearance = 62f;

    public static Vector2 Polar(float angle, float distance) => new(MathF.Cos(angle) * distance, MathF.Sin(angle) * distance);

    /// <summary>The branch positions and the child positions of each branch, in the order the counts are given.</summary>
    public static (Vector2[] Branches, Vector2[][] Children, float Extent) Compute(IReadOnlyList<int> counts)
    {
        var weights = counts.Select(count => MathF.Sqrt(Math.Max(1, count)) + 0.6f).ToArray();
        var total = weights.Sum();
        var widths = weights.Select(weight => MathF.Tau * weight / total).ToArray();

        // The first branch, the core, is centred on the left, where a window is widest.
        var cursor = MathF.PI - widths[0] / 2f;
        var branches = new Vector2[counts.Count];
        var children = new Vector2[counts.Count][];
        var extent = 400f;
        for (var branch = 0; branch < counts.Count; branch++)
        {
            var angle = cursor + widths[branch] / 2f;
            cursor += widths[branch];
            branches[branch] = Polar(angle, BranchDistance);
            children[branch] = new Vector2[counts[branch]];
            var placed = 0;
            for (var ring = 0; placed < counts[branch]; ring++)
            {
                var radius = FirstRing + ring * RingStep;
                var half = MathF.Max(0f, widths[branch] / 2f - EdgeClearance / radius);
                var capacity = Math.Max(1, (int) MathF.Floor(2f * half * radius / NodeSpacing) + 1);
                var count = Math.Min(capacity, counts[branch] - placed);
                for (var i = 0; i < count; i++)
                {
                    var offset = count == 1 ? 0f : ((float) i / (count - 1) - 0.5f) * 2f * half;
                    children[branch][placed + i] = Polar(angle + offset, radius);
                }

                placed += count;
                extent = MathF.Max(extent, radius + 64f);
            }
        }

        return (branches, children, extent);
    }
}
