// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.Combat.Physical;
using Content.Shared._NC.Combat.Physical;
using Content.Shared.Damage;

namespace Content.Server.Shuttles.Systems;

public sealed partial class ShuttleSystem
{
    [Dependency] private NCPhysicalImpactAdapterSystem _ncPhysicalImpacts = default!;

    private bool TryApplyNcShuttleImpact(
        EntityUid target,
        EntityUid grid,
        DamageSpecifier damage,
        Vector2 direction)
    {
        var request = new NCPhysicalImpactRequestedEvent(
            NCPhysicalImpactSourceKind.Collision,
            new DamageSpecifier(damage),
            grid,
            grid,
            direction);
        return _ncPhysicalImpacts.TryApply(target, ref request);
    }
}
