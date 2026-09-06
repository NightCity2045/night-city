// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Agent.UI;

namespace Content.Client._NC.Agent;

public sealed partial class NCAgentMenu
{
    private NCAgentContractsUpdateMessage? _contractData;
    private NCAgentContractsControl? _contractPanel;
    public event Action<BoundUserInterfaceMessage>? OnContractRequest;

    public void UpdateContractData(NCAgentContractsUpdateMessage update)
    {
        _contractData = update;
        if (_currentView == BuiltInAppView && _agentData?.ActiveApp == NCAgentApp.Contracts)
            _contractPanel?.SetData(update);
    }

    private void BuildContracts()
    {
        // Keep a single panel alive across snapshot refreshes so a notification cannot erase an unfinished draft.
        _contractPanel ??= CreateContractsPanel();
        _contractPanel.SetData(_contractData);
        _contractPanel.Orphan();
        BuiltInContent.AddChild(_contractPanel);
    }

    private NCAgentContractsControl CreateContractsPanel()
    {
        var panel = new NCAgentContractsControl();
        panel.OnRequest += message => OnContractRequest?.Invoke(message);
        panel.SetData(_contractData);
        return panel;
    }
}
