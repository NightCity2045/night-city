// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.Combat.Damage;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared.Standing;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Combat.Targeting;

/// <summary>
/// Resolves anatomical location only after another system has established a real hit. It consumes immutable
/// physical evidence and never performs accuracy, skill or RED checks.
/// </summary>
public sealed partial class NCHitLocationSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private StandingStateSystem _standing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public bool TryResolve(
        in NCResolvedCombatImpact impact,
        Entity<NCMedicalBodyComponent?> body,
        out ProtoId<NCTargetZonePrototype> actualZone,
        out EntityUid region,
        out NCHitLocationSource source)
    {
        actualZone = default;
        region = default;
        source = default;

        if (impact.ForcedZone is { } forced &&
            _anatomy.TryResolveSelectedZone(body, forced, out actualZone, out region))
        {
            source = NCHitLocationSource.Scripted;
            return true;
        }

        if (impact.IntendedTarget == body.Owner &&
            impact.TargetingMode == NCTargetingMode.SelectedZone &&
            impact.SelectedZone is { } selected &&
            _anatomy.TryResolveSelectedZone(body, selected, out actualZone, out region))
        {
            source = NCHitLocationSource.Selected;
            return true;
        }

        if (impact.HasPhysicalHitPosition &&
            TryResolveCollision(body, impact.TargetLocalHitPosition, out actualZone, out region))
        {
            source = NCHitLocationSource.Collision;
            return true;
        }

        if (TryResolveWeighted(body, impact.Seed, impact.ImpactDirection, out actualZone, out region))
        {
            source = NCHitLocationSource.Weighted;
            return true;
        }

        return false;
    }

    private bool TryResolveCollision(
        Entity<NCMedicalBodyComponent?> body,
        Vector2 localHit,
        out ProtoId<NCTargetZonePrototype> actualZone,
        out EntityUid region)
    {
        actualZone = default;
        region = default;
        if (!_anatomy.TryGetProfile(body, out var profile) ||
            !float.IsFinite(localHit.X) ||
            !float.IsFinite(localHit.Y))
        {
            return false;
        }

        var point = localHit + new Vector2(0.5f, 0.5f);
        if (point.X < 0f || point.X > 1f || point.Y < 0f || point.Y > 1f)
            return false;

        ProtoId<NCTargetZonePrototype>? bestZone = null;
        var bestPriority = int.MinValue;
        foreach (var zoneId in profile.TargetZones)
        {
            if (!_prototypes.TryIndex(zoneId, out NCTargetZonePrototype? zone) ||
                zone.HitBounds is not { } bounds ||
                point.X < bounds.Position.X ||
                point.Y < bounds.Position.Y ||
                point.X >= bounds.Position.X + bounds.Size.X ||
                point.Y >= bounds.Position.Y + bounds.Size.Y)
            {
                continue;
            }

            // Equal-priority rectangles remain stable by anatomy-profile order. Semantic prototype tests reject
            // those overlaps, while explicit higher priorities allow small facial regions inside the head.
            if (zone.HitPriority > bestPriority)
            {
                bestZone = zoneId;
                bestPriority = zone.HitPriority;
            }
        }

        return bestZone is { } selected &&
               _anatomy.TryResolveSelectedZone(body, selected, out actualZone, out region);
    }

    private bool TryResolveWeighted(
        Entity<NCMedicalBodyComponent?> body,
        ulong seed,
        Vector2 worldImpactDirection,
        out ProtoId<NCTargetZonePrototype> actualZone,
        out EntityUid region)
    {
        actualZone = default;
        region = default;
        if (!_anatomy.TryGetProfile(body, out var profile))
            return false;

        var downed = _standing.IsDown(body.Owner);
        var approach = ResolveApproach(body.Owner, worldImpactDirection);
        var weights = new Dictionary<ProtoId<NCTargetZonePrototype>, float>();
        var ordered = new List<ProtoId<NCTargetZonePrototype>>();
        var total = 0f;
        foreach (var configured in profile.TargetZones)
        {
            if (!_prototypes.TryIndex(configured, out NCTargetZonePrototype? zone) ||
                !_anatomy.TryResolveSelectedZone(body, configured, out var resolved, out _) ||
                zone.AutomaticWeight <= 0f)
            {
                continue;
            }

            var multiplier = downed ? zone.DownedWeightMultiplier : 1f;
            if (approach is { } direction &&
                zone.DirectionWeightMultipliers.TryGetValue(direction, out var directionMultiplier))
            {
                multiplier *= directionMultiplier;
            }

            var weight = zone.AutomaticWeight * multiplier;
            if (!float.IsFinite(weight) || weight <= 0f)
                continue;

            if (!weights.TryGetValue(resolved, out var existing))
                ordered.Add(resolved);
            weights[resolved] = existing + weight;
            total += weight;
        }

        if (ordered.Count == 0 || !float.IsFinite(total) || total <= 0f)
            return _anatomy.TryResolveSelectedZone(body, profile.AutomaticFallbackZone, out actualZone, out region);

        var cursor = ToUnitInterval(SplitMix64(seed)) * total;
        foreach (var candidate in ordered)
        {
            cursor -= weights[candidate];
            if (cursor > 0f)
                continue;

            return _anatomy.TryResolveSelectedZone(body, candidate, out actualZone, out region);
        }

        return _anatomy.TryResolveSelectedZone(body, profile.AutomaticFallbackZone, out actualZone, out region);
    }

    private NCHitApproachDirection? ResolveApproach(EntityUid target, Vector2 worldDirection)
    {
        if (!float.IsFinite(worldDirection.X) || !float.IsFinite(worldDirection.Y) || worldDirection.IsLengthZero())
            return null;

        var local = (-_transform.GetWorldRotation(target)).RotateVec(worldDirection.Normalized());
        if (MathF.Abs(local.X) > MathF.Abs(local.Y))
            return local.X > 0f ? NCHitApproachDirection.Left : NCHitApproachDirection.Right;

        return local.Y > 0f ? NCHitApproachDirection.Rear : NCHitApproachDirection.Front;
    }

    private static ulong SplitMix64(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ value >> 30) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ value >> 27) * 0x94D049BB133111EBUL;
        return value ^ value >> 31;
    }

    private static float ToUnitInterval(ulong value)
    {
        return (float) ((value >> 40) * (1.0 / (1UL << 24)));
    }
}
