// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server.Decals;
using Content.Server._NC.Medical.Wounds;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Decals;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Bleeding;

/// <summary>
/// Converts one accepted externally bleeding wound into an authoritative initial blood pulse, a PVS-scoped
/// transient spray and a bounded number of cleanable floor decals. It never scans entities or updates per tick.
/// </summary>
public sealed partial class NCImpactBloodSystem : EntitySystem
{
    private const float MaximumLocalHitOffset = 0.45f;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCBleedingSystem _bleeding = default!;
    [Dependency] private DecalSystem _decals = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        // WoundSystem is the sole trauma-request owner and emits this event only after exactly-once acceptance.
        SubscribeLocalEvent<NCMedicalBodyComponent, NCWoundImpactAppliedEvent>(OnWoundImpactApplied);
        InitializeSurfaceStains();
    }

    private void OnWoundImpactApplied(
        Entity<NCMedicalBodyComponent> entity,
        ref NCWoundImpactAppliedEvent args)
    {
        if (!float.IsFinite(args.ExternalBleedRate) ||
            args.ExternalBleedRate <= 0f ||
            !TryComp<NCWoundContainerComponent>(entity, out var wounds) ||
            !_prototypes.TryIndex(wounds.Profile, out NCWoundSystemProfilePrototype? woundSystem) ||
            !_prototypes.TryIndex(woundSystem.ImpactBloodProfile, out NCImpactBloodProfilePrototype? profile) ||
            !profile.WoundTypes.Contains(args.WoundType) ||
            !profile.Patterns.TryGetValue(args.WoundType, out var pattern) ||
            !profile.BloodLossBySeverity.TryGetValue(args.Severity, out var requestedLoss) ||
            !float.IsFinite(requestedLoss) ||
            requestedLoss <= 0f)
        {
            return;
        }

        var actualLoss = _bleeding.ApplyExternalPulse(entity, requestedLoss);
        if (actualLoss <= 0f)
            return;

        var intensity = Math.Clamp(actualLoss / requestedLoss, 0f, 1f);
        var seed = args.Request.ResolutionId.DeriveSeed(
            args.Request.ProjectileSequence ^ args.Request.ImpactSequence * 0x9E3779B9u);
        var direction = ResolveDirection(args.Request.ImpactDirection, args.Request.ProjectileDisposition);
        var distanceMultiplier = NCImpactBloodRules.ResolveDistanceMultiplier(
            profile,
            args.Request.ProjectileDisposition);
        var origin = ResolveOrigin(entity, args.Request.TargetLocalHitPosition);
        var originMap = _transform.ToMapCoordinates(origin);

        var dropletCount = ScaleCount(profile.DropletsBySeverity.GetValueOrDefault(args.Severity), intensity);
        var splatCount = ScaleCount(
            pattern.SplatsBySeverity.GetValueOrDefault(args.Severity),
            intensity);
        var streakCount = ScaleCount(pattern.StreaksBySeverity.GetValueOrDefault(args.Severity), intensity);
        if ((dropletCount > 0 || splatCount > 0 || streakCount > 0) &&
            originMap.MapId != MapId.Nullspace)
        {
            RaiseNetworkEvent(
                new NCImpactBloodEffectEvent(
                    GetNetCoordinates(origin),
                    direction,
                    woundSystem.ImpactBloodProfile,
                    args.WoundType,
                    seed,
                    dropletCount,
                    splatCount,
                    streakCount,
                    distanceMultiplier),
                Filter.Pvs(origin, entityMan: EntityManager));
        }

        var configuredDecals = profile.DecalsBySeverity.GetValueOrDefault(args.Severity);
        var decalCount = Math.Clamp(
            (int) MathF.Ceiling(configuredDecals * intensity),
            0,
            Math.Max(0, profile.MaximumDecalsPerImpact));
        AddDecals(origin, direction, profile, pattern, seed, decalCount, distanceMultiplier);
    }

    private EntityCoordinates ResolveOrigin(EntityUid body, Vector2 requestedLocalHit)
    {
        var local = IsFinite(requestedLocalHit) ? requestedLocalHit : Vector2.Zero;
        if (local.LengthSquared() > MaximumLocalHitOffset * MaximumLocalHitOffset)
            local = local.Normalized() * MaximumLocalHitOffset;

        return new EntityCoordinates(body, local);
    }

    private void AddDecals(
        EntityCoordinates origin,
        Vector2 direction,
        NCImpactBloodProfilePrototype profile,
        NCImpactBloodPattern pattern,
        ulong seed,
        int requestedCount,
        float distanceMultiplier)
    {
        if (requestedCount <= 0)
            return;

        var originMap = _transform.ToMapCoordinates(origin);
        if (originMap.MapId == MapId.Nullspace)
            return;

        var gridUid = _transform.GetGrid(origin);
        if (gridUid is not { Valid: true } grid)
            return;

        var gridTransform = Transform(grid);
        var localOrigin = _transform.ToCoordinates((grid, gridTransform), originMap);
        var nearby = CountNearbyImpactDecals(grid, localOrigin.Position, profile);
        var floorRemaining = Math.Max(0, profile.MaximumNearbyDecals - nearby);
        var count = Math.Min(requestedCount, Math.Max(0, profile.MaximumDecalsPerImpact));

        for (var index = 0; index < count; index++)
        {
            var sprayDirection = NCImpactBloodRules.SamplePatternDirection(
                direction,
                pattern.Kind,
                pattern.DirectionOffsetDegrees,
                pattern.ConeDegrees,
                seed,
                (uint) index);
            var useStreak = pattern.StreakDecals.Count > 0 &&
                            (pattern.SplatDecals.Count == 0 ||
                             NCImpactBloodRules.Sample01(seed, (uint) index + 0x180u) <
                             pattern.PersistentStreakFraction);
            var candidates = useStreak ? pattern.StreakDecals : pattern.SplatDecals;
            if (candidates.Count == 0)
                continue;

            var decalIndex = NCImpactBloodRules.PickIndex(candidates.Count, seed, (uint) index + 0x1C0u);
            var decal = candidates[decalIndex];
            var distanceSample = NCImpactBloodRules.Sample01(seed, (uint) index + 0x100u);
            var distance = MathF.Max(0f, profile.SprayDistance) *
                           MathF.Max(0f, pattern.DistanceMultiplier) *
                           MathF.Max(0f, distanceMultiplier) *
                           (0.35f + distanceSample * 0.65f);
            if (useStreak)
                distance *= 0.55f;

            var mapPosition = originMap.Position + sprayDirection * distance;
            var coordinates = _transform.ToCoordinates(
                (grid, gridTransform),
                new MapCoordinates(mapPosition, originMap.MapId));
            var angle = useStreak
                ? sprayDirection.ToWorldAngle()
                : new Angle(NCImpactBloodRules.Sample01(seed, (uint) index + 0x200u) * MathF.Tau);

            var sizeSample = NCImpactBloodRules.Sample01(seed, (uint) index + 0x240u);
            var surfaceSize = useStreak
                ? new Vector2(
                    pattern.StreakWidth * (0.82f + sizeSample * 0.36f),
                    pattern.SplatSize * pattern.StreakLengthMultiplier * (0.72f + sizeSample * 0.38f))
                : new Vector2(pattern.SplatSize * (0.78f + sizeSample * 0.36f));
            surfaceSize *= profile.SurfaceStainScale;
            if (TryDepositSurfaceStain(
                    originMap,
                    sprayDirection,
                    distance,
                    decal,
                    angle,
                    surfaceSize,
                    profile))
            {
                continue;
            }

            if (floorRemaining <= 0)
                continue;

            _decals.TryAddDecal(
                decal.Id,
                coordinates,
                out _,
                profile.DecalColor,
                angle,
                zIndex: 5,
                cleanable: true);
            floorRemaining--;
        }
    }

    /// <summary>
    /// Counts stains across every intersecting decal chunk. The vanilla range helper only reads the chunk
    /// containing the query point, which would allow the bounded blood budget to be bypassed at chunk borders.
    /// </summary>
    private int CountNearbyImpactDecals(
        EntityUid grid,
        Vector2 center,
        NCImpactBloodProfilePrototype profile)
    {
        if (profile.MaximumNearbyDecals <= 0 ||
            !float.IsFinite(profile.NearbyDecalRadius) ||
            profile.NearbyDecalRadius <= 0f)
        {
            return profile.MaximumNearbyDecals;
        }

        var radius = profile.NearbyDecalRadius;
        var extent = new Vector2(radius);
        var radiusSquared = radius * radius;
        var count = 0;
        foreach (var (_, decal) in _decals.GetDecalsIntersecting(grid, new Box2(center - extent, center + extent)))
        {
            if (!IsImpactDecal(profile, decal.Id))
                continue;

            // Decal coordinates describe their tile corner; use the same center convention as DecalSystem.
            var decalCenter = decal.Coordinates + new Vector2(0.5f);
            if (Vector2.DistanceSquared(center, decalCenter) <= radiusSquared)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Counts only this profile's bounded decal set; unrelated map decals never consume the impact budget.
    /// </summary>
    private static bool IsImpactDecal(NCImpactBloodProfilePrototype profile, string id)
    {
        foreach (var pattern in profile.Patterns.Values)
        {
            foreach (var decal in pattern.SplatDecals)
            {
                if (decal.Id == id)
                    return true;
            }

            foreach (var decal in pattern.StreakDecals)
            {
                if (decal.Id == id)
                    return true;
            }
        }

        return false;
    }

    private static Vector2 ResolveDirection(
        Vector2 requested,
        NCTraumaProjectileDisposition disposition)
    {
        var direction = IsFinite(requested) && !requested.IsLengthZero()
            ? requested.Normalized()
            : Vector2.UnitY;
        return disposition == NCTraumaProjectileDisposition.Embedded ? -direction : direction;
    }

    private static byte ScaleCount(int configured, float intensity)
    {
        return (byte) Math.Clamp(
            (int) MathF.Ceiling(Math.Max(0, configured) * Math.Clamp(intensity, 0f, 1f)),
            0,
            byte.MaxValue);
    }

    private static bool IsFinite(Vector2 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y);
    }
}
