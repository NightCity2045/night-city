// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.UserInterface.Fragments;
using Content.Shared.CartridgeLoader;
using Content.Shared.CartridgeLoader.Cartridges;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Agent.Shards.LogProbe;

/// <summary>
/// Keeps the vanilla access-log scan and print mechanics behind an Agent-native display.
/// </summary>
public sealed partial class NCAgentLogProbeUi : UIFragment
{
    private NCAgentLogProbeFragment? _fragment;

    public override Control GetUIFragmentRoot() => _fragment!;

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        _fragment = new NCAgentLogProbeFragment();
        _fragment.PrintRequested += () => userInterface.SendPredictedMessage(
            new CartridgeUiMessage(new LogProbePrintMessage()));
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is LogProbeUiState logs)
            _fragment?.UpdateState(logs.EntityName, logs.PulledLogs);
    }
}
