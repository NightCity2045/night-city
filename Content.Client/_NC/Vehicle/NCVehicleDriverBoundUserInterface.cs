// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Vehicle;

public sealed partial class NCVehicleDriverBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    [Dependency] private IInputManager _input = default!;
    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IUserInterfaceManager _ui = default!;
    private NCVehicleDriverHud? _hud;
    private TimeSpan _nextInput;
    private uint _sequence;
    private bool _gearUp;
    private bool _gearDown;
    private bool _gearLeft;
    private bool _gearRight;

    protected override void Open()
    {
        base.Open();
        // Initial state reconciliation can enqueue the same open more than once before the next frame.
        if (_hud != null)
            return;
        _hud = new NCVehicleDriverHud();
        var viewport = _ui.ActiveScreen?.FindControl<LayoutContainer>("ViewportContainer");
        if (viewport != null)
        {
            viewport.AddChild(_hud);
            LayoutContainer.SetAnchorPreset(_hud, LayoutContainer.LayoutPreset.Wide);
        }
        else
        {
            _ui.StateRoot.AddChild(_hud);
            LayoutContainer.SetAnchorPreset(_hud, LayoutContainer.LayoutPreset.Wide);
        }
        _hud.DriverAction += SendAction;
        _hud.BeltRequested += SendBelt;
        _hud.InfrastructureAction += travel =>
        {
            if (EntMan.TryGetComponent<NCVehicleDriveComponent>(Owner, out var drive))
                SendMessage(new NCVehicleInfrastructureMessage(drive.Authority, travel));
        };
        _hud.Tick += Update;
        _input.Contexts.SetActiveContext("nc-vehicle");
    }

    public override void Update()
    {
        base.Update();
        if (_hud == null || !EntMan.TryGetComponent<NCVehicleDriveComponent>(Owner, out var drive) ||
            !_prototypes.TryIndex(drive.Handling, out var tuning))
            return;
        if (_timing.RealTime < _nextInput)
            return;
        var speed = EntMan.TryGetComponent<PhysicsComponent>(Owner, out var body) ? body.LinearVelocity.Length() : 0;
        var gear = drive.Gear == 0 ? Loc.GetString("nc-vehicle-gear-n") :
            drive.Gear == tuning.GearRatios.Length - 1 ? Loc.GetString("nc-vehicle-gear-r") : drive.Gear.ToString();
        _hud.TelemetryText = Loc.GetString("nc-vehicle-telemetry", ("speed", MathF.Round(speed * 3.6f)),
            ("rpm", MathF.Round(drive.EngineRpm)), ("gear", gear), ("fuel", MathF.Round(drive.Fuel, 1)));
        _hud.IgnitionText = Loc.GetString(drive.EngineRunning || drive.Cranking
            ? "nc-vehicle-ignition-stop" : "nc-vehicle-ignition-start");
        // Diagnose from replicated state through the same shared interlocks used by the start action.
        if (drive.Cranking)
            _hud.EngineStatusText = Loc.GetString("nc-vehicle-engine-cranking");
        else if (!drive.EngineRunning)
        {
            LocId? reason = null;
            if (drive.Driver is { } driver)
                EntMan.System<NCVehicleControlSystem>().CanStartEngine(Owner, driver, out reason);
            _hud.EngineStatusText = Loc.GetString(reason ?? "nc-vehicle-engine-stopped");
        }
        else
            _hud.EngineStatusText = Loc.GetString(drive.EngineRpm >= tuning.RedlineRpm * tuning.HighRpmWearStart
                ? "nc-vehicle-engine-high-rpm" : drive.HandbrakeHeld ? "nc-vehicle-launch-handbrake" :
                !tuning.Automatic && drive.Gear != 0 && drive.ClutchHeld ? "nc-vehicle-launch-release-clutch" :
                "nc-vehicle-engine-running");
        _hud.PedalsText = Loc.GetString("nc-vehicle-pedals", ("gas", MathF.Round(drive.Throttle * 100)),
            ("brake", MathF.Round(drive.Brake * 100)), ("clutch", MathF.Round(drive.Clutch * 100)));
        _hud.SetPedals(drive.Throttle, drive.Brake, drive.Clutch);
        _hud.ShiftStatusText = Loc.GetString(drive.ShiftStatus switch
        {
            NCVehicleShiftStatus.ClutchRequired => "nc-vehicle-shift-clutch-required",
            NCVehicleShiftStatus.WaitingForClutch => "nc-vehicle-shift-waiting",
            _ => drive.Gear == 0 ? "nc-vehicle-shift-neutral" : "nc-vehicle-shift-engaged",
        });
        _hud.LaunchText = Loc.GetString(tuning.Automatic ? "nc-vehicle-launch-automatic" : "nc-vehicle-launch-manual");
        var belted = drive.Driver is { } seated && EntMan.TryGetComponent<NCVehicleOccupantComponent>(seated, out var occupant) && occupant.Belted;
        _hud.SafetyText = Loc.GetString(belted ? "nc-vehicle-seatbelt-fastened" : "nc-vehicle-seatbelt-unfastened");
        _hud.SeatbeltText = Loc.GetString(belted ? "nc-vehicle-seatbelt-unfasten" : "nc-vehicle-seatbelt-fasten");
        _hud.SetSafetyVisual(belted);
        _hud.DrivingHintText = Loc.GetString(!drive.EngineRunning || drive.Gear <= 0 || tuning.Automatic
            ? "nc-vehicle-shift-advice-none"
            : drive.EngineRpm >= tuning.RedlineRpm * tuning.ShiftAdviceUpFraction && drive.Gear < tuning.GearRatios.Length - 2
                ? "nc-vehicle-shift-advice-up"
                : drive.EngineRpm < tuning.IdleRpm * tuning.ShiftAdviceDownRpmFactor && drive.Gear > 1
                    ? "nc-vehicle-shift-advice-down" : "nc-vehicle-shift-advice-ready");
        _hud.GripStatusText = Loc.GetString(drive.LateralSlip >= tuning.SlipWarning ? "nc-vehicle-grip-sliding" :
            drive.LongitudinalSlip >= tuning.SlipWarning ? "nc-vehicle-grip-wheelspin" : "nc-vehicle-grip-stable");
        if (EntMan.TryGetComponent<NCVehicleAccessoriesComponent>(Owner, out var accessories))
            _hud.AccessoriesText = Loc.GetString("nc-vehicle-accessories",
                ("cranking", Loc.GetString(drive.Cranking ? "nc-vehicle-switch-on" : "nc-vehicle-switch-off")),
                ("lights", Loc.GetString(accessories.Headlights ? "nc-vehicle-switch-on" : "nc-vehicle-switch-off")),
                ("dirt", MathF.Round(accessories.WindshieldDirt * 100))) +
                (drive.Overturned ? "\n" + Loc.GetString("nc-vehicle-overturned") : string.Empty);
        if (EntMan.TryGetComponent<NCVehicleConditionComponent>(Owner, out var condition))
        {
            _hud.ConditionText = Loc.GetString("nc-vehicle-condition",
                ("engine", MathF.Round(condition.EngineTemperature - 273.15f)),
                ("brakes", MathF.Round(condition.BrakeTemperature - 273.15f)),
                ("battery", MathF.Round(condition.BatteryCharge, 1)));
            _hud.FluidsText = Loc.GetString("nc-vehicle-fluids", ("oil", MathF.Round(condition.OilFraction * 100)),
                ("coolant", MathF.Round(condition.CoolantFraction * 100)), ("fluid", MathF.Round(condition.BrakeFluidFraction * 100))) + "\n" +
                Loc.GetString("nc-vehicle-service-fluids", ("transmission", MathF.Round(condition.TransmissionFraction * 100)),
                    ("steering", MathF.Round(condition.SteeringFluidFraction * 100)), ("washer", MathF.Round(condition.WasherFraction * 100)),
                    ("pressure", MathF.Round(condition.OilPressure)));
        }
        _hud.GearText = tuning.Automatic ? Loc.GetString("nc-vehicle-selector", ("position", drive.Selector switch
        {
            NCVehicleSelector.Park => "P",
            NCVehicleSelector.Reverse => "R",
            NCVehicleSelector.Drive => "D",
            NCVehicleSelector.Low => "L",
            _ => "N",
        })) : Loc.GetString("nc-vehicle-lever-state", ("column", drive.LeverColumn + 1),
            ("row", Loc.GetString(drive.LeverRow == 0 ? "nc-vehicle-gear-n" :
                drive.LeverRow < 0 ? "nc-vehicle-gear-up" : "nc-vehicle-gear-down")),
            ("reverse", Loc.GetString(drive.ReverseArmed ? "nc-vehicle-reverse-armed" : "nc-vehicle-reverse-locked")));
        _hud.SetShifter(tuning.Automatic, drive.LeverColumn, drive.LeverRow, drive.Selector, drive.ReverseArmed);
        var temperature = 20f;
        var engineWarning = !drive.EngineRunning && !drive.Cranking;
        var oilWarning = false;
        var batteryWarning = false;
        if (EntMan.TryGetComponent<NCVehicleConditionComponent>(Owner, out var instrumentCondition))
        {
            temperature = instrumentCondition.EngineTemperature - 273.15f;
            engineWarning |= temperature >= 115;
            oilWarning = instrumentCondition.OilFraction < 0.2f ||
                drive.EngineRunning && instrumentCondition.OilPressure <= 20;
            batteryWarning = instrumentCondition.BatteryCharge < 8;
        }
        _hud.SetInstruments(speed * 3.6f, drive.EngineRpm, tuning.RedlineRpm,
            drive.Fuel / Math.Max(0.1f, tuning.FuelCapacity), temperature, gear, engineWarning, oilWarning,
            batteryWarning, drive.HandbrakeHeld, Math.Max(drive.LongitudinalSlip, drive.LateralSlip) >= tuning.SlipWarning);
        _nextInput = _timing.RealTime + TimeSpan.FromSeconds(tuning.InputInterval);
        // Typing in chat must not hold the pedals or steering keys.
        var focused = _clyde.IsFocused && _ui.KeyboardFocused is not LineEdit && _ui.KeyboardFocused is not TextEdit;
        if (!focused || Held(EngineKeyFunctions.CloseModals) || Held(EngineKeyFunctions.EscapeMenu))
            ReleaseSteering();
        var gas = focused && Held(NCVehicleKeyFunctions.Throttle);
        var brake = focused && Held(NCVehicleKeyFunctions.Brake);
        var clutch = focused && Held(NCVehicleKeyFunctions.Clutch);
        var handbrake = focused && Held(NCVehicleKeyFunctions.Handbrake);
        _hud.SteeringMaximumAngle = tuning.SteeringWheelTravel;
        var steering = _hud.SteeringHeld ? _hud.SteeringValue : drive.Steering;
        if (focused && Held(NCVehicleKeyFunctions.SteerLeft))
            steering = Math.Min(1, steering + tuning.KeyboardSteeringRate * tuning.InputInterval);
        if (focused && Held(NCVehicleKeyFunctions.SteerRight))
            steering = Math.Max(-1, steering - tuning.KeyboardSteeringRate * tuning.InputInterval);
        _hud.SteeringValue = steering;
        // Serial arithmetic remains valid when the uint sequence wraps.
        if (NCVehicleControlSystem.Newer(drive.LastInput, _sequence))
            _sequence = drive.LastInput;
        _sequence = unchecked(_sequence + 1);
        SendPredictedMessage(new NCVehicleInputMessage(drive.Authority, _sequence, focused ? steering : 0,
            gas, brake, clutch, handbrake, _hud.SteeringHeld ||
            (focused && (Held(NCVehicleKeyFunctions.SteerLeft) || Held(NCVehicleKeyFunctions.SteerRight)))));
        // Arrow keys move the physical selector by one detent per press; drag remains available on the HUD.
        UpdateGearKey(NCVehicleKeyFunctions.GearUp, NCVehicleDriverAction.GearUp, focused, ref _gearUp);
        UpdateGearKey(NCVehicleKeyFunctions.GearDown, NCVehicleDriverAction.GearDown, focused, ref _gearDown);
        UpdateGearKey(NCVehicleKeyFunctions.GearLeft, NCVehicleDriverAction.GearLeft, focused, ref _gearLeft);
        UpdateGearKey(NCVehicleKeyFunctions.GearRight, NCVehicleDriverAction.GearRight, focused, ref _gearRight);
    }

    private void UpdateGearKey(BoundKeyFunction function, NCVehicleDriverAction action, bool focused, ref bool previous)
    {
        var pressed = focused && Held(function);
        if (pressed && !previous)
            SendAction(action);
        previous = pressed;
    }

    private bool Held(BoundKeyFunction function)
    {
        foreach (var binding in _input.GetKeyBindings(function))
            if (binding.State == BoundKeyState.Down)
                return true;
        return false;
    }

    private void ReleaseSteering() => _hud?.ReleaseSteeringGrip();

    private void SendAction(NCVehicleDriverAction action)
    {
        if (EntMan.TryGetComponent<NCVehicleDriveComponent>(Owner, out var drive))
        {
            // Moving the occupant out of its container is an authoritative server action.
            if (action == NCVehicleDriverAction.Exit)
                SendMessage(new NCVehicleExitMessage(drive.Authority));
            else
                SendPredictedMessage(new NCVehicleDriverActionMessage(drive.Authority, action));
        }
    }

    private void SendBelt()
    {
        if (EntMan.TryGetComponent<NCVehicleDriveComponent>(Owner, out var drive))
            SendMessage(new NCVehicleBeltMessage(drive.Authority));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseSteering();
            EntMan.System<InputSystem>().SetEntityContextActive();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            _hud?.Orphan();
            _hud = null;
        }
    }
}
