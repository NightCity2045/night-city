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
    private NCVehicleDriverWindow? _window;
    private TimeSpan _nextInput;
    private uint _sequence;
    private bool _up;
    private bool _down;
    private bool _left;
    private bool _right;

    protected override void Open()
    {
        base.Open();
        // Initial state reconciliation can enqueue the same open more than once before the next frame.
        if (_window != null)
            return;
        _window = this.CreateWindow<NCVehicleDriverWindow>();
        _window.DriverAction += SendAction;
        _window.InfrastructureAction += travel =>
        {
            if (EntMan.TryGetComponent<NCVehicleDriveComponent>(Owner, out var drive))
                SendMessage(new NCVehicleInfrastructureMessage(drive.Authority, travel));
        };
        _window.Tick += Update;
        _input.Contexts.SetActiveContext("nc-vehicle");
    }

    public override void Update()
    {
        base.Update();
        if (_window == null || !EntMan.TryGetComponent<NCVehicleDriveComponent>(Owner, out var drive) ||
            !_prototypes.TryIndex(drive.Handling, out var tuning))
            return;
        if (_timing.RealTime < _nextInput)
            return;
        var speed = EntMan.TryGetComponent<PhysicsComponent>(Owner, out var body) ? body.LinearVelocity.Length() : 0;
        var gear = drive.Gear == 0 ? Loc.GetString("nc-vehicle-gear-n") :
            drive.Gear == tuning.GearRatios.Length - 1 ? Loc.GetString("nc-vehicle-gear-r") : drive.Gear.ToString();
        _window.TelemetryText = Loc.GetString("nc-vehicle-telemetry", ("speed", MathF.Round(speed * 3.6f)),
            ("rpm", MathF.Round(drive.EngineRpm)), ("gear", gear), ("fuel", MathF.Round(drive.Fuel, 1)));
        _window.IgnitionText = Loc.GetString(drive.EngineRunning || drive.Cranking
            ? "nc-vehicle-ignition-stop" : "nc-vehicle-ignition-start");
        // Diagnose from replicated state through the same shared interlocks used by the start action.
        if (drive.Cranking)
            _window.EngineStatusText = Loc.GetString("nc-vehicle-engine-cranking");
        else if (!drive.EngineRunning)
        {
            LocId? reason = null;
            if (drive.Driver is { } driver)
                EntMan.System<NCVehicleControlSystem>().CanStartEngine(Owner, driver, out reason);
            _window.EngineStatusText = Loc.GetString(reason ?? "nc-vehicle-engine-stopped");
        }
        else
            _window.EngineStatusText = Loc.GetString(drive.EngineRpm >= tuning.RedlineRpm * tuning.HighRpmWearStart
                ? "nc-vehicle-engine-high-rpm" : drive.HandbrakeHeld ? "nc-vehicle-launch-handbrake" :
                !tuning.Automatic && drive.Gear != 0 && drive.ClutchHeld ? "nc-vehicle-launch-release-clutch" :
                "nc-vehicle-engine-running");
        _window.PedalsText = Loc.GetString("nc-vehicle-pedals", ("gas", MathF.Round(drive.Throttle * 100)),
            ("brake", MathF.Round(drive.Brake * 100)), ("clutch", MathF.Round(drive.Clutch * 100)));
        _window.ShiftStatusText = Loc.GetString(drive.ShiftStatus switch
        {
            NCVehicleShiftStatus.ClutchRequired => "nc-vehicle-shift-clutch-required",
            NCVehicleShiftStatus.WaitingForClutch => "nc-vehicle-shift-waiting",
            _ => drive.Gear == 0 ? "nc-vehicle-shift-neutral" : "nc-vehicle-shift-engaged",
        });
        _window.LaunchText = Loc.GetString(tuning.Automatic ? "nc-vehicle-launch-automatic" : "nc-vehicle-launch-manual");
        if (EntMan.TryGetComponent<NCVehicleAccessoriesComponent>(Owner, out var accessories))
            _window.AccessoriesText = Loc.GetString("nc-vehicle-accessories",
                ("cranking", Loc.GetString(drive.Cranking ? "nc-vehicle-switch-on" : "nc-vehicle-switch-off")),
                ("lights", Loc.GetString(accessories.Headlights ? "nc-vehicle-switch-on" : "nc-vehicle-switch-off")),
                ("dirt", MathF.Round(accessories.WindshieldDirt * 100))) +
                (drive.Overturned ? "\n" + Loc.GetString("nc-vehicle-overturned") : string.Empty);
        if (EntMan.TryGetComponent<NCVehicleConditionComponent>(Owner, out var condition))
        {
            _window.ConditionText = Loc.GetString("nc-vehicle-condition",
                ("engine", MathF.Round(condition.EngineTemperature - 273.15f)),
                ("brakes", MathF.Round(condition.BrakeTemperature - 273.15f)),
                ("battery", MathF.Round(condition.BatteryCharge, 1)));
            _window.FluidsText = Loc.GetString("nc-vehicle-fluids", ("oil", MathF.Round(condition.OilFraction * 100)),
                ("coolant", MathF.Round(condition.CoolantFraction * 100)), ("fluid", MathF.Round(condition.BrakeFluidFraction * 100))) + "\n" +
                Loc.GetString("nc-vehicle-service-fluids", ("transmission", MathF.Round(condition.TransmissionFraction * 100)),
                    ("steering", MathF.Round(condition.SteeringFluidFraction * 100)), ("washer", MathF.Round(condition.WasherFraction * 100)),
                    ("pressure", MathF.Round(condition.OilPressure)));
        }
        _window.GearText = tuning.Automatic ? Loc.GetString("nc-vehicle-selector", ("position", drive.Selector switch
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
        _window.SetShifter(tuning.Automatic, drive.LeverColumn, drive.LeverRow, drive.Selector, drive.ReverseArmed);
        _nextInput = _timing.RealTime + TimeSpan.FromSeconds(tuning.InputInterval);
        // Typing in chat must not hold the accelerator or operate the gearbox.
        var focused = _clyde.IsFocused && _ui.KeyboardFocused is not LineEdit && _ui.KeyboardFocused is not TextEdit;
        if (!focused || Held(EngineKeyFunctions.CloseModals) || Held(EngineKeyFunctions.EscapeMenu))
            ReleaseSteering();
        var gas = focused && Held(NCVehicleKeyFunctions.Throttle);
        var brake = focused && Held(NCVehicleKeyFunctions.Brake);
        var clutch = focused && Held(NCVehicleKeyFunctions.Clutch);
        var handbrake = focused && Held(NCVehicleKeyFunctions.Handbrake);
        _window.SteeringMaximumAngle = tuning.SteeringWheelTravel;
        var steering = _window.SteeringHeld ? _window.SteeringValue : drive.Steering;
        if (focused && Held(NCVehicleKeyFunctions.SteerLeft))
            steering = Math.Min(1, steering + tuning.KeyboardSteeringRate * tuning.InputInterval);
        if (focused && Held(NCVehicleKeyFunctions.SteerRight))
            steering = Math.Max(-1, steering - tuning.KeyboardSteeringRate * tuning.InputInterval);
        _window.SteeringValue = steering;
        // Serial arithmetic remains valid when the uint sequence wraps.
        if (NCVehicleControlSystem.Newer(drive.LastInput, _sequence))
            _sequence = drive.LastInput;
        _sequence = unchecked(_sequence + 1);
        SendPredictedMessage(new NCVehicleInputMessage(drive.Authority, _sequence, focused ? steering : 0,
            gas, brake, clutch, handbrake, _window.SteeringHeld ||
            (focused && (Held(NCVehicleKeyFunctions.SteerLeft) || Held(NCVehicleKeyFunctions.SteerRight)))));
        // Choose the neutral gate before engaging it when A/W arrive in the same input frame.
        var left = focused && Held(NCVehicleKeyFunctions.GearLeft);
        var right = focused && Held(NCVehicleKeyFunctions.GearRight);
        if (left && !_left)
            SendAction(NCVehicleDriverAction.GearLeft);
        if (right && !_right)
            SendAction(NCVehicleDriverAction.GearRight);
        _left = left;
        _right = right;
        var up = focused && Held(NCVehicleKeyFunctions.GearUp);
        var down = focused && Held(NCVehicleKeyFunctions.GearDown);
        if (up && !_up)
            SendAction(NCVehicleDriverAction.GearUp);
        if (down && !_down)
            SendAction(NCVehicleDriverAction.GearDown);
        _up = up;
        _down = down;
    }

    private bool Held(BoundKeyFunction function)
    {
        foreach (var binding in _input.GetKeyBindings(function))
            if (binding.State == BoundKeyState.Down)
                return true;
        return false;
    }

    private void ReleaseSteering() => _window?.ReleaseSteeringGrip();

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseSteering();
            EntMan.System<InputSystem>().SetEntityContextActive();
        }
        base.Dispose(disposing);
        if (disposing)
            _window = null;
    }
}
