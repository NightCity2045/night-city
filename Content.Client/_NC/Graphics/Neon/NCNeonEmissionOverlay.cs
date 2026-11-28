// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Numerics;
using Content.Client.Light;
using Content.Shared._NC.Graphics;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Graphics.Neon;

/// <summary>
/// Adds the glow of <see cref="NCNeonEmissionComponent"/> entities to the enlarged light target,
/// after roofs and tile emission and before the content light blur softens it.
/// </summary>
public sealed class NCNeonEmissionOverlay : Overlay
{
    private static readonly ProtoId<ShaderPrototype> NeonShader = "NCNeonEmission";

    public override OverlaySpace Space => OverlaySpace.BeforeLighting;

    private readonly IOverlayManager _overlays;
    private readonly EntityLookupSystem _lookup;
    private readonly SharedTransformSystem _transform;
    private readonly EntityQuery<PointLightComponent> _lights;
    private readonly ShaderInstance _shader;
    private readonly HashSet<Entity<NCNeonEmissionComponent>> _emitters = new();

    public NCNeonEmissionOverlay(IEntityManager entities, IOverlayManager overlays, IPrototypeManager prototypes)
    {
        _overlays = overlays;
        _lookup = entities.System<EntityLookupSystem>();
        _transform = entities.System<SharedTransformSystem>();
        _lights = entities.GetEntityQuery<PointLightComponent>();
        _shader = prototypes.Index(NeonShader).Instance();

        // Light blur runs at TileEmissionOverlay.ContentZIndex + 1, so this still lands before it.
        ZIndex = TileEmissionOverlay.ContentZIndex;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var viewport = args.Viewport;
        var eye = viewport.Eye;
        if (eye == null || !_overlays.TryGetOverlay<BeforeLightTargetOverlay>(out var lightOverlay))
            return;

        _emitters.Clear();
        _lookup.GetEntitiesIntersecting(args.MapId, lightOverlay.EnlargedBounds, _emitters);
        if (_emitters.Count == 0)
            return;

        var handle = args.WorldHandle;
        var target = lightOverlay.GetCachedForViewport(viewport).EnlargedLightTarget;
        var lightScale = viewport.LightRenderTarget.Size / (Vector2) viewport.Size;
        var scale = viewport.RenderScale / (Vector2.One / lightScale);

        handle.RenderInRenderTarget(target,
            () =>
            {
                var invMatrix = target.GetWorldToLocalMatrix(eye, scale);
                handle.UseShader(_shader);

                foreach (var (uid, emission) in _emitters)
                {
                    var color = emission.Color;
                    if (_lights.TryComp(uid, out var light))
                    {
                        if (!light.Enabled || light.ContainerOccluded)
                            continue;

                        color ??= light.Color;
                    }

                    if (color is not { } glow)
                        continue;

                    handle.SetTransform(Matrix3x2.Multiply(_transform.GetWorldMatrix(uid), invMatrix));
                    // The additive blend scales the colour by its alpha.
                    handle.DrawRect(Box2.CenteredAround(emission.Offset, emission.Size),
                        glow.WithAlpha(Math.Clamp(emission.Intensity, 0f, 1f)));
                }

                handle.UseShader(null);
            },
            null);
    }
}
