// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Content.Shared.DoAfter;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Vehicle;

public sealed partial class NCVehicleServiceSystem
{
    private void OnTune(Entity<NCVehicleComponent> ent, ref NCVehicleTuneMessage args)
        => TryStartCalibration(ent, args.Actor, args.Calibration);

    public bool CanCalibrate(EntityUid uid, EntityUid user, ProtoId<NCVehicleTuningPrototype> calibration)
    {
        if (!TryComp<NCVehicleComponent>(uid, out var vehicle) || !_prototypes.TryIndex(vehicle.Chassis, out var chassis) ||
            !chassis.Calibrations.Contains(calibration) || !_prototypes.TryIndex(calibration, out var setup) ||
            !CanWork(uid, user, chassis) || !TryComp<NCVehicleAssemblyComponent>(uid, out var assembly) || !assembly.HoodOpen ||
            _hands.GetActiveItem(user) is not { } tool || !TryComp<NCVehicleDiagnosticToolComponent>(tool, out var gauge) ||
            gauge.Level < 2 || !gauge.Categories.Contains("RunningGear") || !NCVehicleCalibrationValidator.IsValid(setup))
            return false;
        foreach (var bench in _lookup.GetEntitiesInRange<NCVehicleWorkshopEquipmentComponent>(Transform(uid).Coordinates, chassis.WorkshopSearchRange))
            if (bench.Comp.Workbench && Transform(bench).Anchored &&
                _interaction.InRangeUnobstructed(uid, bench.Owner, range: bench.Comp.Range))
                return true;
        return false;
    }

    public bool TryStartCalibration(EntityUid uid, EntityUid user, ProtoId<NCVehicleTuningPrototype> calibration)
    {
        if (!CanCalibrate(uid, user, calibration))
            return false;
        var assembly = Comp<NCVehicleAssemblyComponent>(uid);
        if (assembly.ReservedSlots.ContainsKey(string.Empty))
            return false;
        var setup = _prototypes.Index(calibration);
        if (!_doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, setup.Seconds,
            new NCVehicleTuneDoAfterEvent { Calibration = calibration, Revision = assembly.Revision }, uid, uid, _hands.GetActiveItem(user))
            { BreakOnMove = true, BreakOnDamage = true, NeedHand = true }))
            return false;
        assembly.ReservedSlots[string.Empty] = user;
        return true;
    }

    private void OnTuneComplete(Entity<NCVehicleComponent> ent, ref NCVehicleTuneDoAfterEvent args)
    {
        if (args.Handled || !TryComp<NCVehicleAssemblyComponent>(ent, out var assembly) ||
            !assembly.ReservedSlots.TryGetValue(string.Empty, out var worker) || worker != args.User)
            return;
        assembly.ReservedSlots.Remove(string.Empty);
        args.Handled = true;
        if (args.Cancelled || args.Revision != assembly.Revision || args.Used != _hands.GetActiveItem(args.User) ||
            !CanCalibrate(ent, args.User, args.Calibration) || !TryComp<NCVehicleDriveComponent>(ent, out var drive))
            return;
        drive.Calibration = args.Calibration;
        assembly.Revision++;
        Dirty(ent.Owner, drive);
        Refresh(ent);
    }
}
