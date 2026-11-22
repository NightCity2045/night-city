// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Pharmacology.Lab;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Medical.Pharmacology;

public sealed partial class NCLabMachineBoundUserInterface(EntityUid owner, Enum key) : BoundUserInterface(owner, key)
{
    private NCLabMachineWindow? _window;

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<NCLabMachineWindow>();
        _window.Title = EntMan.GetComponent<MetaDataComponent>(Owner).EntityName;
        _window.StartRequested += () => SendMessage(new NCLabMachineStartMessage());
        _window.CancelRequested += () => SendMessage(new NCLabMachineCancelMessage());
        _window.EjectRequested += slot => SendMessage(new NCLabMachineEjectMessage(slot));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is NCLabMachineState machine)
            _window?.UpdateState(machine);
    }
}
