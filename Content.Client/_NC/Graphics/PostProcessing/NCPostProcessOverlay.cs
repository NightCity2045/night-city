// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Client.Graphics;
using Content.Shared._NC.Configuration;
using Robust.Client.Graphics;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Graphics.PostProcessing;

/// <summary>
/// Applies light-driven cel shading, color grading, bloom, vignette, and film grain to the world.
/// </summary>
public sealed class NCPostProcessOverlay : Overlay
{
    private static readonly ProtoId<ShaderPrototype> ThresholdShader = "NCThreshold";
    private static readonly ProtoId<ShaderPrototype> PostProcessShader = "NCPostProcess";

    private const int PostProcessZIndex = 100;
    private const float BloomBlurStrength = 2f;

    private readonly IClyde _clyde;
    private readonly IConfigurationManager _configuration;
    private readonly ILightManager _lights;
    private readonly IEntityManager _entities;
    private readonly ShaderInstance _thresholdShader;
    private readonly ShaderInstance _postProcessShader;
    private readonly OverlayResourceCache<CachedResources> _resources = new();

    private bool _celShadingEnabled;
    private float _celShadingStrength;
    private bool _bloomEnabled;
    private int _bloomIntensity;
    private float _bloomThreshold;
    private bool _vignetteEnabled;
    private float _vignetteRadius;
    private float _vignetteSoftness;
    private bool _filmGrainEnabled;
    private float _filmGrainStrength;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;
    public override bool RequestScreenTexture { get; set; } = true;

    public NCPostProcessOverlay(
        IClyde clyde,
        IConfigurationManager configuration,
        IPrototypeManager prototypes,
        ILightManager lights,
        IEntityManager entities)
    {
        _clyde = clyde;
        _configuration = configuration;
        _lights = lights;
        _entities = entities;
        _thresholdShader = prototypes.Index(ThresholdShader).InstanceUnique();
        _postProcessShader = prototypes.Index(PostProcessShader).InstanceUnique();
        ZIndex = PostProcessZIndex;

        // Cache archived settings so the render hot path does not perform repeated CVar lookups.
        _configuration.OnValueChanged(NCGraphicsCVars.CelShading, OnCelShadingChanged, true);
        _configuration.OnValueChanged(NCGraphicsCVars.CelShadingStrength, OnCelShadingStrengthChanged, true);
        _configuration.OnValueChanged(NCGraphicsCVars.Bloom, OnBloomChanged, true);
        _configuration.OnValueChanged(NCGraphicsCVars.BloomIntensity, OnBloomIntensityChanged, true);
        _configuration.OnValueChanged(NCGraphicsCVars.BloomThreshold, OnBloomThresholdChanged, true);
        _configuration.OnValueChanged(NCGraphicsCVars.Vignette, OnVignetteChanged, true);
        _configuration.OnValueChanged(NCGraphicsCVars.VignetteRadius, OnVignetteRadiusChanged, true);
        _configuration.OnValueChanged(NCGraphicsCVars.VignetteSoftness, OnVignetteSoftnessChanged, true);
        _configuration.OnValueChanged(NCGraphicsCVars.FilmGrain, OnFilmGrainChanged, true);
        _configuration.OnValueChanged(NCGraphicsCVars.FilmGrainStrength, OnFilmGrainStrengthChanged, true);
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        return args.MapId != MapId.Nullspace;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var screenTexture = ScreenTexture;
        var viewport = args.Viewport;
        var eye = viewport.Eye;
        if (screenTexture == null || eye == null)
            return;

        var worldHandle = args.WorldHandle;
        var worldBounds = args.WorldBounds;
        var resources = _resources.GetForViewport(viewport, static _ => new CachedResources());

        if (_bloomEnabled)
        {
            var size = viewport.Size;
            var bloomSize = new Vector2i(Math.Max(1, size.X / 2), Math.Max(1, size.Y / 2));
            EnsureBloomTargets(resources, bloomSize);

            var originalTransform = worldHandle.GetTransform();
            var bloomScale = (Vector2) bloomSize / (Vector2) size;
            var bloomTransform = resources.BloomTarget!.GetWorldToLocalMatrix(
                eye,
                viewport.RenderScale * bloomScale);

            _thresholdShader.SetParameter("SCREEN_TEXTURE", screenTexture);
            _thresholdShader.SetParameter("threshold", _bloomThreshold);

            worldHandle.RenderInRenderTarget(
                resources.BloomTarget,
                () =>
                {
                    worldHandle.SetTransform(bloomTransform);
                    worldHandle.UseShader(_thresholdShader);
                    worldHandle.DrawRect(worldBounds, Color.White);
                    worldHandle.UseShader(null);
                },
                Color.Black);

            _clyde.BlurRenderTarget(
                viewport,
                resources.BloomTarget,
                resources.BlurBuffer!,
                eye,
                BloomBlurStrength);

            worldHandle.SetTransform(originalTransform);
        }

        _postProcessShader.SetParameter("SCREEN_TEXTURE", screenTexture);
        _postProcessShader.SetParameter("celEnabled", _celShadingEnabled);
        _postProcessShader.SetParameter("celStrength", _celShadingStrength);
        // WorldSpace overlays run after Clyde unbinds lighting. Bind the viewport's actual
        // light buffer explicitly, and never sample a stale buffer in unlit/debug views.
        var celLighting = _celShadingEnabled && _celShadingStrength > 0f &&
            _lights.Enabled && _lights.DrawLighting && eye.DrawLight &&
            _entities.TryGetComponent<MapComponent>(args.MapUid, out var map) && map.LightingEnabled;
        _postProcessShader.SetParameter("celLighting", celLighting);
        if (celLighting)
            _postProcessShader.SetParameter("LIGHT_TEXTURE", viewport.LightRenderTarget.Texture);

        _postProcessShader.SetParameter("bloomEnabled", _bloomEnabled);
        if (_bloomEnabled && resources.BloomTarget != null)
        {
            _postProcessShader.SetParameter("BLOOM_TEXTURE", resources.BloomTarget.Texture);
            // Preserve the original Night City 2045 bloom scaling.
            _postProcessShader.SetParameter("bloomIntensity", _bloomIntensity / 100f);
        }

        _postProcessShader.SetParameter("vignetteEnabled", _vignetteEnabled);
        if (_vignetteEnabled)
        {
            _postProcessShader.SetParameter("vignetteRadius", _vignetteRadius);
            _postProcessShader.SetParameter("vignetteSoftness", _vignetteSoftness);
            _postProcessShader.SetParameter("vignetteColor", Color.Black);
        }

        _postProcessShader.SetParameter("grainEnabled", _filmGrainEnabled);
        if (_filmGrainEnabled)
            _postProcessShader.SetParameter("grainStrength", _filmGrainStrength / 100f);

        worldHandle.UseShader(_postProcessShader);
        worldHandle.DrawRect(worldBounds, Color.White);
        worldHandle.UseShader(null);
    }

