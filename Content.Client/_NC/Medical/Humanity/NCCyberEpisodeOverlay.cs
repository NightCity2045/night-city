// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using System.Linq;
using Content.Shared._NC.Medical.Humanity;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;

namespace Content.Client._NC.Medical.Humanity;

/// <summary>Owner-only perception. Synthetic images do not query hidden world entities or create gameplay objects.</summary>
public sealed class NCCyberEpisodeOverlay : Overlay
{
    private readonly NCCyberEpisodeClientSystem _system;
    private readonly Font _font;
    private readonly Font _title;
    private readonly Texture[] _stages;
    public override OverlaySpace Space => OverlaySpace.ScreenSpace;
    public NCCyberEpisodeOverlay(NCCyberEpisodeClientSystem system, IResourceCache resources)
    {
        _system = system;
        _font = new VectorFont(resources.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 13);
        _title = new VectorFont(resources.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 17);
        _stages = new[] { "stable", "warning", "crisis", "acute", "recovery" }.Select(name =>
            resources.GetResource<RSIResource>("/Textures/_NC/Interface/Cyberpsychosis/episodes.rsi").RSI[name].GetFrame(Robust.Shared.Graphics.RSI.RsiDirection.South, 0)).ToArray();
    }
    protected override void Draw(in OverlayDrawArgs args)
    {
        if (_system.View is not { } view || view.Status.Burden <= 0f && view.Status.Stage == NCCyberCrisisStage.Stable) return;
        var h = args.ScreenHandle;
        var bounds = args.ViewportBounds;
        var stage = view.Status.Stage;
        var color = stage switch
        {
            NCCyberCrisisStage.Warning => Color.FromHex("#E6C16B"),
            NCCyberCrisisStage.Crisis => Color.FromHex("#F58D54"),
            NCCyberCrisisStage.Acute => Color.FromHex("#ED5268"),
            _ => Color.FromHex("#62CEB9"),
        };
        var origin = new Vector2(bounds.Left + 74, bounds.Bottom - 230);
        h.DrawRect(UIBox2.FromDimensions(origin, new Vector2(466, 96)), Color.FromHex("#08141C").WithAlpha(.9f));
        h.DrawRect(UIBox2.FromDimensions(origin, new Vector2(3, 96)), color);
        h.DrawString(_title, origin + new Vector2(68, 24), Loc.GetString(NCCyberEpisodeClientSystem.StageKey(stage)), color);
        h.DrawString(_font, origin + new Vector2(68, 60), Loc.GetString("nc-psychosis-hud", ("stress", MathF.Round(view.Status.Stress)),
            ("exhaustion", MathF.Round(view.Status.Exhaustion))), Color.LightGray);
        h.DrawRect(UIBox2.FromDimensions(origin + new Vector2(68, 84), new Vector2(380, 4)), Color.FromHex("#263B46"));
        h.DrawRect(UIBox2.FromDimensions(origin + new Vector2(68, 84), new Vector2(3.8f * view.Status.Stress, 4)), color);
        h.DrawTextureRect(_stages[(int) stage], UIBox2.FromDimensions(origin + new Vector2(9, 9), new Vector2(52)));
        if (!_system.ReducedEffects && stage is NCCyberCrisisStage.Crisis or NCCyberCrisisStage.Acute)
        {
            var thickness = stage == NCCyberCrisisStage.Acute ? 12f : 5f;
            h.DrawRect(UIBox2.FromDimensions(new Vector2(bounds.Left, bounds.Top), new Vector2(thickness, bounds.Height)), color.WithAlpha(.35f));
            h.DrawRect(UIBox2.FromDimensions(new Vector2(bounds.Right - thickness, bounds.Top), new Vector2(thickness, bounds.Height)), color.WithAlpha(.35f));
        }
        if (_system.Symptom is not { } symptom || _system.SymptomRemaining <= 0f) return;
        var center = new Vector2(bounds.Left + bounds.Width * .5f, bounds.Top + bounds.Height * .5f);
        // No flashing, camera shake, full-screen blackout or UI input interception.
        h.DrawString(_font, new Vector2(bounds.Left + 90, bounds.Bottom - 247), Loc.GetString(symptom.Message), color);
        if (_system.ReducedEffects) return;
    }
}
