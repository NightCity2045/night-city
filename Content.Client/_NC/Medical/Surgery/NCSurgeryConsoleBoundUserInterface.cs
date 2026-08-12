// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Surgery;
using Robust.Client.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Medical.Surgery;

public sealed partial class NCSurgeryConsoleBoundUserInterface(EntityUid owner, Enum uiKey)
    : BoundUserInterface(owner, uiKey)
{
    [Dependency] private IPrototypeManager _prototypes = default!;

    private NCSurgeryConsoleWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = new NCSurgeryConsoleWindow(_prototypes);
        _window.OnSelect += entry => SendMessage(new NCSurgeryConsoleSelectMessage(
            entry.Procedure,
            entry.TargetCategory));
        _window.OnZoneSelect += zone => SendMessage(new NCSurgeryConsoleSelectZoneMessage(zone));
        _window.OnExecuteStep += () => SendMessage(new NCSurgeryConsoleExecuteStepMessage());
        _window.OnClear += () => SendMessage(new NCSurgeryConsoleClearSelectionMessage());
        _window.OpenCentered();
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is NCSurgeryConsoleUpdateMessage update)
            _window?.UpdateState(update);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _window?.Dispose();
    }
}
