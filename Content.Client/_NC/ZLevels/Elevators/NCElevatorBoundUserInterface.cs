// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ZLevels.Elevators;
using Robust.Client.UserInterface;

namespace Content.Client._NC.ZLevels.Elevators;

public sealed partial class NCElevatorBoundUserInterface(EntityUid owner, Enum key) : BoundUserInterface(owner, key)
{
    private NCElevatorWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<NCElevatorWindow>();
        _window.MoveRequested += offset => SendMessage(new NCElevatorMoveMessage(offset));
        if (State is NCElevatorUiState state)
            _window.ApplyState(state);
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is NCElevatorUiState elevatorState)
            _window?.ApplyState(elevatorState);
    }
}
