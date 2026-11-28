// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Configuration;
using Robust.Client.Graphics;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Graphics.Neon;

/// <summary>
/// Keeps the neon glow overlay in place while the neon glow setting is on.
/// </summary>
public sealed partial class NCNeonEmissionSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private NCNeonEmissionOverlay? _overlay;

    public override void Initialize()
    {
        base.Initialize();

        Subs.CVar(_configuration, NCGraphicsCVars.NeonGlow, OnNeonGlowChanged, true);
    }

    public override void Shutdown()
    {
        RemoveOverlay();

        base.Shutdown();
    }

    private void OnNeonGlowChanged(bool enabled)
    {
        if (!enabled)
        {
            RemoveOverlay();
            return;
        }

        if (_overlay != null)
            return;

        _overlay = new NCNeonEmissionOverlay(EntityManager, _overlays, _prototypes);
        _overlays.AddOverlay(_overlay);
    }

    private void RemoveOverlay()
    {
        if (_overlay == null)
            return;

        _overlays.RemoveOverlay(_overlay);
        _overlay = null;
    }
}
