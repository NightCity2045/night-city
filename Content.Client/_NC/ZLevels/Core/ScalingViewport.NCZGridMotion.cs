// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client._NC.ZLevels.Core;
using Content.Shared._NC.ZLevels.Core;
using Content.Shared._NC.ZLevels.Core.Components;
using Robust.Client.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Client.Viewport;

public sealed partial class ScalingViewport
{
    [Dependency] private IGameTiming _motionTiming = default!;

    private float GridMotionOffset(NCZGridMotionComponent motion)
    {
        // Keep the last transit frame in the preceding render slot until the arrival state arrives.
        return Math.Clamp(NCZGridMotionMath.Offset(motion.StartedAt, motion.ArrivesAt,
            _motionTiming.CurTime, motion.Direction), -0.9999f, 0.9999f);
    }

    private void RenderMovingGrids(IClydeViewport viewport, EntityUid baseMap, int slot,
        int lowest, int highest, float riderShift, EntityUid? riderGrid)
    {
        if (_fallbackEye == null ||
            !_entityManager.TryGetComponent<NCZMapComponent>(baseMap, out var baseZ))
            return;

        var motions = _entityManager.EntityQueryEnumerator<NCZGridMotionComponent, TransformComponent>();
        while (motions.MoveNext(out var grid, out var motion, out var transform))
        {
            if (transform.MapUid is not { } transitMap ||
                !_entityManager.TryGetComponent<NCZGridMotionMapComponent>(transitMap, out var transit) ||
                transit.Grid != grid ||
                !_entityManager.TryGetComponent<MapComponent>(transitMap, out var map) ||
                !_entityManager.TryGetComponent<NCZMapComponent>(motion.SourceMap, out var source) ||
                source.NetworkUid != baseZ.NetworkUid)
                continue;

            var height = source.Depth - baseZ.Depth + GridMotionOffset(motion);
            if ((int) MathF.Floor(height) != slot)
                continue;

            var ownGrid = riderGrid == grid;
            viewport.Eye = new ZEye(lowest - 1, ownGrid ? 0 : slot, highest)
            {
                IsGridMotion = true,
                Position = new MapCoordinates(_fallbackEye.Position.Position, map.MapId),
                DrawFov = _fallbackEye.DrawFov && ownGrid,
                DrawLight = _fallbackEye.DrawLight,
                Offset = _fallbackEye.Offset + (-_fallbackEye.Rotation).ToWorldVec() *
                    NCClientZLevelsSystem.ZLevelOffset * (height - riderShift),
                Rotation = _fallbackEye.Rotation,
                Scale = _fallbackEye.Scale,
            };
            viewport.ClearColor = null;
            viewport.Render();
        }
    }
}
