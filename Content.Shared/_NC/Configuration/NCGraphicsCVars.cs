// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared;
using Robust.Shared.Configuration;

namespace Content.Shared._NC.Configuration;

/// <summary>
/// Client-side configuration for the Night City post-processing pipeline.
/// </summary>
[CVarDefs]
public sealed class NCGraphicsCVars : CVars
{
    public static readonly CVarDef<bool> CelShading =
        CVarDef.Create("graphics.cel_shading", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<float> CelShadingStrength =
        CVarDef.Create("graphics.cel_shading_strength", 0.8f, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<float> FilmGrainStrength =
        CVarDef.Create("graphics.film_grain_strength", 60f, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<bool> FilmGrain =
        CVarDef.Create("graphics.film_grain", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<bool> Bloom =
        CVarDef.Create("graphics.bloom", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<int> BloomIntensity =
        CVarDef.Create("graphics.bloom_intensity", 150, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<float> BloomThreshold =
        CVarDef.Create("graphics.bloom_threshold", 0.5f, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<bool> Vignette =
        CVarDef.Create("graphics.vignette", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<float> VignetteRadius =
        CVarDef.Create("graphics.vignette_radius", 0.8f, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<float> VignetteSoftness =
        CVarDef.Create("graphics.vignette_softness", 0.5f, CVar.CLIENTONLY | CVar.ARCHIVE);
}
