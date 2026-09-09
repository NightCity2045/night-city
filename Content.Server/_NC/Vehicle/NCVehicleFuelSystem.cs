// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;

namespace Content.Server._NC.Vehicle;

public sealed partial class NCVehicleFuelSystem : EntitySystem
{
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCVehicleFuelComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<NCVehicleFuelComponent, SolutionChangedEvent>(OnChanged);
        SubscribeLocalEvent<NCVehicleFuelComponent, NCVehicleFuelConsumedEvent>(OnConsumed);
    }

    private void OnMapInit(Entity<NCVehicleFuelComponent> ent, ref MapInitEvent args) => Refresh(ent);
    private void OnChanged(Entity<NCVehicleFuelComponent> ent, ref SolutionChangedEvent args) => Refresh(ent);

    private void OnConsumed(Entity<NCVehicleFuelComponent> ent, ref NCVehicleFuelConsumedEvent args)
    {
        if (!float.IsFinite(args.Litres) || args.Litres <= 0 || !float.IsFinite(ent.Comp.UnitsPerLitre) || ent.Comp.UnitsPerLitre <= 0)
            return;
        ent.Comp.PendingLitres += args.Litres;
        var quantity = FixedPoint2.New(ent.Comp.PendingLitres * ent.Comp.UnitsPerLitre);
        if (quantity <= FixedPoint2.Zero)
        {
            Refresh(ent);
            return;
        }
        if (_solutions.TryGetSolution(ent.Owner, ent.Comp.Solution, out var reservoir))
        {
            // Remove debt before the synchronous chemistry notification recomputes usable fuel.
            ent.Comp.PendingLitres = Math.Max(0, ent.Comp.PendingLitres - quantity.Float() / ent.Comp.UnitsPerLitre);
            _solutions.RemoveReagent(reservoir.Value, ent.Comp.Reagent.Id, quantity);
        }
        Refresh(ent);
    }

    private void Refresh(Entity<NCVehicleFuelComponent> ent)
    {
        if (!TryComp<NCVehicleDriveComponent>(ent, out var drive))
            return;
        drive.Fuel = float.IsFinite(ent.Comp.UnitsPerLitre) && ent.Comp.UnitsPerLitre > 0 &&
            _solutions.TryGetSolution(ent.Owner, ent.Comp.Solution, out _, out var solution) ?
            Math.Max(0, solution.GetTotalPrototypeQuantity(ent.Comp.Reagent).Float() / ent.Comp.UnitsPerLitre - ent.Comp.PendingLitres) : 0;
        if (drive.Fuel <= 0)
            drive.EngineRunning = false;
        Dirty(ent.Owner, drive);
    }
}
