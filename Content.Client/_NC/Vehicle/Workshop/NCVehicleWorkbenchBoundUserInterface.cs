// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Shared.Prototypes;
using Content.Shared._NC.Vehicle.Prototypes;
using Content.Shared._NC.Vehicle.Workshop;

namespace Content.Client._NC.Vehicle.Workshop;

public sealed partial class NCVehicleWorkbenchBoundUserInterface(EntityUid owner, Enum uiKey)
    : BoundUserInterface(owner, uiKey)
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    private NCVehicleWorkbenchWindow? _window;

    protected override void Open()
    {
        base.Open();
        if (_window != null)
            return;
        _window = this.CreateWindow<NCVehicleWorkbenchWindow>();
        _window.Requested += SendMessage;
        SendMessage(new NCVehicleWorkbenchRefresh());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is not NCVehicleWorkbenchState workbench)
            return;
        NCVehicleChassisPrototype? chassis = null;
        NCVehicleWorkshopPrototype? profile = null;
        if (workbench.Chassis is { } chassisId)
            _prototypes.TryIndex(chassisId, out chassis);
        if (workbench.Profile is { } profileId)
            _prototypes.TryIndex(profileId, out profile);
        _window?.Apply(workbench, chassis, profile);
    }
}
