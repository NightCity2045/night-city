// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Pharmacology.Batch;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Medical.Pharmacology;

public sealed partial class NCPharmaStationBoundUserInterface(EntityUid owner, Enum key) : BoundUserInterface(owner, key)
{
    private NCPharmaStationWindow? _window;

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<NCPharmaStationWindow>();
        _window.StartRequested += (formula, profile, units) =>
            SendMessage(new NCPharmaStationStartMessage(formula, profile, units));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is NCPharmaStationState station)
            _window?.UpdateState(station);
    }
}
