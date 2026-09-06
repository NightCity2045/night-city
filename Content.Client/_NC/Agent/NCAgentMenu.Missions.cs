// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Agent.UI;

namespace Content.Client._NC.Agent;

public sealed partial class NCAgentMenu
{
    private NCAgentMissionsUpdateMessage? _missionData;
    private string? _selectedMission;
    private TimeSpan _missionReceivedAt;
    public event Action<string>? OnStartMission;
    public event Action<Guid>? OnFinishMission;

    public void UpdateMissionData(NCAgentMissionsUpdateMessage update)
    {
        _missionData = update;
        _missionReceivedAt = _gameTiming.RealTime;
        if (_currentView == BuiltInAppView && _agentData?.ActiveApp == NCAgentApp.Missions)
            RenderAgentApp(NCAgentApp.Missions);
    }

    private void BuildMissions()
    {
        var panel = new NCAgentMissionsControl();
        panel.OnRefresh += () => OnOpenAgentApp?.Invoke(NCAgentApp.Missions);
        panel.OnSelect += id => _selectedMission = id;
        panel.OnStart += id => OnStartMission?.Invoke(id);
        panel.OnFinish += id => OnFinishMission?.Invoke(id);
        panel.SetData(_missionData, _selectedMission, _missionReceivedAt);
        BuiltInContent.AddChild(panel);
    }
}
