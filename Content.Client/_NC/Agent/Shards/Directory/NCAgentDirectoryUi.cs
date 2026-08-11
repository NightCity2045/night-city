// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.UserInterface.Fragments;
using Content.Shared._NC.Agent.Shards.Directory;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Agent.Shards.Directory;

/// <summary>
/// Renders the persistent public Night City personnel registry.
/// </summary>
public sealed partial class NCAgentDirectoryUi : UIFragment
{
    private NCAgentDirectoryFragment? _fragment;

    public override Control GetUIFragmentRoot() => _fragment!;

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        _fragment = new NCAgentDirectoryFragment();
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is NCAgentDirectoryUiState directory)
            _fragment?.UpdateState(directory.Records);
    }
}
