// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Workshop;
using Content.Server.Fluids.EntitySystems;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Content.Shared.ActionBlocker;

namespace Content.Server._NC.Vehicle;

public sealed partial class NCVehicleFluidsSystem : EntitySystem
{
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private PuddleSystem _puddles = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCVehicleFluidsComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<NCVehicleFluidsComponent, SolutionChangedEvent>(OnChanged);
        SubscribeLocalEvent<NCVehicleFluidsComponent, InteractUsingEvent>(OnInteract);
        SubscribeLocalEvent<NCVehicleFluidsComponent, NCVehicleMaintenanceEvent>(OnMaintenance);
        SubscribeLocalEvent<NCVehicleFluidsComponent, NCVehicleWasherUsedEvent>(OnWasherUsed);
    }

    private void OnWasherUsed(Entity<NCVehicleFluidsComponent> ent, ref NCVehicleWasherUsedEvent args)
    {
        if (ent.Comp.Circuits.TryGetValue(NCVehicleFluidCircuit.Washer, out var circuit) &&
            float.IsFinite(args.Volume) && args.Volume > 0 && _solutions.TryGetSolution(ent.Owner, circuit.Solution, out var reservoir))
            _solutions.SplitSolution(reservoir.Value, FixedPoint2.New(args.Volume));
    }

    private void OnMaintenance(Entity<NCVehicleFluidsComponent> ent, ref NCVehicleMaintenanceEvent args)
    {
        if (!TryComp<NCVehicleAssemblyComponent>(ent, out var assembly) || !float.IsFinite(args.Seconds) || args.Seconds <= 0)
            return;
        foreach (var circuit in ent.Comp.Circuits.Values)
        {
            if (!float.IsFinite(circuit.MaximumLeakPerSecond) || circuit.MaximumLeakPerSecond <= 0)
                continue;
            var seal = 1f;
            foreach (var slot in circuit.SealSlots)
                seal = Math.Min(seal, assembly.Installed.TryGetValue(slot, out var part) && part.Connected ? part.Condition : 0);
            if (seal >= 1 || !_solutions.TryGetSolution(ent.Owner, circuit.Solution, out var reservoir))
                continue;
            // Split the actual mixture, preserving contamination and notifying fuel and mass observers.
            var leaked = _solutions.SplitSolution(reservoir.Value,
                FixedPoint2.New((1 - seal) * circuit.MaximumLeakPerSecond * args.Seconds));
            _puddles.TrySpillAt(Transform(ent).Coordinates, leaked, out _, sound: false);
        }
    }

    private void OnMapInit(Entity<NCVehicleFluidsComponent> ent, ref MapInitEvent args) => Refresh(ent);
    private void OnChanged(Entity<NCVehicleFluidsComponent> ent, ref SolutionChangedEvent args) => Refresh(ent);
    private void OnInteract(Entity<NCVehicleFluidsComponent> ent, ref InteractUsingEvent args)
    {
        if (!args.Handled)
            args.Handled = TryFill(ent, args.User, args.Used);
    }

    public bool TryFill(EntityUid uid, EntityUid user, EntityUid bottle)
    {
        if (!TryComp<NCVehicleFluidsComponent>(uid, out var fluids) ||
            !TryComp<NCVehicleFluidBottleComponent>(bottle, out var source) ||
            !fluids.Circuits.TryGetValue(source.Circuit, out var circuit) ||
            !float.IsFinite(source.TransferVolume) || source.TransferVolume <= 0 ||
            !TryComp<NCVehicleDriveComponent>(uid, out var drive) || drive.EngineRunning ||
            !TryComp<NCVehicleAssemblyComponent>(uid, out var assembly) || !assembly.HoodOpen ||
            !_blocker.CanConsciouslyPerformAction(user) || !_interaction.InRangeUnobstructed(user, uid) ||
            !_interaction.InRangeUnobstructed(user, bottle) ||
            !_solutions.TryGetSolution(uid, circuit.Solution, out var target) ||
            !_solutions.TryGetSolution(bottle, source.Solution, out var reservoir, out var solution))
            return false;
        if (!_solutions.TryTransferSolution(target.Value, solution, FixedPoint2.New(source.TransferVolume)))
            return false;
        // The transfer API updates the target; explicitly notify the source reservoir as well.
        _solutions.UpdateChemicals(reservoir.Value);
        return true;
    }

    /// <summary>Workshop-port transfer after the workshop system has checked access and reserved the assembly.</summary>
    public bool TryFillAtPort(EntityUid uid, EntityUid bottle, NCVehicleFluidCircuit circuitKind)
    {
        if (!TryComp<NCVehicleFluidsComponent>(uid, out var fluids) ||
            !TryComp<NCVehicleFluidBottleComponent>(bottle, out var source) || source.Circuit != circuitKind ||
            !fluids.Circuits.TryGetValue(circuitKind, out var circuit) ||
            !float.IsFinite(source.TransferVolume) || source.TransferVolume <= 0 ||
            !_solutions.TryGetSolution(uid, circuit.Solution, out var target) ||
            !_solutions.TryGetSolution(bottle, source.Solution, out var reservoir, out var solution) ||
            !_solutions.TryTransferSolution(target.Value, solution, FixedPoint2.New(source.TransferVolume)))
            return false;
        _solutions.UpdateChemicals(reservoir.Value);
        return true;
    }

    /// <summary>Moves the real mixture, including contamination, into a drain pan with finite capacity.</summary>
    public bool TryDrainAtPort(EntityUid uid, EntityUid pan, NCVehicleFluidCircuit circuitKind)
    {
        if (!TryComp<NCVehicleFluidsComponent>(uid, out var fluids) ||
            !TryComp<NCVehicleFluidDrainPanComponent>(pan, out var drain) ||
            !fluids.Circuits.TryGetValue(circuitKind, out var circuit) ||
            !float.IsFinite(drain.TransferVolume) || drain.TransferVolume <= 0 ||
            !_solutions.TryGetSolution(uid, circuit.Solution, out var source, out var sourceSolution) ||
            !_solutions.TryGetSolution(pan, drain.Solution, out var target, out var targetSolution))
            return false;
        var amount = FixedPoint2.Min(FixedPoint2.New(drain.TransferVolume), sourceSolution.Volume, targetSolution.AvailableVolume);
        if (amount <= FixedPoint2.Zero)
            return false;
        var removed = _solutions.SplitSolution(source.Value, amount);
        return _solutions.TryAddSolution(target.Value, removed);
    }

    public NCVehicleWorkshopFluidState[] GetWorkshopState(EntityUid uid)
    {
        if (!TryComp<NCVehicleFluidsComponent>(uid, out var fluids))
            return [];
        var result = new List<NCVehicleWorkshopFluidState>(fluids.Circuits.Count);
        foreach (var (kind, circuit) in fluids.Circuits)
        {
            if (!_solutions.TryGetSolution(uid, circuit.Solution, out _, out var solution))
                continue;
            var capacity = Math.Max(circuit.WorkingVolume, 0);
            var desired = solution.GetTotalPrototypeQuantity(circuit.Reagent).Float();
            var volume = solution.Volume.Float();
            var fraction = capacity > 0 ? Math.Clamp(desired / capacity, 0, 1) : 0;
            var purity = volume > 0 ? Math.Clamp(desired / volume, 0, 1) : 1;
            result.Add(new(kind, fraction, purity, volume, capacity));
        }
        return result.ToArray();
    }

    private void Refresh(Entity<NCVehicleFluidsComponent> ent)
    {
        if (!TryComp<NCVehicleConditionComponent>(ent, out var state))
            return;
        state.OilFraction = 0;
        state.CoolantFraction = 0;
        state.BrakeFluidFraction = 0;
        state.TransmissionFraction = 1;
        state.SteeringFluidFraction = 1;
        state.WasherFraction = 0;
        foreach (var (kind, circuit) in ent.Comp.Circuits)
        {
            var fraction = float.IsFinite(circuit.WorkingVolume) && circuit.WorkingVolume > 0 &&
                _solutions.TryGetSolution(ent.Owner, circuit.Solution, out _, out var solution) ?
                Math.Clamp(solution.GetTotalPrototypeQuantity(circuit.Reagent).Float() / circuit.WorkingVolume, 0, 1) : 0;
            switch (kind)
            {
                case NCVehicleFluidCircuit.Oil: state.OilFraction = fraction; break;
                case NCVehicleFluidCircuit.Coolant: state.CoolantFraction = fraction; break;
                case NCVehicleFluidCircuit.Brakes: state.BrakeFluidFraction = fraction; break;
                case NCVehicleFluidCircuit.Transmission: state.TransmissionFraction = fraction; break;
                case NCVehicleFluidCircuit.Steering: state.SteeringFluidFraction = fraction; break;
                case NCVehicleFluidCircuit.Washer: state.WasherFraction = fraction; break;
            }
        }
        Dirty(ent.Owner, state);
    }
}
