// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.ZoneTemplates.Components;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared.Interaction;

namespace Content.Server._NC.City.ZoneTemplates;

/// <summary>Executes resolved cross-Z template links while keeping their targets independent of runtime entity IDs.</summary>
public sealed partial class NCVerticalLinkSystem : EntitySystem
{
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCResolvedVerticalLinkComponent, ActivateInWorldEvent>(OnActivate);
    }

    private void OnActivate(Entity<NCResolvedVerticalLinkComponent> entity, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex || !_coordinates.TryResolve(entity.Comp.Target, out var target))
            return;
        args.Handled = true;
        _transform.SetMapCoordinates(args.User, target);
    }
}
