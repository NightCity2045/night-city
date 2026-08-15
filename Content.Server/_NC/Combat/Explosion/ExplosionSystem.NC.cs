// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Combat.Explosion;
using Content.Shared.Damage;
using Robust.Shared.Map;

namespace Content.Server.Explosion.EntitySystems;

/// <summary>
/// Narrow bridge kept in a custom file so the upstream explosion implementation only needs one guarded call.
/// </summary>
public sealed partial class ExplosionSystem
{
    [Dependency] private NCExplosionMedicalAdapterSystem _ncMedicalExplosion = default!;

    private bool NCTryApplyMedicalExplosion(
        EntityUid target,
        MapCoordinates epicenter,
        DamageSpecifier damage,
        string explosionPrototype,
        EntityUid? cause)
    {
        return _ncMedicalExplosion.TryHandleExplosionDamage(
            target,
            epicenter,
            damage,
            explosionPrototype,
            cause);
    }
}
