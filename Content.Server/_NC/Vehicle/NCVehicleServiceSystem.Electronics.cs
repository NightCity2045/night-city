// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Vehicle.Components;

namespace Content.Server._NC.Vehicle;

public sealed partial class NCVehicleServiceSystem
{
    /// <summary>Read-only physical electronics service eligibility; no calibration or installation is performed.</summary>
    public bool CanServiceElectronics(EntityUid uid, EntityUid user, out uint assemblyRevision)
    {
        assemblyRevision = 0;
        if (!TryComp<NCVehicleComponent>(uid, out var vehicle) || !_prototypes.TryIndex(vehicle.Chassis, out var chassis) ||
            !CanWork(uid, user, chassis) || !TryComp<NCVehicleAssemblyComponent>(uid, out var assembly) || !assembly.HoodOpen ||
            _hands.GetActiveItem(user) is not { } tool || !TryComp<NCVehicleDiagnosticToolComponent>(tool, out var gauge) ||
            gauge.Level < 2 || !gauge.Categories.Contains("RunningGear")) return false;
        foreach (var bench in _lookup.GetEntitiesInRange<NCVehicleWorkshopEquipmentComponent>(Transform(uid).Coordinates, chassis.WorkshopSearchRange))
            if (bench.Comp.Workbench && Transform(bench).Anchored && _interaction.InRangeUnobstructed(uid, bench.Owner, range: bench.Comp.Range))
            { assemblyRevision = assembly.Revision; return true; }
        return false;
    }
}
