// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Workshop;
using Robust.Shared.Prototypes;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Vehicle;

public sealed partial class NCVehicleServiceBoundUserInterface(EntityUid owner, Enum key) : BoundUserInterface(owner, key)
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    private NCVehicleServiceWindow? _window;

    protected override void Open()
    {
        base.Open();
        if (_window != null)
            return;
        _window = this.CreateWindow<NCVehicleServiceWindow>();
        _window.Requested += (slot, action) => SendMessage(new NCVehicleServiceMessage(slot, action));
        _window.CalibrationRequested += calibration => SendMessage(new NCVehicleTuneMessage(calibration));
        _window.WorkshopRequested += message => SendMessage(message);
        _window.TorqueRequested += torque => SendMessage(new NCVehicleWorkshopTorqueRequest(torque));
        _window.RefreshRequested += () => SendMessage(new NCVehicleWorkshopRefresh());
        EntMan.System<Workshop.NCVehicleWorkshopClientSystem>().TryApplyLatest(Owner);
        SendMessage(new NCVehicleWorkshopRefresh());
    }

    public void ApplySnapshot(NCVehicleWorkshopSnapshot snapshot)
    {
        if (_window != null &&
            _prototypes.TryIndex(snapshot.Profile, out var profile) &&
            EntMan.TryGetComponent<NCVehicleComponent>(Owner, out var vehicle) &&
            _prototypes.TryIndex(vehicle.Chassis, out var chassis))
            _window.ApplyWorkshop(snapshot, profile, chassis);
    }
}
