// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Combat.Projectiles;

/// <summary>
/// Immutable shot snapshot attached to a physical projectile. No medical or armor collections live here.
/// </summary>
[RegisterComponent, UnsavedComponent, Access(typeof(NCProjectileContextSystem), typeof(NCProjectileImpactSystem))]
public sealed partial class NCProjectileHitContextComponent : Component
{
    [ViewVariables]
    public NCActionResolutionId ResolutionId;

    [ViewVariables]
    public uint ProjectileSequence;

    /// <summary>
    /// Number of physical projectiles emitted by the same accepted gun shot. Values above one are resolved as
    /// a compound volley: the first contact establishes a primary zone and the remaining projectiles spread
    /// deterministically across that zone and its configured neighbours.
    /// </summary>
    [ViewVariables]
    public uint ProjectilesInShot = 1;

    [ViewVariables]
    public uint NextImpactSequence;

    [ViewVariables]
    public EntityUid? Shooter;

    [ViewVariables]
    public EntityUid Weapon;

    [ViewVariables]
    public EntityUid? IntendedTarget;

    [ViewVariables]
    public NCTargetingMode TargetingMode;

    [ViewVariables]
    public ProtoId<NCTargetZonePrototype>? SelectedZone;

    [ViewVariables]
    public bool ShooterMoving;
    public float MovingPenaltyReduction;

    [ViewVariables]
    public ulong Seed;
}
