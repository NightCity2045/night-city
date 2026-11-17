/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Numerics;
using Content.Client._NC.ZLevels.Core;
using Content.Shared._NC.ZLevels.Core.Components;
using Content.Shared._NC.ZLevels.Core.EntitySystems;
using Content.Shared.Maps;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Client.Viewport;

public sealed partial class ScalingViewport
{
    [Dependency] private IEyeManager _eyeManager = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private ITileDefinitionManager _tile = default!;

    private NCClientZLevelsSystem? _zLevels;
    private SharedMapSystem? _mapSystem;

    private EntityQuery<TransformComponent>? _xformQuery;
    private EntityQuery<MapComponent>? _mapQuery;

    private IEye? _fallbackEye;

    /// <summary>
    /// We are looking for at least one empty tile on the screen.
    /// This is used to ensure that it makes sense to draw the z-planes and that they are visible.
    /// </summary>
    public bool TryFindEmptyTiles(EntityUid mapUid)
    {
        if (_xformQuery is null || !_xformQuery.Value.TryComp(mapUid, out var xform))
            return true;

        var drawBox = GetDrawBox();
        var mapId = xform.MapID;

        var corners = new[]
        {
            _eyeManager.ScreenToMap(drawBox.BottomLeft).Position,
            _eyeManager.ScreenToMap(drawBox.BottomRight).Position,
            _eyeManager.ScreenToMap(drawBox.TopLeft).Position,
            _eyeManager.ScreenToMap(drawBox.TopRight).Position
        };

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        foreach (var c in corners)
        {
            if (c.X < minX)
                minX = c.X;
            if (c.Y < minY)
                minY = c.Y;
            if (c.X > maxX)
                maxX = c.X;
            if (c.Y > maxY)
                maxY = c.Y;
        }

        var mapCoordsBottomLeft = new MapCoordinates(new Vector2(minX, minY), mapId);
        var mapCoordsTopRight = new MapCoordinates(new Vector2(maxX, maxY), mapId);

        if (_mapSystem is null || !_mapSystem.TryFindGridAt(mapUid, mapCoordsBottomLeft.Position, out var gridUid, out var grid))
            return true;

        var tileBottomLeft = _mapSystem.TileIndicesFor(gridUid, grid, mapCoordsBottomLeft);
        var tileTopRight = _mapSystem.TileIndicesFor(gridUid, grid, mapCoordsTopRight);

        for (var x = tileBottomLeft.X - 1; x <= tileTopRight.X + 1; x++)
        {
            for (var y = tileBottomLeft.Y - 1; y <= tileTopRight.Y + 1; y++)
            {
                var tile = _mapSystem.GetTileRef(gridUid, grid, new Vector2i(x, y));
                var tileDef = (ContentTileDefinition)_tile[tile.Tile.TypeId];
                if (tileDef.Transparent || tile.Tile.IsEmpty)
                    return true;
            }
        }

        return false;
    }

    private void RenderZLevels(IClydeViewport viewport)
    {
        if (_eye is null)
            return;

        _fallbackEye = _eye;
        _xformQuery ??= _entityManager.GetEntityQuery<TransformComponent>();
        _mapQuery ??= _entityManager.GetEntityQuery<MapComponent>();
        _zLevels ??= _entityManager.System<NCClientZLevelsSystem>();
        _mapSystem ??= _entityManager.System<SharedMapSystem>();

        if (_player.LocalEntity is not { } player ||
            !_entityManager.TryGetComponent<NCZLevelViewerComponent>(player, out var viewer) ||
            !_xformQuery.Value.TryComp(player, out var playerTransform) ||
            playerTransform.MapUid is not { } playerMap)
            return;

        EntityUid? riderGrid = null;
        NCZGridMotionComponent? rider = null;
        if (_entityManager.TryGetComponent<NCZGridMotionMapComponent>(playerMap, out var transit) &&
            _entityManager.TryGetComponent<NCZGridMotionComponent>(transit.Grid, out var motion) &&
            _entityManager.HasComponent<NCZMapComponent>(motion.SourceMap))
        {
            riderGrid = transit.Grid;
            rider = motion;
        }
        var baseMap = rider?.SourceMap ?? playerMap;
        var riderShift = rider == null ? 0f : GridMotionOffset(rider);
        var lookUp = viewer.LookUp ? 1 : 0;

        var lowestDepth = 0;
        for (var depth = 0; depth >= -NCSharedZLevelsSystem.MaxZLevelsBelowRendering; depth--)
        {
            var checkingMap = baseMap;
            if (depth != 0)
            {
                if (!_zLevels.TryMapOffset(baseMap, depth, out var lowerMap))
                    continue;
                checkingMap = lowerMap;
            }
            lowestDepth = depth;
            if (!TryFindEmptyTiles(checkingMap))
                break;
        }

        if (rider != null)
            lowestDepth = Math.Min(lowestDepth, (int) MathF.Floor(riderShift));

        for (var depth = lowestDepth; depth <= lookUp; depth++)
        {
            var map = baseMap;
            if (depth != 0)
            {
                if (!_zLevels.TryMapOffset(baseMap, depth, out var otherMap))
                    continue;
                map = otherMap;
            }
            if (!_mapQuery.Value.TryComp(map, out var mapComponent))
                continue;

            if (depth == 0 && rider == null)
            {
                viewport.Eye = _fallbackEye;
            }
            else
            {
                var offset = (-_fallbackEye.Rotation).ToWorldVec() *
                    NCClientZLevelsSystem.ZLevelOffset * (depth - riderShift);
                viewport.Eye = new ZEye(lowestDepth, depth, lookUp)
                {
                    Position = new MapCoordinates(_fallbackEye.Position.Position, mapComponent.MapId),
                    DrawFov = _fallbackEye.DrawFov && depth >= 0,
                    DrawLight = _fallbackEye.DrawLight,
                    Offset = _fallbackEye.Offset + offset,
                    Rotation = _fallbackEye.Rotation,
                    Scale = _fallbackEye.Scale,
                };
            }

            viewport.ClearColor = depth == lowestDepth ? Color.Black : null;
            viewport.Render();
            RenderMovingGrids(viewport, baseMap, depth, lowestDepth, lookUp, riderShift, riderGrid);
        }

        Eye = _fallbackEye;
        viewport.Eye = Eye;
    }

    public sealed partial class ZEye(int lowest, int depth, int high) : Robust.Shared.Graphics.Eye
    {
        public int LowestDepth = lowest;
        public int Depth = depth;
        public int HighestDepth = high;
        public bool IsGridMotion;
    }
}
