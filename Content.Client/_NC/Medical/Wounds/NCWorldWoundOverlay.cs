// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Linq;
using Content.Client.Clickable;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Humanoid;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.Utility;
using Robust.Shared.Enums;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Medical.Wounds;

/// <summary>
/// Paints public wound marks directly over visible humanoid sprites. The owning system maintains an event-driven
/// index containing only bodies with visible marks, while this overlay additionally culls them to the viewport.
/// </summary>
public sealed class NCWorldWoundOverlay : Overlay
{
    private readonly NCWorldWoundOverlaySystem _system;
    private readonly IEntityManager _entities;
    private readonly IPrototypeManager _prototypes;
    private readonly IResourceCache _resources;
    private readonly IClickMapManager _clickMaps;
    private readonly SharedTransformSystem _transform;
    private readonly SpriteSystem _sprites;
    private readonly HashSet<Vector2i> _occupied = [];
    private readonly HashSet<ProtoId<NCTargetZonePrototype>> _builtZoneMasks = [];
    private readonly Dictionary<ProtoId<NCTargetZonePrototype>, HashSet<Vector2i>> _zoneMasks = [];
    private readonly Dictionary<LayerMaskKey, HashSet<Vector2i>> _layerMasks = [];
    private readonly Dictionary<ProtoId<NCTargetDollProfilePrototype>, WoundLayout> _layouts = [];

    // WorldSpaceBelowFOV is the same proven pass used by vanilla status overlays: it is rendered after entity
    // sprites while still respecting the player's field of view. WorldSpaceEntities can be interleaved at a
    // draw-depth that is never visited on some composed humanoids, which made wound primitives disappear.
    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowFOV;

    public NCWorldWoundOverlay(
        NCWorldWoundOverlaySystem system,
        IEntityManager entities,
        IPrototypeManager prototypes,
        IResourceCache resources,
        IClickMapManager clickMaps)
    {
        _system = system;
        _entities = entities;
        _prototypes = prototypes;
        _resources = resources;
        _clickMaps = clickMaps;
        _transform = entities.System<SharedTransformSystem>();
        _sprites = entities.System<SpriteSystem>();
    }

    public void ClearLayoutCache()
    {
        _layouts.Clear();
        _layerMasks.Clear();
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var medicalQuery = _entities.GetEntityQuery<NCMedicalBodyComponent>();
        var woundQuery = _entities.GetEntityQuery<NCVisibleWoundComponent>();
        var spriteQuery = _entities.GetEntityQuery<SpriteComponent>();
        var transformQuery = _entities.GetEntityQuery<TransformComponent>();
        foreach (var uid in _system.WoundedEntities)
        {
            if (!woundQuery.TryGetComponent(uid, out var wounds) ||
                wounds.Markers.Count == 0 ||
                !medicalQuery.TryGetComponent(uid, out var medical) ||
                !spriteQuery.TryGetComponent(uid, out var sprite) ||
                !sprite.Visible ||
                !transformQuery.TryGetComponent(uid, out var xform) ||
                xform.MapID != args.MapId ||
                !TryGetLayout(medical.Profile, out var layout))
            {
                continue;
            }

            var worldPosition = _transform.GetWorldPosition(xform, transformQuery);
            var spriteBounds = _sprites.GetLocalBounds((uid, sprite)).Translated(worldPosition);
            if (!spriteBounds.Intersects(args.WorldAABB))
                continue;

            DrawEntity(args, (uid, wounds), sprite, xform, transformQuery, layout);
        }

        args.WorldHandle.SetTransform(Matrix3x2.Identity);
    }

    private void DrawEntity(
        in OverlayDrawArgs args,
        Entity<NCVisibleWoundComponent> entity,
        SpriteComponent sprite,
        TransformComponent xform,
        EntityQuery<TransformComponent> transformQuery,
        WoundLayout layout)
    {
        var (worldPosition, worldRotation) = _transform.GetWorldPositionRotation(xform, transformQuery);
        var eyeRotation = args.Viewport.Eye?.Rotation ?? Angle.Zero;
        var screenRotation = (worldRotation + eyeRotation).Reduced().FlipPositive();
        var cardinal = sprite is { NoRotation: false, SnapCardinals: true }
            ? screenRotation.RoundToCardinalAngle()
            : Angle.Zero;
        var entityMatrix = Matrix3Helpers.CreateTransform(
            worldPosition,
            sprite.NoRotation ? -eyeRotation : worldRotation - cardinal);
        var spriteMatrix = Matrix3x2.Multiply(sprite.LocalMatrix, entityMatrix);
        _occupied.Clear();
        _builtZoneMasks.Clear();
        foreach (var mask in _zoneMasks.Values)
            mask.Clear();

        foreach (var marker in entity.Comp.Markers)
        {
            if (!layout.Zones.TryGetValue(marker.Zone, out var zone))
                continue;

            if (!TryGetZoneRenderData(
                entity.Owner,
                sprite,
                marker.Zone,
                zone,
                layout.CanvasSize,
                screenRotation,
                out var layerMatrix,
                out var mask))
            {
                continue;
            }

            if (!TryResolvePoint(marker, zone.Bounds, mask, out var point))
                continue;

            var transform = Matrix3x2.Multiply(layerMatrix, spriteMatrix);
            args.WorldHandle.SetTransform(transform);
            DrawMarker(args.WorldHandle, marker, mask, point, layout.CanvasSize);
        }
    }

