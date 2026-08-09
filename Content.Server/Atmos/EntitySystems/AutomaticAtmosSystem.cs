using Content.Server.Shuttles.Systems;
// NC start
using Content.Server._NC.Atmos.Components;
// NC end
using Content.Shared.Atmos.Components;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Events;

namespace Content.Server.Atmos.EntitySystems;

/// <summary>
/// Handles automatically adding a grid atmosphere to grids that become large enough, allowing players to build shuttles
/// with a sealed atmosphere from scratch.
/// </summary>
public sealed partial class AutomaticAtmosSystem : EntitySystem
{
    [Dependency] private AtmosphereSystem _atmosphereSystem = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MapGridComponent, MassDataChangedEvent>(OnMassDataChanged);
    }

    private void OnMassDataChanged(Entity<MapGridComponent> ent, ref MassDataChangedEvent ev)
    {
        // NC start: Night City maps use immutable ambient air and must never allocate per-tile atmos state.
        var mapUid = Transform(ent).MapUid;
        if (HasComp<NCUniformAirComponent>(ent) ||
            mapUid != null && HasComp<NCUniformAirComponent>(mapUid.Value))
        {
            return;
        }
        // NC end

        if (_atmosphereSystem.HasAtmosphere(ent))
            return;

        // We can't actually count how many tiles there are efficiently, so instead estimate with the mass.
        if (ev.NewMass / ShuttleSystem.TileDensityMultiplier >= 7.0f)
        {
            AddComp<GridAtmosphereComponent>(ent);
            Log.Info($"Giving grid {ent} GridAtmosphereComponent.");
        }

        // It's not super important to remove it should the grid become too small again.
        // If explosions ever gain the ability to outright shatter grids, do rethink this.

        return;
    }
}
