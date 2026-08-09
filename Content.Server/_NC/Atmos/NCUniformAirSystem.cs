// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Atmos.Components;
using Content.Shared.Atmos.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._NC.Atmos;

/// <summary>
/// Removes per-tile atmosphere state from maps that use one immutable ambient air mixture.
/// </summary>
public sealed partial class NCUniformAirSystem : EntitySystem
{
    [Dependency] private SharedMapSystem _maps = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCUniformAirComponent, ComponentStartup>(OnUniformAirStartup);
    }

    private void OnUniformAirStartup(Entity<NCUniformAirComponent> entity, ref ComponentStartup args)
    {
        if (!TryComp(entity.Owner, out MapComponent? map))
            return;

        // The marker may be applied after an already initialized Z-level joins its network.
        // Strip any atmosphere state that the vanilla automatic system created during map loading.
        foreach (var grid in _maps.GetAllGrids(map.MapId))
            RemoveGridAtmosphere(grid.Owner);
    }

    private void RemoveGridAtmosphere(EntityUid gridUid)
    {
        RemComp<GridAtmosphereComponent>(gridUid);
        RemComp<GasTileOverlayComponent>(gridUid);
    }
}
