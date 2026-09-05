// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Coordinates.Systems;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.Client._NC.LivingCity.Ambient;

/// <summary>
/// Draws cheap distant silhouettes without creating client or server ECS entities.
/// </summary>
public sealed class NCAmbientCrowdOverlay : Overlay
{
    private readonly NCAmbientCrowdClientSystem _system;
    private readonly NCMapCoordinatesSystem _coordinates;
    private readonly IGameTiming _timing;

    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowFOV;

    public NCAmbientCrowdOverlay(NCAmbientCrowdClientSystem system)
    {
        _system = system;
        _coordinates = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<NCMapCoordinatesSystem>();
        _timing = IoCManager.Resolve<IGameTiming>();
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var bounds = args.WorldBounds.CalcBoundingBox().Enlarged(1f);
        var elapsed = (float) _timing.CurTime.TotalSeconds;
        foreach (var visual in _system.Visuals)
        {
            if (!_coordinates.TryResolve(visual.Origin, out var resolved) || resolved.MapId != args.MapId)
                continue;

            var angle = (visual.Seed % 6283u) / 1000f;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var offset = direction * MathF.Sin(elapsed * visual.MotionSpeed + angle) * visual.MotionRadius;
            var position = resolved.Position + offset;
            if (!bounds.Contains(position))
                continue;

            var scale = Math.Clamp(visual.Scale, 0.25f, 3f);
            var bodySize = new Vector2(0.24f, 0.48f) * scale;
            var body = Box2.CenteredAround(position + new Vector2(0f, -0.08f * scale), bodySize);
            args.WorldHandle.DrawRect(body, visual.Color);
            args.WorldHandle.DrawCircle(
                position + new Vector2(0f, 0.27f * scale),
                0.13f * scale,
                visual.Color);
        }
    }
}
