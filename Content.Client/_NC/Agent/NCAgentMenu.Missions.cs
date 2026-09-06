// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client._NC.Missions;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.Missions.UI;

namespace Content.Client._NC.Agent;

public sealed partial class NCAgentMenu
{
    private NCFixerMissionUpdateMessage? _missionData;
    private TimeSpan _missionReceivedAt;
    private NCFixerMissionControl? _fixerWorkPanel;
    public event Action<BoundUserInterfaceMessage>? OnFixerWorkRequest;

    public void UpdateMissionData(NCFixerMissionUpdateMessage update)
    {
        _missionData = update;
        _missionReceivedAt = _gameTiming.RealTime;
        if (_currentView == BuiltInAppView && _agentData?.ActiveApp == NCAgentApp.Missions)
            _fixerWorkPanel?.SetData(update);
    }

    private void BuildMissions()
    {
        _fixerWorkPanel ??= CreateFixerWorkPanel();
        _fixerWorkPanel.SetContacts(_agentData?.Contacts ?? []);
        _fixerWorkPanel.SetData(_missionData, _missionReceivedAt);
        _fixerWorkPanel.Orphan();
        BuiltInContent.AddChild(_fixerWorkPanel);
    }

    private NCFixerMissionControl CreateFixerWorkPanel()
    {
        var panel = new NCFixerMissionControl();
        panel.OnRequest += message => OnFixerWorkRequest?.Invoke(message);
        return panel;
    }
}
