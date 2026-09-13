// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Client.Graphics;
using Robust.Shared;
using Robust.Shared.Configuration;

namespace Content.Client._NC.UserInterface;

/// <summary>
/// Resolves automatic UI scale once at content startup so a transient zero DPI report cannot invalidate autoscaling.
/// </summary>
public sealed partial class NCUiScaleGuardSystem : EntitySystem
{
    private const float MinimumScale = 0.5f;

    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IConfigurationManager _configuration = default!;

    public override void Initialize()
    {
        base.Initialize();

        if (_configuration.GetCVar(CVars.DisplayUIScale) > 0)
            return;

        // Some Windows/SDL transitions report zero content scale for one event. A fixed valid value keeps the
        // engine's autoscale bounds ordered without patching RobustToolbox.
        var detected = _clyde.DefaultWindowScale.X;
        var scale = float.IsFinite(detected) && detected > 0 ? Math.Max(MinimumScale, detected) : 1f;
        _configuration.SetCVar(CVars.DisplayUIScale, scale);
    }
}
