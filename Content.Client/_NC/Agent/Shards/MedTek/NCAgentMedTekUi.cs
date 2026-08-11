// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.UserInterface.Fragments;
using Content.Shared._NC.Agent.Shards.MedTek;
using Content.Shared.CartridgeLoader;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Agent.Shards.MedTek;

/// <summary>
/// Displays the physical MEDTEK shard history captured by vanilla analyzer scans.
/// </summary>
public sealed partial class NCAgentMedTekUi : UIFragment
{
    private NCAgentMedTekFragment? _fragment;

    public override Control GetUIFragmentRoot() => _fragment!;

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        _fragment = new NCAgentMedTekFragment();
        _fragment.ClearRequested += () => userInterface.SendMessage(
            new CartridgeUiMessage(new NCAgentMedTekClearMessage()));
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is NCAgentMedTekUiState medTek)
            _fragment?.UpdateState(medTek.History);
    }
}
