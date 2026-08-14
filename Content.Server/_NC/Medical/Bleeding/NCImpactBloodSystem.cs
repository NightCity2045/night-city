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

        var configuredDroplets = profile.DropletsBySeverity.GetValueOrDefault(args.Severity);
        var dropletCount = (byte) Math.Clamp(
            (int) MathF.Ceiling(configuredDroplets * intensity),
            0,
            byte.MaxValue);
        if (dropletCount > 0 && originMap.MapId != MapId.Nullspace)
        {
            RaiseNetworkEvent(
                new NCImpactBloodEffectEvent(
                    GetNetCoordinates(origin),
                    direction,
                    woundSystem.ImpactBloodProfile,
                    seed,
                    dropletCount,
                    distanceMultiplier),
                Filter.Pvs(origin, entityMan: EntityManager));
        }

        var configuredDecals = profile.DecalsBySeverity.GetValueOrDefault(args.Severity);
        var decalCount = Math.Clamp(
            (int) MathF.Ceiling(configuredDecals * intensity),
            0,
            Math.Max(0, profile.MaximumDecalsPerImpact));
        AddDecals(origin, direction, profile, seed, decalCount, distanceMultiplier);
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
        ulong seed,
        int requestedCount,
        float distanceMultiplier)
    {
        if (requestedCount <= 0 ||
            profile.MaximumNearbyDecals <= 0 ||
            !float.IsFinite(profile.NearbyDecalRadius) ||
            profile.NearbyDecalRadius <= 0f)
        {
            return;
        }

        var originMap = _transform.ToMapCoordinates(origin);
        if (originMap.MapId == MapId.Nullspace)
            return;

        var gridUid = _transform.GetGrid(origin);
        if (gridUid is not { Valid: true } grid)
            return;

        var gridTransform = Transform(grid);
        var localOrigin = _transform.ToCoordinates((grid, gridTransform), originMap);
        var nearby = _decals.GetDecalsInRange(
            grid,
            localOrigin.Position,
            profile.NearbyDecalRadius,
            decal => decal.Id == profile.Decal.Id).Count;
        var remaining = Math.Max(0, profile.MaximumNearbyDecals - nearby);
        var count = Math.Min(requestedCount, remaining);

        for (var index = 0; index < count; index++)
        {
            var sprayDirection = NCImpactBloodRules.SampleDirection(
                direction,
                profile.SprayConeDegrees,
                seed,
                (uint) index);
            var distanceSample = NCImpactBloodRules.Sample01(seed, (uint) index + 0x100u);
            var distance = MathF.Max(0f, profile.SprayDistance) *
                           MathF.Max(0f, distanceMultiplier) *
                           (0.35f + distanceSample * 0.65f);
            var mapPosition = originMap.Position + sprayDirection * distance;
            var coordinates = _transform.ToCoordinates(
                (grid, gridTransform),
                new MapCoordinates(mapPosition, originMap.MapId));
            _decals.TryAddDecal(
                profile.Decal.Id,
                coordinates,
                out _,
                profile.Color,
                sprayDirection.ToAngle(),
                zIndex: 5,
                cleanable: true);
        }
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

    private static bool IsFinite(Vector2 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y);
    }
}
