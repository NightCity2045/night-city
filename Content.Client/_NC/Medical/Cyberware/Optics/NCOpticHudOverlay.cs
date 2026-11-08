// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;

namespace Content.Client._NC.Medical.Cyberware.Optics;

public sealed class NCOpticHudOverlay : Overlay
{
    private readonly NCCyberOpticsSystem _optics;
    private readonly Font _font;
    public override OverlaySpace Space => OverlaySpace.ScreenSpace;
    public NCOpticHudOverlay(NCCyberOpticsSystem optics, IResourceCache resources)
    {
        _optics = optics;
        _font = new VectorFont(resources.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 12);
    }
    protected override void Draw(in OverlayDrawArgs args)
    {
        if (_optics.View is not { } view || view.Telemetry.Length == 0) return;
        args.ScreenHandle.DrawString(_font, new Vector2(24, 80), view.Telemetry, Color.Cyan);
    }
}
