// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.UserInterface.Fragments;
using Content.Shared._NC.Agent.Shards.Notes;
using Content.Shared.CartridgeLoader;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Agent.Shards.Notes;

/// <summary>
/// Connects the Agent-native NOTES control to the active cartridge relay.
/// </summary>
public sealed partial class NCAgentNotesUi : UIFragment
{
    private NCAgentNotesFragment? _fragment;

    public override Control GetUIFragmentRoot()
    {
        return _fragment!;
    }

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        _fragment = new NCAgentNotesFragment();
        _fragment.ActionRequested += (action, id, body) =>
            userInterface.SendMessage(new CartridgeUiMessage(new NCAgentNotesUiMessage(action, id, body)));
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is NCAgentNotesUiState notes)
            _fragment?.UpdateState(notes.Entries);
    }
}
