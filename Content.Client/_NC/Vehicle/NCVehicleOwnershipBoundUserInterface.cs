// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Vehicle;

public sealed class NCVehicleOwnershipBoundUserInterface(EntityUid owner, Enum key) : BoundUserInterface(owner, key)
{
    private NCVehicleOwnershipWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<NCVehicleOwnershipWindow>();
        _window.Requested += (action, price, revision) => SendMessage(new NCVehicleOwnershipMessage(action, price, revision));
        if (State is NCVehicleOwnershipState state)
            _window.ApplyState(state);
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is NCVehicleOwnershipState ownership)
            _window?.ApplyState(ownership);
    }
}
