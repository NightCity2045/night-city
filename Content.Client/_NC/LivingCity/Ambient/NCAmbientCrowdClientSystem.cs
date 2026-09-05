// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Ambient;
using Robust.Client.Graphics;

namespace Content.Client._NC.LivingCity.Ambient;

/// <summary>
/// Owns the non-authoritative decorative crowd projection and its world overlay.
/// </summary>
public sealed partial class NCAmbientCrowdClientSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlays = default!;

    private NCAmbientCrowdOverlay? _overlay;

    public IReadOnlyList<NCAmbientDecorativeVisual> Visuals { get; private set; } = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCAmbientDecorativeSnapshot>(OnSnapshot);
    }

    public override void Shutdown()
    {
        RemoveOverlay();
        Visuals = [];
        base.Shutdown();
    }

    private void OnSnapshot(NCAmbientDecorativeSnapshot snapshot)
    {
        Visuals = snapshot.Active ? snapshot.Visuals : [];
        if (!snapshot.Active || snapshot.Visuals.Length == 0)
        {
            RemoveOverlay();
            return;
        }

        if (_overlay != null)
            return;
        _overlay = new NCAmbientCrowdOverlay(this);
        _overlays.AddOverlay(_overlay);
    }

    private void RemoveOverlay()
    {
        if (_overlay != null)
            _overlays.RemoveOverlay(_overlay);
        _overlay = null;
    }
}
