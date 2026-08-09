// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Missions.UI;
using Robust.Client.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._NC.Missions;

public sealed partial class NCMissionConsoleBoundUserInterface(EntityUid owner, Enum uiKey)
    : BoundUserInterface(owner, uiKey)
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;

    private NCMissionConsoleWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = new NCMissionConsoleWindow(_prototypes, _timing);
        _window.OnStart += () => SendMessage(new NCMissionConsoleStartMessage());
        _window.OnFinish += () => SendMessage(new NCMissionConsoleFinishMessage());
        _window.OpenCentered();
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is NCMissionConsoleUpdateMessage update)
            _window?.UpdateState(update);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _window?.Dispose();
    }
}
