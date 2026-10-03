// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.Combat.Damage;
using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Combat.Environment;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared.Damage;
using Content.Shared.Projectiles;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Combat.Projectiles;

/// <summary>
/// Adapts one real physics collision into the authoritative NC armor and trauma pipeline.
/// It never performs a second accuracy check and never searches outside the struck body's bounded caches.
/// </summary>
public sealed partial class NCProjectileImpactSystem : EntitySystem
{
    private const ulong ImpactSeedStride = 0x9E3779B97F4A7C15UL;

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private NCCombatTraumaDeliverySystem _delivery = default!;
    [Dependency] private NCMedicalDamageRoutingSystem _routing = default!;
    [Dependency] private NCProjectileContextSystem _contexts = default!;
    [Dependency] private NCProjectileVolleySystem _volleys = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    /// <summary>
    /// Returns true only when NC medicine accepted or explicitly cancelled this human impact.
    /// The caller must skip legacy Damageable damage in that case.
    /// </summary>
    public bool TryHandleImpact(
        Entity<ProjectileComponent> projectile,
        EntityUid target,
        DamageSpecifier damage,
        Vector2 impactDirection,
        out DamageSpecifier feedbackDamage,
        out bool projectileSpent)
    {
        feedbackDamage = new DamageSpecifier();
        projectileSpent = true;

        if (!HasComp<NCMedicalBodyComponent>(target))
            return false;

        if (!TryComp<NCProjectileHitContextComponent>(projectile, out var context))
            return false;

        var impactSequence = context.NextImpactSequence;
        // The projectile and impact sequences jointly identify this accepted contact. The target owns its replay
        // cache, so overpenetration and later contacts with the same body remain distinct without accepting replay.
        unchecked
        {
            context.NextImpactSequence++;
        }

        if (!_routing.TryPartitionDamage(damage, out var partition))
        {
            Log.Error($"NC projectile impact on {ToPrettyString(target)} has no damage routing map; compatibility HP was suppressed.");
            return true;
        }

        var systemic = _routing.ApplySystemicSubset(
            target,
            context.ResolutionId,
            NCSystemicDamageSourceKind.Projectile,
            context.ProjectileSequence,
            impactSequence,
            damage);
        if (!systemic.Accepted)
            Log.Error($"NC projectile systemic payload was rejected on {ToPrettyString(target)}; compatibility HP was suppressed.");

        foreach (var healing in partition.RejectedHealing)
        {
            // Physical repair requires a typed treatment or surgery target; a projectile scalar cannot erase wounds.
            var rejected = new NCLegacyHealingRejectedEvent(healing.DamageType, healing.Amount);
            RaiseLocalEvent(target, ref rejected);
        }

        foreach (var unmapped in partition.Unmapped)
            Log.Warning($"NC projectile {unmapped.DamageType.Id} change on {ToPrettyString(target)} has no authoritative mapping; legacy HP was suppressed.");

        if (!partition.LocalTrauma.AnyPositive())
            return true;

        if (!_contexts.TryResolveDamageProfile(projectile.Owner, partition.LocalTrauma, out var damageProfile))
        {
            Log.Error($"NC projectile {ToPrettyString(projectile.Owner)} has no local profile for its routed trauma; compatibility HP was suppressed.");
            return true;
        }

        if (_prototypes.TryIndex(damageProfile, out NCCombatDamageProfilePrototype? resolvedProfile) &&
            resolvedProfile.SuppressLocalTrauma)
        {
            return true;
        }

        var impactSeed = context.Seed ^ (ImpactSeedStride * (impactSequence + 1UL));
        var localHit = _transform.GetRelativePosition(Transform(projectile), target);
        var compoundVolley = context.ProjectilesInShot > 1;
        ProtoId<NCTargetZonePrototype>? distributedZone = null;
        if (compoundVolley &&
            _volleys.TryGetDistributedZone(
                target,
                context.ResolutionId,
                context.ProjectileSequence,
                out var resolvedDistributedZone))
        {
            distributedZone = resolvedDistributedZone;
        }
        var impact = new NCResolvedCombatImpact(
            context.ResolutionId,
            context.ProjectileSequence,
            impactSequence,
            impactSeed,
            target,
            context.Shooter,
            context.Weapon,
            projectile,
            context.IntendedTarget,
            compoundVolley ? NCTargetingMode.Automatic : context.TargetingMode,
            compoundVolley ? null : context.SelectedZone,
            damageProfile,
            partition.LocalTrauma,
            localHit,
            impactDirection,
            0f,
            _timing.CurTime)
        {
            // Only the first accepted pellet establishes the primary collision zone. Later pellets consume the
            // same action result and deterministic neighbour spread instead of each rolling their own anatomy.
            ForcedZone = distributedZone,
            HasPhysicalHitPosition = distributedZone == null,
            ShooterMoving = context.ShooterMoving,
        };

        if (!_delivery.TryDeliver(impact, out var delivery))
        {
            Log.Error($"NC projectile delivery rejected an impact on {ToPrettyString(target)}; compatibility HP was suppressed.");
            return true;
        }

        feedbackDamage = delivery.FeedbackDamage;
        if (compoundVolley && distributedZone == null)
            _volleys.RecordPrimary(target, context.ResolutionId, delivery.ActualZone);
        projectileSpent = delivery.Cancelled ||
                          !delivery.CanOverpenetrateBody ||
                          delivery.Armor.Stopped;

        return true;
    }
}
