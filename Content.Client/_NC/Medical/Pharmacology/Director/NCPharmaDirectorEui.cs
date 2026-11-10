// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.Eui;
using Content.Shared._NC.Medical.Pharmacology.Director;
using Content.Shared.Eui;

namespace Content.Client._NC.Medical.Pharmacology.Director;

public sealed class NCPharmaDirectorEui : BaseEui
{
    private readonly NCPharmaDirectorWindow _window = new();

    public NCPharmaDirectorEui()
    {
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
        _window.OnRefresh += () => SendMessage(new NCPharmaDirectorRefreshMessage());
        _window.OnRecall += id => SendMessage(new NCPharmaDirectorRecallMessage(id));
        // A staged contamination: purity and sterility fall to a poor value; the exact figures are for the console command.
        _window.OnContaminate += id => SendMessage(new NCPharmaDirectorAdjustMessage(id, 0.55f, 0.4f));
        _window.OnGrant += (compound, level) => SendMessage(new NCPharmaDirectorKnowledgeMessage(compound, level));
    }

    public override void Opened()
    {
        base.Opened();
        _window.OpenCentered();
    }

    public override void Closed()
    {
        base.Closed();
        _window.Close();
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is NCPharmaDirectorEuiState director)
            _window.SetState(director);
    }
}
