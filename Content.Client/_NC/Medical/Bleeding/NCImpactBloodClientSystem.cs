// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Medical.Bleeding;
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
            args.DropletCount == 0 ||
            !_prototypes.TryIndex(args.Profile, out NCImpactBloodProfilePrototype? profile) ||
            profile.EffectDuration <= TimeSpan.Zero ||
            !float.IsFinite(profile.SprayDistance) ||
            profile.SprayDistance <= 0f ||
            !float.IsFinite(profile.DropletSize) ||
            profile.DropletSize <= 0f ||
            !IsFinite(args.Direction) ||
            !float.IsFinite(args.DistanceMultiplier) ||
            args.DistanceMultiplier <= 0f)
        {
            return;
        }

        var coordinates = GetCoordinates(args.Origin);
        if (Deleted(coordinates.EntityId))
            return;

        var origin = _transform.ToMapCoordinates(coordinates);
        if (origin.MapId == MapId.Nullspace)
            return;

        _overlay.AddBurst(new NCImpactBloodBurst(
            origin.MapId,
            origin.Position,
            args.Direction.IsLengthZero() ? Vector2.UnitY : args.Direction.Normalized(),
            args.Seed,
            args.DropletCount,
            args.DistanceMultiplier,
            profile.EffectDuration,
            profile.SprayDistance,
            profile.SprayConeDegrees,
            profile.DropletSize,
            profile.Color,
            Math.Max(1, profile.MaximumClientBursts),
            _timing.CurTime));
    }

    private static bool IsFinite(Vector2 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y);
    }
}