    /// <summary>
    /// Resolves the same directional humanoid layer matrix and alpha frame that SpriteSystem renders. This keeps
    /// wound pixels attached to rotated/scaled body parts instead of projecting front-facing rectangles into air.
    /// </summary>
    private bool TryGetZoneRenderData(
        EntityUid entity,
        SpriteComponent sprite,
        ProtoId<NCTargetZonePrototype> zoneId,
        WoundZoneLayout zone,
        Vector2 canvasSize,
        Angle screenRotation,
        out Matrix3x2 layerMatrix,
        out HashSet<Vector2i> mask)
    {
        foreach (var visualLayer in zone.VisualLayers)
        {
            if (!_sprites.TryGetLayer((entity, (SpriteComponent?) sprite), visualLayer, out var layer, false) ||
                !_sprites.IsVisible(layer))
            {
                continue;
            }

            var geometryDirection = layer.ActualState is { } state
                ? SpriteComponent.Layer.GetDirection(state.RsiDirections, screenRotation)
                : RsiDirection.South;
            layer.GetLayerDrawMatrix(geometryDirection, out layerMatrix);

            var layerMask = GetLayerMask(sprite, layer, geometryDirection, canvasSize);
            if (layerMask.Count == 0)
                continue;

            if (!zone.RestrictVertical)
            {
                mask = layerMask;
                return true;
            }

            if (!_zoneMasks.TryGetValue(zoneId, out mask!))
            {
                mask = [];
                _zoneMasks.Add(zoneId, mask);
            }

            if (_builtZoneMasks.Add(zoneId))
            {
                var top = (int) MathF.Floor(zone.Bounds.Bottom);
                var bottom = (int) MathF.Ceiling(zone.Bounds.Top);
                foreach (var cell in layerMask)
                {
                    if (cell.Y >= top && cell.Y < bottom)
                        mask.Add(cell);
                }
            }

            if (mask.Count > 0)
                return true;
        }

        layerMatrix = default;
        mask = default!;
        return false;
    }

    private HashSet<Vector2i> GetLayerMask(
        SpriteComponent sprite,
        SpriteComponent.Layer layer,
        RsiDirection geometryDirection,
        Vector2 canvasSize)
    {
        var canvasPixels = new Vector2i(
            Math.Max(1, (int) MathF.Round(canvasSize.X)),
            Math.Max(1, (int) MathF.Round(canvasSize.Y)));

        if (layer.ActualRsi is { } rsi && layer.ActualState is { } state)
        {
            var renderedDirection = geometryDirection;
            if (sprite.EnableDirectionOverride)
                renderedDirection = sprite.DirectionOverride.Convert(state.RsiDirections);
            renderedDirection = renderedDirection.OffsetRsiDir(layer.DirOffset);

            var key = new LayerMaskKey(rsi, layer.State, renderedDirection, layer.AnimationFrame, canvasPixels);
            if (_layerMasks.TryGetValue(key, out var cached))
                return cached;

            var result = new HashSet<Vector2i>();
            for (var y = 0; y < canvasPixels.Y; y++)
            {
                for (var x = 0; x < canvasPixels.X; x++)
                {
                    var local = CanvasCellToLocal(new Vector2i(x, y), canvasSize);
                    var imagePosition = (Vector2i) (local * EyeManager.PixelsPerMeter * new Vector2(1f, -1f) +
                                                     state.Size / 2f);
                    if (_clickMaps.IsOpaque(rsi, layer.State, renderedDirection, layer.AnimationFrame, imagePosition))
                        result.Add(new Vector2i(x, y));
                }
            }

            _layerMasks.Add(key, result);
            return result;
        }

        if (layer.Texture is { } texture)
        {
            var key = new LayerMaskKey(texture, default, RsiDirection.South, 0, canvasPixels);
            if (_layerMasks.TryGetValue(key, out var cached))
                return cached;

            var result = new HashSet<Vector2i>();
            for (var y = 0; y < canvasPixels.Y; y++)
            {
                for (var x = 0; x < canvasPixels.X; x++)
                {
                    var local = CanvasCellToLocal(new Vector2i(x, y), canvasSize);
                    var imagePosition = (Vector2i) (local * EyeManager.PixelsPerMeter * new Vector2(1f, -1f) +
                                                     texture.Size / 2f);
                    if (_clickMaps.IsOpaque(texture, imagePosition))
                        result.Add(new Vector2i(x, y));
                }
            }

            _layerMasks.Add(key, result);
            return result;
        }

        return [];
    }

