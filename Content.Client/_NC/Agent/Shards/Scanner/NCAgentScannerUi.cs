// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.UserInterface.Fragments;
using Content.Shared.CartridgeLoader.Cartridges;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Agent.Shards.Scanner;

/// <summary>
/// Presents vanilla NetProbe scan results through the Agent-native SCANNER control.
/// </summary>
public sealed partial class NCAgentScannerUi : UIFragment
{
    private NCAgentScannerFragment? _fragment;

    public override Control GetUIFragmentRoot()
    {
        return _fragment!;
    }

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        _fragment = new NCAgentScannerFragment();
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is NetProbeUiState scanner)
            _fragment?.UpdateState(scanner.ProbedDevices);
    }
}
