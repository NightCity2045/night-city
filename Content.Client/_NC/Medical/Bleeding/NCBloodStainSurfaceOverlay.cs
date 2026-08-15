// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Medical.Bleeding;
using Robust.Client.Graphics;
using Robust.Shared.Enums;

namespace Content.Client._NC.Medical.Bleeding;

/// <summary>
/// Draws compact blood masks in each structure's local space. This keeps stains attached while a door rotates or
/// moves and avoids allocating a sprite entity for every mark.
/// </summary>
public sealed class NCBloodStainSurfaceOverlay : Overlay
{
    private readonly NCBloodStainSurfaceClientSystem _system;
    private readonly IEntityManager _entities;
    private readonly SharedTransformSystem _transform;

    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowFOV;

    public NCBloodStainSurfaceOverlay(
        NCBloodStainSurfaceClientSystem system,
        IEntityManager entities)
    {
        _system = system;
        _entities = entities;
        _transform = entities.System<SharedTransformSystem>();
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var stainQuery = _entities.GetEntityQuery<NCBloodStainSurfaceComponent>();
        var transformQuery = _entities.GetEntityQuery<TransformComponent>();
        foreach (var uid in _system.StainedEntities)
        {
            if (!stainQuery.TryGetComponent(uid, out var surface) ||
                surface.Stains.Count == 0 ||
                !transformQuery.TryGetComponent(uid, out var transform) ||
                transform.MapID != args.MapId)
            {
                continue;
            }

            var (worldPosition, worldRotation) = _transform.GetWorldPositionRotation(transform, transformQuery);
            var cullingBounds = Box2.CenteredAround(worldPosition, new Vector2(1.5f));
            if (!cullingBounds.Intersects(args.WorldAABB))
                continue;

            foreach (var stain in surface.Stains)
            {
                if (!_system.TryGetTexture(stain.Decal, out var texture))
                    continue;

                var position = worldPosition + worldRotation.RotateVec(stain.LocalPosition);
                var box = Box2.CenteredAround(position, stain.Size);
                var rotation = new Angle(worldRotation.Theta + stain.LocalRotation);
                args.WorldHandle.DrawTextureRect(
                    texture,
                    new Box2Rotated(box, rotation, position),
                    stain.Color);
            }
        }
    }
}
