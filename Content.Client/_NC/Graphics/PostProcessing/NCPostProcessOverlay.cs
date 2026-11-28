// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Client.Graphics;
using Content.Shared._NC.Configuration;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._NC.Graphics.PostProcessing;

/// <summary>
/// Applies the Night City bloom, colour grade, vignette, and film grain to the rendered world.
/// </summary>
/// <remarks>
/// Bloom runs at two scales: a half-resolution tight halo and a quarter-resolution wide spill built from it.
/// The grade is a 32x32x32 lookup table, so artists can restyle the city by editing a copy of the neutral table.
/// </remarks>
public sealed class NCPostProcessOverlay : Overlay
{
    private static readonly ProtoId<ShaderPrototype> ThresholdShader = "NCThreshold";
    private static readonly ProtoId<ShaderPrototype> DownsampleShader = "NCBloomDownsample";
    private static readonly ProtoId<ShaderPrototype> PostProcessShader = "NCPostProcess";
    private static readonly ResPath GradeTable = new("/Textures/_NC/Shaders/LUT/night_city.png");

    private const int PostProcessZIndex = 100;
    private const float TightBlurStrength = 1.5f;
    private const float WideBlurStrength = 5f;
    private const float TightWeight = 0.6f;
    private const float WideWeight = 0.9f;
    private const float BloomKnee = 0.25f;

    private readonly IClyde _clyde;
    private readonly IConfigurationManager _configuration;
    private readonly ShaderInstance _thresholdShader;
    private readonly ShaderInstance _downsampleShader;
    private readonly ShaderInstance _postProcessShader;
    private readonly Texture _gradeTable;
    private readonly OverlayResourceCache<CachedResources> _resources = new();

