// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Robust.Shared.Physics;

namespace Content.Server._NC.Vehicle;

/// <summary>Measures the real solid chassis without moving it or changing its broadphase membership.</summary>
public sealed class NCVehicleFootprintSystem : EntitySystem
{
    public bool TryGetHalfExtents(EntityUid uid, out Vector2 half)
    {
        half = Vector2.Zero;
        if (!TryComp<FixturesComponent>(uid, out var fixtures))
            return false;
        foreach (var fixture in fixtures.Fixtures.Values)
        {
            if (!fixture.Hard || fixture.CollisionLayer == 0)
                continue;
            for (var child = 0; child < fixture.Shape.ChildCount; child++)
            {
                var bounds = fixture.Shape.ComputeAABB(Robust.Shared.Physics.Transform.Empty, child);
                // Preserve offsets from the entity origin; an asymmetric nose also needs clearance.
                half = Vector2.Max(half, Vector2.Max(Vector2.Abs(bounds.BottomLeft), Vector2.Abs(bounds.TopRight)));
            }
        }
        return float.IsFinite(half.X) && float.IsFinite(half.Y) && half.X > 0 && half.Y > 0;
    }
}
