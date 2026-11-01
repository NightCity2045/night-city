// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.Body;
using Content.Shared._NC.Medical.Cyberware.Visuals;
using Content.Shared.Body;
using Robust.Client.GameObjects;
using Robust.Shared.GameStates;

namespace Content.Client._NC.Medical.Cyberware;

/// <summary>Loose parts display the same sanitized finish as the anatomical layer, including after detachment.</summary>
public sealed partial class NCCyberwareLoosePartVisualSystem : EntitySystem
{
    [Dependency] private SpriteSystem _sprites = default!;
    [Dependency] private IComponentFactory _components = default!;
    [Dependency] private VisualBodySystem _visual = default!;

    public override void Initialize()
    {
        base.Initialize();
        _visual.OrganVisualChanged += Refresh;
    }

    public override void Shutdown()
    {
        _visual.OrganVisualChanged -= Refresh;
        base.Shutdown();
    }

    private void Refresh(EntityUid uid)
    {
        if (!TryComp<OrganComponent>(uid, out var organ) || organ.Body != null ||
            !TryComp<VisualOrganComponent>(uid, out var visual) || !TryComp<SpriteComponent>(uid, out var sprite) ||
            MetaData(uid).EntityPrototype is not { } prototype ||
            !prototype.HasComp<NCCyberwareExteriorComponent>(_components)) return;
        _sprites.LayerSetData((uid, sprite), 0, visual.Data);
    }
}
