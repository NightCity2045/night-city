// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared.Decals;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._NC.Medical.Bleeding;

/// <summary>
/// Receives PVS-scoped server-authoritative blood pulses and owns their bounded transient world overlay.
/// </summary>
public sealed partial class NCImpactBloodClientSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SpriteSystem _sprites = default!;

    private NCImpactBloodOverlay? _overlay;

    public override void Initialize()
    {
        base.Initialize();

        _overlay = new NCImpactBloodOverlay(_timing);
        _overlays.AddOverlay(_overlay);
        SubscribeNetworkEvent<NCImpactBloodEffectEvent>(OnImpactBlood);
    }

    public override void Shutdown()
    {
        if (_overlay != null)
            _overlays.RemoveOverlay(_overlay);
        _overlay = null;

        base.Shutdown();
    }

    private void OnImpactBlood(NCImpactBloodEffectEvent args)
    {
        if (_overlay == null ||
            (args.DropletCount == 0 && args.SplatCount == 0 && args.StreakCount == 0) ||
            !_prototypes.TryIndex(args.Profile, out NCImpactBloodProfilePrototype? profile) ||
            !profile.Patterns.TryGetValue(args.WoundType, out var pattern) ||
            profile.EffectDuration <= TimeSpan.Zero ||
            !float.IsFinite(profile.SprayDistance) ||
            profile.SprayDistance <= 0f ||
            !float.IsFinite(profile.DropletSize) ||
            profile.DropletSize <= 0f ||
            !IsFinite(args.Direction) ||
            !float.IsFinite(args.DistanceMultiplier) ||
            args.DistanceMultiplier <= 0f ||
            !IsValid(pattern))
        {
            return;
        }

        var coordinates = GetCoordinates(args.Origin);
        if (Deleted(coordinates.EntityId))
            return;

        var origin = _transform.ToMapCoordinates(coordinates);
        if (origin.MapId == MapId.Nullspace)
            return;

        var splatTextures = ResolveTextures(pattern.SplatDecals);
        var streakTextures = ResolveTextures(pattern.StreakDecals);
        if ((args.SplatCount > 0 && splatTextures.Length == 0) ||
            (args.StreakCount > 0 && streakTextures.Length == 0))
        {
            return;
        }

        _overlay.AddBurst(new NCImpactBloodBurst(
            origin.MapId,
            origin.Position,
            args.Direction.IsLengthZero() ? Vector2.UnitY : args.Direction.Normalized(),
            args.Seed,
            args.DropletCount,
            args.SplatCount,
            args.StreakCount,
            splatTextures,
            streakTextures,
            args.DistanceMultiplier,
            profile.EffectDuration,
            profile.SprayDistance,
            pattern.Kind,
            pattern.ConeDegrees,
            pattern.DirectionOffsetDegrees,
            pattern.DistanceMultiplier,
            pattern.SplatRadius,
            pattern.SplatSize,
            pattern.StreakLengthMultiplier,
            pattern.StreakWidth,
            profile.DropletSize,
            profile.EffectColor,
            Math.Max(1, profile.MaximumClientBursts),
            _timing.CurTime));
    }

    private static bool IsFinite(Vector2 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y);
    }

    private static bool IsValid(NCImpactBloodPattern pattern)
    {
        return pattern.SplatDecals.Count > 0 &&
               pattern.StreakDecals.Count > 0 &&
               float.IsFinite(pattern.ConeDegrees) &&
               pattern.ConeDegrees is >= 0f and <= 360f &&
               float.IsFinite(pattern.DirectionOffsetDegrees) &&
               float.IsFinite(pattern.DistanceMultiplier) && pattern.DistanceMultiplier > 0f &&
               float.IsFinite(pattern.SplatRadius) && pattern.SplatRadius >= 0f &&
               float.IsFinite(pattern.SplatSize) && pattern.SplatSize > 0f &&
               float.IsFinite(pattern.StreakLengthMultiplier) && pattern.StreakLengthMultiplier > 0f &&
               float.IsFinite(pattern.StreakWidth) && pattern.StreakWidth > 0f &&
               float.IsFinite(pattern.PersistentStreakFraction) &&
               pattern.PersistentStreakFraction is >= 0f and <= 1f;
    }

    /// <summary>
    /// Resolves the existing decal sprites once per impact rather than looking them up on every overlay frame.
    /// </summary>
    private Texture[] ResolveTextures(IReadOnlyList<ProtoId<DecalPrototype>> decals)
    {
        var textures = new List<Texture>(decals.Count);
        foreach (var decalId in decals)
        {
            if (_prototypes.TryIndex(decalId, out DecalPrototype? decal))
                textures.Add(_sprites.Frame0(decal.Sprite));
        }

        return textures.ToArray();
    }
}