    private static Vector2 CanvasCellToLocal(Vector2i cell, Vector2 canvasSize)
    {
        return new Vector2(
            (cell.X + 0.5f - canvasSize.X * 0.5f) / EyeManager.PixelsPerMeter,
            (canvasSize.Y * 0.5f - cell.Y - 0.5f) / EyeManager.PixelsPerMeter);
    }

    private bool TryResolvePoint(
        NCVisibleWoundMarker marker,
        Box2 bounds,
        HashSet<Vector2i> mask,
        out Vector2 point)
    {
        var basePoint = NCWoundMarkerProjection.ResolveBasePoint(marker, bounds);
        var desired = new Vector2i((int) MathF.Floor(basePoint.X), (int) MathF.Floor(basePoint.Y));
        if (!TryFindNearestCell(mask, desired, true, out var resolved) &&
            !TryFindNearestCell(mask, desired, false, out resolved))
        {
            point = default;
            return false;
        }

        _occupied.Add(resolved);
        point = new Vector2(resolved.X, resolved.Y);
        return true;
    }

    private bool TryFindNearestCell(
        HashSet<Vector2i> mask,
        Vector2i desired,
        bool requireFree,
        out Vector2i resolved)
    {
        resolved = default;
        var found = false;
        var bestDistance = int.MaxValue;
        foreach (var candidate in mask)
        {
            if (requireFree && _occupied.Contains(candidate))
                continue;

            var delta = candidate - desired;
            var distance = delta.X * delta.X + delta.Y * delta.Y;
            if (found &&
                (distance > bestDistance ||
                 distance == bestDistance &&
                 (candidate.Y > resolved.Y ||
                  candidate.Y == resolved.Y && candidate.X >= resolved.X)))
            {
                continue;
            }

            found = true;
            bestDistance = distance;
            resolved = candidate;
        }

        return found;
    }

    private static void DrawMarker(
        DrawingHandleWorld handle,
        NCVisibleWoundMarker marker,
        HashSet<Vector2i> mask,
        Vector2 point,
        Vector2 canvasSize)
    {
        var color = WoundColor(marker);
        switch (marker.Type)
        {
            case NCWoundType.Slash:
                DrawSlash(handle, marker, mask, point, canvasSize, color);
                return;
            case NCWoundType.Blunt:
            case NCWoundType.Pressure:
                DrawCell(handle, mask, point, canvasSize, color.WithAlpha(0.82f));
                DrawCell(handle, mask, point + Vector2.UnitX, canvasSize, color.WithAlpha(0.58f));
                DrawCell(handle, mask, point + Vector2.UnitY, canvasSize, color.WithAlpha(0.58f));
                return;
            case NCWoundType.Thermal:
            case NCWoundType.Cold:
            case NCWoundType.Chemical:
            case NCWoundType.Blast:
                DrawCell(handle, mask, point, canvasSize, color);
                DrawCell(handle, mask, point + Vector2.UnitX, canvasSize, color.WithAlpha(0.72f));
                DrawCell(handle, mask, point + Vector2.UnitY, canvasSize, color.WithAlpha(0.72f));
                return;
            case NCWoundType.Electrical:
                DrawCell(handle, mask, point, canvasSize, color);
                DrawCell(handle, mask, point + Vector2.One, canvasSize, color.WithAlpha(0.75f));
                return;
            default:
                // Ballistic and piercing entries remain one source pixel per resolved impact.
                DrawCell(handle, mask, point, canvasSize, color);
                return;
        }
    }

