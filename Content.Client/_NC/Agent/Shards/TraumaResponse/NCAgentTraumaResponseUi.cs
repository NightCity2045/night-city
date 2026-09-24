// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.UserInterface.Fragments;
using Content.Shared._NC.Agent.Shards.TraumaResponse;
using Content.Shared.CartridgeLoader;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Agent.Shards.TraumaResponse;

/// <summary>
/// Connects the physical TRAUMA RESPONSE shard to the authoritative dispatch service.
/// </summary>
public sealed partial class NCAgentTraumaResponseUi : UIFragment
{
    private NCAgentTraumaResponseFragment? _fragment;

    public override Control GetUIFragmentRoot() => _fragment!;

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        _fragment = new NCAgentTraumaResponseFragment();
        _fragment.ActionRequested += (action, callId, version) => userInterface.SendMessage(
            new CartridgeUiMessage(new NCAgentTraumaResponseActionMessage(action, callId, version)));
        userInterface.SendMessage(new CartridgeUiMessage(new NCAgentTraumaResponseActionMessage(
            NCTraumaResponseAction.Refresh, Guid.Empty, 0)));
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is NCAgentTraumaResponseUiState response)
            _fragment?.UpdateState(response);
    }
}
