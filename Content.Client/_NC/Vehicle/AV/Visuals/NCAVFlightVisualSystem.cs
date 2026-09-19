// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Client.Graphics;
using Robust.Client.Player;

namespace Content.Client._NC.Vehicle.AV.Visuals;

/// <summary>
/// Owns the world overlay that communicates the AV's fractional altitude between Z-level transitions.
/// </summary>
public sealed partial class NCAVFlightVisualSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IPlayerManager _players = default!;

    private NCAVFlightOverlay? _overlay;

    public override void Initialize()
    {
        base.Initialize();

        _overlay = new NCAVFlightOverlay(EntityManager, _players);
        _overlays.AddOverlay(_overlay);
    }

    public override void Shutdown()
    {
        if (_overlay != null)
            _overlays.RemoveOverlay(_overlay);

        _overlay = null;
        base.Shutdown();
    }
}
