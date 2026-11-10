// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Pharmacology.Batch;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Medical.Pharmacology;

public sealed partial class NCPharmaQcBoundUserInterface(EntityUid owner, Enum key) : BoundUserInterface(owner, key)
{
    private NCPharmaQcWindow? _window;

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<NCPharmaQcWindow>();
        _window.RunRequested += tests => SendMessage(new NCPharmaQcRunMessage(tests));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is NCPharmaQcMachineState machine)
            _window?.UpdateState(machine);
    }
}
