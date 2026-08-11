// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.UserInterface.Fragments;
using Content.Shared.CartridgeLoader;
using Content.Shared.CartridgeLoader.Cartridges;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Agent.Shards.Taskboard;

/// <summary>
/// Connects the Agent-native TASKBOARD control to the unchanged vanilla NanoTask relay.
/// </summary>
public sealed partial class NCAgentTaskboardUi : UIFragment
{
    private NCAgentTaskboardFragment? _fragment;

    public override Control GetUIFragmentRoot()
    {
        return _fragment!;
    }

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        _fragment = new NCAgentTaskboardFragment();
        _fragment.CreateRequested += item => Send(userInterface, new NanoTaskAddTask(item));
        _fragment.UpdateRequested += (id, item) => Send(userInterface, new NanoTaskUpdateTask(new NanoTaskItemAndId(id, item)));
        _fragment.DeleteRequested += id => Send(userInterface, new NanoTaskDeleteTask(id));
        _fragment.PrintRequested += item => Send(userInterface, new NanoTaskPrintTask(item), predicted: true);
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is NanoTaskUiState tasks)
            _fragment?.UpdateState(tasks.Tasks);
    }

    private static void Send(
        BoundUserInterface userInterface,
        INanoTaskUiMessagePayload payload,
        bool predicted = false)
    {
        var message = new CartridgeUiMessage(new NanoTaskUiMessageEvent(payload));
        if (predicted)
            userInterface.SendPredictedMessage(message);
        else
            userInterface.SendMessage(message);
    }
}
