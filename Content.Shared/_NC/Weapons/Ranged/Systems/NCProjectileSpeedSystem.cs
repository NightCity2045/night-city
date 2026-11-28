// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;

namespace Content.Shared._NC.Weapons.Ranged.Systems;

/// <summary>
/// Slows every gun-fired projectile in the city. Applied as a gun modifier so prototype values, upgrades and
/// wield bonuses keep their relative balance and prediction sees the same speed as the server.
/// </summary>
public sealed class NCProjectileSpeedSystem : EntitySystem
{
    public const float ProjectileSpeedMultiplier = 0.5f;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GunComponent, GunRefreshModifiersEvent>(OnGunRefreshModifiers);
    }

    private void OnGunRefreshModifiers(Entity<GunComponent> gun, ref GunRefreshModifiersEvent args)
    {
        args.ProjectileSpeed *= ProjectileSpeedMultiplier;
    }
}
