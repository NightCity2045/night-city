// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Content.Shared.ActionBlocker;
using Content.Shared.Popups;
using Robust.Shared.Prototypes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;

namespace Content.Shared._NC.Vehicle;

/// <summary>Only the current seated actor can author replayable control state.</summary>
public sealed partial class NCVehicleControlSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private NCVehicleConditionSystem _condition = default!;
    [Dependency] private NCVehicleAccessorySystem _accessories = default!;
    [Dependency] private NCVehicleAccessSystem _access = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCVehicleDriveComponent, NCVehicleInputMessage>(OnInput);
        SubscribeLocalEvent<NCVehicleDriveComponent, NCVehicleDriverActionMessage>(OnAction);
    }

    private void OnInput(Entity<NCVehicleDriveComponent> ent, ref NCVehicleInputMessage args)
    {
        if (ent.Comp.Driver != args.Actor || ent.Comp.Authority != args.Authority ||
            !_blocker.CanConsciouslyPerformAction(args.Actor) || !float.IsFinite(args.Steering) ||
            args.Steering is < -1 or > 1 || !Newer(args.Sequence, ent.Comp.LastInput))
            return;
        ent.Comp.LastInput = args.Sequence;
        ent.Comp.LastInputTime = _timing.CurTime;
        ent.Comp.SteeringTarget = args.Steering;
        ent.Comp.SteeringHeld = args.SteeringHeld;
        ent.Comp.GasHeld = args.Gas;
        ent.Comp.BrakeHeld = args.Brake;
        ent.Comp.ClutchHeld = args.Clutch;
        ent.Comp.HandbrakeHeld = args.Handbrake;
        Dirty(ent);
    }

    private void OnAction(Entity<NCVehicleDriveComponent> ent, ref NCVehicleDriverActionMessage args)
    {
        TryControl(ent, args.Actor, args.Authority, args.Action);
    }

    public bool TryControl(EntityUid uid, EntityUid user, uint authority, NCVehicleDriverAction action)
    {
        if (!TryComp<NCVehicleDriveComponent>(uid, out var drive))
            return false;
        var ent = new Entity<NCVehicleDriveComponent>(uid, drive);
        if (drive.Driver != user || drive.Authority != authority ||
            !_blocker.CanConsciouslyPerformAction(user) ||
            !_prototypes.TryIndex(ent.Comp.Handling, out var tuning))
            return false;
        if (tuning.Automatic && action is NCVehicleDriverAction.GearUp or NCVehicleDriverAction.GearDown or NCVehicleDriverAction.Neutral or NCVehicleDriverAction.Reverse)
            return TrySelectAutomatic(uid, action, tuning);
        switch (action)
        {
            case NCVehicleDriverAction.Lights:
            case NCVehicleDriverAction.Horn:
            case NCVehicleDriverAction.Wash:
                return _accessories.TryUse(uid, user, action);
            case NCVehicleDriverAction.Ignition:
                if (ent.Comp.EngineRunning || ent.Comp.Cranking)
                {
                    ent.Comp.EngineRunning = false;
                    ent.Comp.Cranking = false;
                }
                else
                {
                    if (!CanStartEngine(uid, user, out var reason))
                    {
                        if (reason is { } message)
                            _popup.PopupPredicted(Loc.GetString(message), uid, user);
                        return false;
                    }
                    if (!_condition.TryStart(uid))
                        return false;
                    ent.Comp.Cranking = true;
                    ent.Comp.CrankElapsed = 0;
                    _accessories.PlayStartup(uid, user);
                }
                break;
            case NCVehicleDriverAction.Neutral:
                ent.Comp.PendingShift = null;
                ent.Comp.ShiftStatus = NCVehicleShiftStatus.None;
                ent.Comp.Gear = 0;
                ent.Comp.LeverRow = 0;
                break;
            case NCVehicleDriverAction.GearUp:
            case NCVehicleDriverAction.GearDown:
            case NCVehicleDriverAction.GearLeft:
            case NCVehicleDriverAction.GearRight:
                if (!TryMoveGear(uid, action, tuning))
                    return false;
                break;
            case NCVehicleDriverAction.Reverse:
                ent.Comp.PendingShift = null;
                ent.Comp.ShiftStatus = NCVehicleShiftStatus.None;
                if (tuning.Sequential)
                {
                    if (TryComp<PhysicsComponent>(uid, out var body) &&
                        body.LinearVelocity.Length() > tuning.SelectorMaximumSpeed)
                    {
                        return false;
                    }

                    if (ent.Comp.Clutch < tuning.ShiftClutchThreshold)
                        return TryQueueShift(uid, ent.Comp, action);
                    ent.Comp.Gear = tuning.GearRatios.Length - 1;
                    ent.Comp.LeverRow = 1;
                    ent.Comp.ReverseArmed = false;
                }
                else
                {
                    ent.Comp.ReverseArmed = !ent.Comp.ReverseArmed;
                }
                break;
            default:
                return false;
        }
        Dirty(ent);
        return true;
    }

    /// <summary>Use the same launch interlocks for authoritative control and client feedback.</summary>
    public bool CanStartEngine(EntityUid uid, EntityUid user, out LocId? reason)
    {
        reason = null;
        if (!TryComp<NCVehicleDriveComponent>(uid, out var drive) ||
            drive.Driver != user || !_blocker.CanConsciouslyPerformAction(user) ||
            !_prototypes.TryIndex(drive.Handling, out var tuning))
            reason = "nc-vehicle-start-driver";
        else if (TryComp<NCVehicleRecoveryComponent>(uid, out var recovery) && recovery.Cargo != null && !recovery.Secured)
            reason = "nc-vehicle-start-cargo";
        else if (!_access.CanIgnite(uid, user))
            reason = "nc-vehicle-start-key";
        else if (drive.Fuel <= 0)
            reason = "nc-vehicle-start-fuel";
        else if (!drive.AssemblyReady)
            reason = "nc-vehicle-start-assembly";
        else if (tuning.Automatic ? drive.Selector is not (NCVehicleSelector.Park or NCVehicleSelector.Neutral) :
                     drive.Gear != 0 && drive.Clutch < tuning.ShiftClutchThreshold)
            reason = tuning.Automatic ? "nc-vehicle-start-selector" : "nc-vehicle-start-clutch";
        else
            return _condition.CanStart(uid, out reason);
        return false;
    }

    private bool TryMoveGear(EntityUid uid, NCVehicleDriverAction action, NCVehicleHandlingPrototype tuning)
    {
        var drive = Comp<NCVehicleDriveComponent>(uid);
        // The newest lever request replaces an earlier request, including a return to neutral.
        drive.PendingShift = null;
        drive.ShiftStatus = NCVehicleShiftStatus.None;
        Dirty(uid, drive);
        if (tuning.Automatic)
            return false;
        if (tuning.Sequential)
        {
            if (action is not (NCVehicleDriverAction.GearUp or NCVehicleDriverAction.GearDown))
                return false;
            if (drive.Clutch < tuning.ShiftClutchThreshold)
                return TryQueueShift(uid, drive, action);
            drive.Gear = Math.Clamp(drive.Gear + (action == NCVehicleDriverAction.GearUp ? 1 : -1), 0, tuning.GearRatios.Length - 2);
            return true;
        }
        if (tuning.ManualPattern.Length < 2 || tuning.ManualPattern.Length % 2 != 0)
            return false;
        var horizontal = action == NCVehicleDriverAction.GearLeft ? -1 : action == NCVehicleDriverAction.GearRight ? 1 : 0;
        var vertical = action == NCVehicleDriverAction.GearUp ? -1 : action == NCVehicleDriverAction.GearDown ? 1 : 0;
        var (column, row) = NCVehicleDriveMath.MoveLever(drive.LeverColumn, drive.LeverRow,
            horizontal, vertical, tuning.ManualPattern.Length / 2);
        if (column == drive.LeverColumn && row == drive.LeverRow)
            return false;
        var gear = row == 0 ? 0 : tuning.ManualPattern[column * 2 + (row > 0 ? 1 : 0)];
        if (drive.ReverseArmed && column == tuning.ManualPattern.Length / 2 - 1 && row > 0)
            gear = tuning.GearRatios.Length - 1;
        if (gear < 0 || gear >= tuning.GearRatios.Length)
            return false;
        if (gear != 0 && gear != drive.Gear && drive.Clutch < tuning.ShiftClutchThreshold)
        {
            // Input arrives before the simulated pedal has travelled. Keep that intention while
            // the player holds the clutch instead of dropping a single W/S key press.
            if (drive.ClutchHeld)
                return TryQueueShift(uid, drive, action);
            var speed = 0f;
            var weight = 0f;
            for (var i = 0; i < Math.Min(drive.WheelSpeeds.Length, tuning.Wheels.Count); i++)
            {
                speed += drive.WheelSpeeds[i] * tuning.Wheels[i].DriveWeight;
                weight += tuning.Wheels[i].DriveWeight;
            }
            var shaftRpm = weight > 0 ? speed / weight * tuning.GearRatios[gear] * tuning.FinalDrive * 60 / MathF.Tau : 0;
            if (Math.Abs(shaftRpm - drive.EngineRpm) > tuning.ShiftRpmTolerance)
            {
                // A failed clutchless shift leaves the lever in its old gate and wears the synchronizer.
                drive.GearboxWear = Math.Min(1, drive.GearboxWear + tuning.MisshiftWear);
                drive.ShiftStatus = NCVehicleShiftStatus.ClutchRequired;
                Dirty(uid, drive);
                return false;
            }
        }
        drive.LeverColumn = column;
        drive.LeverRow = row;
        drive.Gear = gear;
        if (gear == tuning.GearRatios.Length - 1)
            drive.ReverseArmed = false;
        return true;
    }

    private bool TryQueueShift(EntityUid uid, NCVehicleDriveComponent drive, NCVehicleDriverAction action)
    {
        drive.PendingShift = drive.ClutchHeld ? action : null;
        drive.ShiftStatus = drive.ClutchHeld ? NCVehicleShiftStatus.WaitingForClutch : NCVehicleShiftStatus.ClutchRequired;
        Dirty(uid, drive);
        return drive.ClutchHeld;
    }

    public bool TryCompleteShift(EntityUid uid)
    {
        if (!TryComp<NCVehicleDriveComponent>(uid, out var drive) || drive.PendingShift is not { } action)
            return false;
        // Releasing the pedal, losing the driver, or incapacitation cancels the request. All checks
        // run in shared physics so prediction and the authoritative server complete the same shift.
        if (!drive.ClutchHeld || drive.Driver is not { } driver || !_blocker.CanConsciouslyPerformAction(driver))
        {
            drive.PendingShift = null;
            drive.ShiftStatus = NCVehicleShiftStatus.None;
            Dirty(uid, drive);
            return false;
        }
        if (!_prototypes.TryIndex(drive.Handling, out var tuning) || drive.Clutch < tuning.ShiftClutchThreshold)
            return false;
        return TryControl(uid, driver, drive.Authority, action);
    }

    private bool TrySelectAutomatic(EntityUid uid, NCVehicleDriverAction action, NCVehicleHandlingPrototype tuning)
    {
        var drive = Comp<NCVehicleDriveComponent>(uid);
        var selected = action == NCVehicleDriverAction.Neutral ? NCVehicleSelector.Neutral :
            action == NCVehicleDriverAction.Reverse ? NCVehicleSelector.Reverse :
            (NCVehicleSelector) Math.Clamp((int) drive.Selector + (action == NCVehicleDriverAction.GearUp ? -1 : 1), 0, 4);
        if (TryComp<PhysicsComponent>(uid, out var body) && body.LinearVelocity.Length() > tuning.SelectorMaximumSpeed &&
            (selected is NCVehicleSelector.Park or NCVehicleSelector.Reverse || drive.Selector == NCVehicleSelector.Reverse))
            return false;
        drive.Selector = selected;
        drive.Gear = selected == NCVehicleSelector.Reverse ? tuning.GearRatios.Length - 1 :
            selected is NCVehicleSelector.Drive or NCVehicleSelector.Low ? 1 : 0;
        Dirty(uid, drive);
        return true;
    }

    /// <summary>Serial-number arithmetic accepts wraparound and rejects duplicate/reordered frames.</summary>
    public static bool Newer(uint candidate, uint previous) => unchecked((int) (candidate - previous)) > 0;
}
