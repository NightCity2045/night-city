// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Vehicle.AV.Components;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.Map.Components;
using DrawDepth = Content.Shared.DrawDepth.DrawDepth;

namespace Content.Client._NC.Vehicle.AV.Visuals;

/// <summary>
/// Draws an exact tile-footprint shadow and lift wash below airborne AV grids without moving their real sprites.
/// </summary>
public sealed class NCAVFlightOverlay : Overlay
{
    private readonly IEntityManager _entities;
    private readonly IPlayerManager _players;
    private readonly EntityLookupSystem _lookup;
    private readonly SharedMapSystem _maps;
    private readonly SharedTransformSystem _transform;

    public override OverlaySpace Space => OverlaySpace.WorldSpaceEntities;

    public NCAVFlightOverlay(IEntityManager entities, IPlayerManager players)
    {
        _entities = entities;
        _players = players;
        _lookup = entities.System<EntityLookupSystem>();
        _maps = entities.System<SharedMapSystem>();
        _transform = entities.System<SharedTransformSystem>();

        // Render above grid tiles but below the AV and ordinary world entities.
        ZIndex = (int) DrawDepth.FloorObjects;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var handle = args.WorldHandle;
        EntityUid? localPlayerGrid = null;

        // Do not draw the AV's exterior flight effects over its own interior for its occupants.
        if (_entities.TryGetComponent<TransformComponent>(_players.LocalEntity, out var playerTransform))
            localPlayerGrid = playerTransform.GridUid;

        var query = _entities.EntityQueryEnumerator<NCAVComponent, MapGridComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var av, out var grid, out var xform))
        {
            if (!av.Airborne || xform.MapID != args.MapId || uid == localPlayerGrid)
                continue;

            var height = Math.Clamp(av.Height, 0f, 1f);
            var worldMatrix = _transform.GetWorldMatrix(xform);
            var shadowOffset = av.FlightShadowOffset * height;
            var shadowWorldBounds = worldMatrix.TransformBox(grid.LocalAABB).Translated(shadowOffset);
            if (!shadowWorldBounds.Intersects(args.WorldAABB))
                continue;

            // The wash remains attached to the AV while its shadow separates from it as altitude increases.
            var glowAlpha = Math.Clamp(av.FlightGlowAlpha, 0f, 1f) * (0.4f + 0.6f * height);
            handle.SetTransform(worldMatrix);
            DrawFootprint(handle, (uid, grid), av.FlightGlowColor.WithAlpha(glowAlpha));

            var shadowMatrix = worldMatrix * Matrix3x2.CreateTranslation(shadowOffset);
            var shadowAlpha = av.FlightShadowColor.A * (0.35f + 0.65f * height);
            handle.SetTransform(shadowMatrix);
            DrawFootprint(handle, (uid, grid), av.FlightShadowColor.WithAlpha(shadowAlpha));
        }

        handle.SetTransform(Matrix3x2.Identity);
    }

    private void DrawFootprint(DrawingHandleWorld handle, Entity<MapGridComponent> grid, Color color)
    {
        foreach (var tile in _maps.GetAllTiles(grid, grid.Comp))
            handle.DrawRect(_lookup.GetLocalBounds(tile.GridIndices, grid.Comp.TileSize), color);
    }
}