    private bool _bloomEnabled;
    private int _bloomIntensity;
    private float _bloomThreshold;
    private bool _gradeEnabled;
    private int _gradeStrength;
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
        IResourceCache resources)
    {
        _clyde = clyde;
        _configuration = configuration;
        _thresholdShader = prototypes.Index(ThresholdShader).InstanceUnique();
        _downsampleShader = prototypes.Index(DownsampleShader).InstanceUnique();
        _postProcessShader = prototypes.Index(PostProcessShader).InstanceUnique();
        _gradeTable = resources.GetResource<TextureResource>(GradeTable).Texture;
        ZIndex = PostProcessZIndex;

        // Cache archived settings so the render hot path does not perform repeated CVar lookups.
        _configuration.OnValueChanged(NCGraphicsCVars.Bloom, OnBloomChanged, true);
        _configuration.OnValueChanged(NCGraphicsCVars.BloomIntensity, OnBloomIntensityChanged, true);
        _configuration.OnValueChanged(NCGraphicsCVars.BloomThreshold, OnBloomThresholdChanged, true);
        _configuration.OnValueChanged(NCGraphicsCVars.ColorGrade, OnColorGradeChanged, true);
        _configuration.OnValueChanged(NCGraphicsCVars.ColorGradeStrength, OnColorGradeStrengthChanged, true);
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
            var tightSize = new Vector2i(Math.Max(1, size.X / 2), Math.Max(1, size.Y / 2));
            var wideSize = new Vector2i(Math.Max(1, size.X / 4), Math.Max(1, size.Y / 4));
            EnsureBloomTargets(resources, tightSize, wideSize);

            var originalTransform = worldHandle.GetTransform();

            // Tight halo: bright pixels of the lit scene at half resolution.
            _thresholdShader.SetParameter("SCREEN_TEXTURE", screenTexture);
            _thresholdShader.SetParameter("threshold", _bloomThreshold);
            _thresholdShader.SetParameter("knee", BloomKnee);
            DrawFullscreenPass(worldHandle, eye, viewport, worldBounds, resources.TightTarget!, _thresholdShader);
            _clyde.BlurRenderTarget(viewport, resources.TightTarget!, resources.TightBlur!, eye, TightBlurStrength);

            // Wide spill: the blurred halo shrunk again and blurred further, so neon washes over the street.
            _downsampleShader.SetParameter("SOURCE_TEXTURE", resources.TightTarget!.Texture);
            _downsampleShader.SetParameter("sourcePixelSize", Vector2.One / tightSize);
            DrawFullscreenPass(worldHandle, eye, viewport, worldBounds, resources.WideTarget!, _downsampleShader);
            _clyde.BlurRenderTarget(viewport, resources.WideTarget!, resources.WideBlur!, eye, WideBlurStrength);

            worldHandle.SetTransform(originalTransform);
        }

        _postProcessShader.SetParameter("SCREEN_TEXTURE", screenTexture);
        _postProcessShader.SetParameter("bloomEnabled", _bloomEnabled);
        if (_bloomEnabled && resources.TightTarget != null && resources.WideTarget != null)
        {
            _postProcessShader.SetParameter("BLOOM_TEXTURE", resources.TightTarget.Texture);
            _postProcessShader.SetParameter("BLOOM_WIDE_TEXTURE", resources.WideTarget.Texture);
            _postProcessShader.SetParameter("bloomIntensity", _bloomIntensity / 100f);
            _postProcessShader.SetParameter("bloomTightWeight", TightWeight);
            _postProcessShader.SetParameter("bloomWideWeight", WideWeight);
        }

        var grade = _gradeEnabled && _gradeStrength > 0;
        _postProcessShader.SetParameter("gradeEnabled", grade);
        if (grade)
        {
            _postProcessShader.SetParameter("LUT_TEXTURE", _gradeTable);
            _postProcessShader.SetParameter("gradeStrength", _gradeStrength / 100f);
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
        _configuration.UnsubValueChanged(NCGraphicsCVars.Bloom, OnBloomChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.BloomIntensity, OnBloomIntensityChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.BloomThreshold, OnBloomThresholdChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.ColorGrade, OnColorGradeChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.ColorGradeStrength, OnColorGradeStrengthChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.Vignette, OnVignetteChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.VignetteRadius, OnVignetteRadiusChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.VignetteSoftness, OnVignetteSoftnessChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.FilmGrain, OnFilmGrainChanged);
        _configuration.UnsubValueChanged(NCGraphicsCVars.FilmGrainStrength, OnFilmGrainStrengthChanged);
        _resources.Dispose();

        base.DisposeBehavior();
    }

    /// <summary>
    /// Covers a whole bloom target with one shader pass whose UVs line up with the screen.
    /// </summary>
    private static void DrawFullscreenPass(
        DrawingHandleWorld worldHandle,
        IEye eye,
        IClydeViewport viewport,
        Box2Rotated worldBounds,
        IRenderTexture target,
        ShaderInstance shader)
    {
        var scale = (Vector2) target.Size / (Vector2) viewport.Size;
        var transform = target.GetWorldToLocalMatrix(eye, viewport.RenderScale * scale);

        worldHandle.RenderInRenderTarget(
            target,
            () =>
            {
                worldHandle.SetTransform(transform);
                worldHandle.UseShader(shader);
                worldHandle.DrawRect(worldBounds, Color.White);
                worldHandle.UseShader(null);
            },
            Color.Black);
    }

    private void EnsureBloomTargets(CachedResources resources, Vector2i tightSize, Vector2i wideSize)
    {
        if (resources.TightTarget?.Size == tightSize && resources.WideTarget?.Size == wideSize)
            return;

        resources.Dispose();
        resources.TightTarget = CreateBloomTarget(tightSize, "nc-post-process-bloom");
        resources.TightBlur = CreateBloomTarget(tightSize, "nc-post-process-bloom-blur");
        resources.WideTarget = CreateBloomTarget(wideSize, "nc-post-process-bloom-wide");
        resources.WideBlur = CreateBloomTarget(wideSize, "nc-post-process-bloom-wide-blur");
    }

    private IRenderTexture CreateBloomTarget(Vector2i size, string name)
    {
        return _clyde.CreateRenderTarget(
            size,
            new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8Srgb),
            name: name);
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

    private void OnColorGradeChanged(bool value)
    {
        _gradeEnabled = value;
    }

    private void OnColorGradeStrengthChanged(int value)
    {
        _gradeStrength = value;
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
        public IRenderTexture? TightTarget;
        public IRenderTexture? TightBlur;
        public IRenderTexture? WideTarget;
        public IRenderTexture? WideBlur;

        public void Dispose()
        {
            TightTarget?.Dispose();
            TightBlur?.Dispose();
            WideTarget?.Dispose();
            WideBlur?.Dispose();
            TightTarget = null;
            TightBlur = null;
            WideTarget = null;
            WideBlur = null;
        }
    }
}
