// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Client.Graphics;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Graphics.PostProcessing;

/// <summary>
/// Owns the Night City world post-processing overlay for the lifetime of the client entity manager.
/// </summary>
public sealed partial class NCPostProcessClientSystem : EntitySystem
{
    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private NCPostProcessOverlay? _overlay;

    public override void Initialize()
    {
        base.Initialize();

        _overlay = new NCPostProcessOverlay(_clyde, _configuration, _prototypes);
        if (_overlays.AddOverlay(_overlay))
            return;

        // A duplicate overlay should never survive a client entity-manager restart.
        _overlay.Dispose();
        _overlay = null;
    }

    public override void Shutdown()
    {
        if (_overlay != null)
        {
            _overlays.RemoveOverlay(_overlay);
            _overlay.Dispose();
            _overlay = null;
        }

        base.Shutdown();
    }
}