    private static void DrawSlash(
        DrawingHandleWorld handle,
        NCVisibleWoundMarker marker,
        HashSet<Vector2i> mask,
        Vector2 center,
        Vector2 canvasSize,
        Color color)
    {
        var angle = marker.VisualDirection / 255f * MathF.Tau;
        var step = new Vector2(
            MathF.Abs(MathF.Cos(angle)) >= 0.35f ? MathF.Sign(MathF.Cos(angle)) : 0f,
            MathF.Abs(MathF.Sin(angle)) >= 0.35f ? MathF.Sign(MathF.Sin(angle)) : 0f);
        if (step == Vector2.Zero)
            step = Vector2.UnitX;

        var radius = marker.Severity >= NCWoundSeverity.Critical ? 2 : 1;
        for (var index = -radius; index <= radius; index++)
            DrawCell(handle, mask, center + step * index, canvasSize, color);
    }

    private static void DrawCell(
        DrawingHandleWorld handle,
        HashSet<Vector2i> mask,
        Vector2 point,
        Vector2 canvasSize,
        Color color)
    {
        var cell = new Vector2i((int) MathF.Round(point.X), (int) MathF.Round(point.Y));
        if (!mask.Contains(cell))
            return;

        var pixelSize = 1f / EyeManager.PixelsPerMeter;
        var localCenter = new Vector2(
            (cell.X + 0.5f - canvasSize.X * 0.5f) * pixelSize,
            (canvasSize.Y * 0.5f - cell.Y - 0.5f) * pixelSize);
        var size = new Vector2(pixelSize);
        handle.DrawRect(Box2.FromDimensions(localCenter - size * 0.5f, size), color);
    }

    private bool TryGetLayout(ProtoId<NCAnatomyProfilePrototype> profileId, out WoundLayout layout)
    {
        layout = default!;
        if (!_prototypes.TryIndex(profileId, out var profile) ||
            !_prototypes.TryIndex(profile.TargetDoll, out var doll))
        {
            return false;
        }

        if (_layouts.TryGetValue(profile.TargetDoll, out var cached))
        {
            layout = cached;
            return true;
        }

        var layerUsage = new Dictionary<HumanoidVisualLayers, int>();
        foreach (var presentation in doll.Zones)
        {
            foreach (var visualLayer in presentation.WorldVisualLayers)
            {
                layerUsage.TryGetValue(visualLayer, out var count);
                layerUsage[visualLayer] = count + 1;
            }
        }

        var zones = new Dictionary<ProtoId<NCTargetZonePrototype>, WoundZoneLayout>();
        foreach (var presentation in doll.Zones)
        {
            var size = presentation.DisplaySize ?? presentation.SelectedRegion?.Size;
            if (size == null)
            {
                if (!_resources.TryGetResource<TextureResource>(presentation.SelectedTexture, out var texture))
                    continue;
                size = new Vector2(texture.Texture.Size.X, texture.Texture.Size.Y);
            }

            var restrictVertical = presentation.WorldVisualLayers.Any(layer => layerUsage[layer] > 1);
            zones[presentation.Zone] = new WoundZoneLayout(
                Box2.FromDimensions(presentation.Position, size.Value),
                presentation.WorldVisualLayers,
                restrictVertical);
        }

        layout = new WoundLayout(doll.CanvasSize, zones);
        _layouts.Add(profile.TargetDoll, layout);
        return true;
    }

    private static Color WoundColor(NCVisibleWoundMarker marker)
    {
        if (marker.Stage == NCWoundStage.Scarred)
            return Color.FromHex("#C68D92");
        if (marker.Stage is NCWoundStage.Treated or NCWoundStage.Closed or NCWoundStage.Healing)
            return Color.FromHex("#B85D65");

        return marker.Type switch
        {
            NCWoundType.Thermal => Color.FromHex("#FF8A3D"),
            NCWoundType.Electrical => Color.FromHex("#FFD84A"),
            NCWoundType.Blast => Color.FromHex("#FF493D"),
            NCWoundType.Cold => Color.FromHex("#79D7FF"),
            NCWoundType.Pressure => Color.FromHex("#B57AFF"),
            NCWoundType.Chemical => Color.FromHex("#B6E447"),
            NCWoundType.Blunt => Color.FromHex("#A93F78"),
            NCWoundType.Pierce => Color.FromHex("#FF4F69"),
            _ => Color.FromHex("#FF2738"),
        };
    }

    private sealed record WoundLayout(
        Vector2 CanvasSize,
        Dictionary<ProtoId<NCTargetZonePrototype>, WoundZoneLayout> Zones);

    private sealed record WoundZoneLayout(
        Box2 Bounds,
        IReadOnlyList<HumanoidVisualLayers> VisualLayers,
        bool RestrictVertical);

    private readonly record struct LayerMaskKey(
        object Source,
        RSI.StateId State,
        RsiDirection Direction,
        int Frame,
        Vector2i CanvasSize);
}
