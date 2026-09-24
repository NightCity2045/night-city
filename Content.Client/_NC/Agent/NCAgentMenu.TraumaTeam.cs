// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Agent.TraumaTeam;

namespace Content.Client._NC.Agent;

public sealed partial class NCAgentMenu
{
    private NCAgentTraumaTeamUpdateMessage? _traumaTeamData;
    private NCAgentTraumaTeamControl? _traumaTeamPanel;
    public event Action<BoundUserInterfaceMessage>? OnTraumaTeamRequest;

    public void UpdateTraumaTeamData(NCAgentTraumaTeamUpdateMessage update)
    {
        _traumaTeamData = update;
        _traumaTeamPanel?.SetData(update);
    }

    private void BuildTraumaTeam()
    {
        _traumaTeamPanel ??= CreateTraumaTeamPanel();
        _traumaTeamPanel.Orphan();
        BuiltInContent.AddChild(_traumaTeamPanel);
        if (_traumaTeamData != null)
            _traumaTeamPanel.SetData(_traumaTeamData);
    }

    private NCAgentTraumaTeamControl CreateTraumaTeamPanel()
    {
        var panel = new NCAgentTraumaTeamControl();
        panel.OnRequest += message => OnTraumaTeamRequest?.Invoke(message);
        return panel;
    }
}
