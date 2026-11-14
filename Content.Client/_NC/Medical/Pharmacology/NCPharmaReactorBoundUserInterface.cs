// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Pharmacology.Batch;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Medical.Pharmacology;

public sealed partial class NCPharmaReactorBoundUserInterface(EntityUid owner, Enum key) : BoundUserInterface(owner, key)
{
    private NCPharmaReactorWindow? _window;

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<NCPharmaReactorWindow>();
        _window.StartRequested += (formula, units) => SendMessage(new NCPharmaReactorStartMessage(formula, units));
        _window.ControlsChanged += (heater, cooler, stirring, venting, ventilation) =>
            SendMessage(new NCPharmaReactorControlMessage(heater, cooler, stirring, venting, ventilation));
        _window.ActionRequested += action => SendMessage(new NCPharmaReactorActionMessage(action));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is NCPharmaReactorState reactor)
            _window?.UpdateState(reactor);
    }
}