    protected override void DisposeBehavior()
    {
        _configuration.UnsubValueChanged(NCGraphicsCVars.CelShading, OnCelShadingChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.CelShadingStrength, OnCelShadingStrengthChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.Bloom, OnBloomChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.BloomIntensity, OnBloomIntensityChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.BloomThreshold, OnBloomThresholdChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.Vignette, OnVignetteChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.VignetteRadius, OnVignetteRadiusChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.VignetteSoftness, OnVignetteSoftnessChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.FilmGrain, OnFilmGrainChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.FilmGrainStrength, OnFilmGrainStrengthChanged);
        _resources.Dispose();

        base.DisposeBehavior();
    }

    private void EnsureBloomTargets(CachedResources resources, Vector2i size)
    {
        if (resources.BloomTarget?.Size == size && resources.BlurBuffer?.Size == size)
            return;

        resources.Dispose();
        resources.BloomTarget = _clyde.CreateRenderTarget(
            size,
            new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8Srgb),
            name: "nc-post-process-bloom");
        resources.BlurBuffer = _clyde.CreateRenderTarget(
            size,
            new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8Srgb),
            name: "nc-post-process-bloom-blur");
    }

    private void OnCelShadingChanged(bool value)
    {
        _celShadingEnabled = value;
    }

    private void OnCelShadingStrengthChanged(float value)
    {
        _celShadingStrength = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0.8f;
    }

    private void OnBloomChanged(bool value)
    {
        _bloomEnabled = value;
    }

    private void OnBloomIntensityChanged(int value)
    {
        _bloomIntensity = value;
    }

    private void OnBloomThresholdChanged(float value)
    {
        _bloomThreshold = value;
    }

    private void OnVignetteChanged(bool value)
    {
        _vignetteEnabled = value;
    }

    private void OnVignetteRadiusChanged(float value)
    {
        _vignetteRadius = value;
    }

    private void OnVignetteSoftnessChanged(float value)
    {
        _vignetteSoftness = value;
    }

    private void OnFilmGrainChanged(bool value)
    {
        _filmGrainEnabled = value;
    }

    private void OnFilmGrainStrengthChanged(float value)
    {
        _filmGrainStrength = value;
    }

    private sealed class CachedResources : IDisposable
    {
        public IRenderTexture? BloomTarget;
        public IRenderTexture? BlurBuffer;

        public void Dispose()
        {
            BloomTarget?.Dispose();
            BlurBuffer?.Dispose();
            BloomTarget = null;
            BlurBuffer = null;
        }
    }
}
