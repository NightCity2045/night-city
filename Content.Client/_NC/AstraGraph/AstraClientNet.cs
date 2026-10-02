// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.AstraGraph;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Network;

namespace Content.Client._NC.AstraGraph;

/// <summary>
/// Registers Studio net messages once for the client process.
/// Entity systems are created again on every connect, and a second registration throws.
/// </summary>
public sealed partial class AstraClientNet
{
    [Dependency] private IClientNetManager _net = default!;
    [Dependency] private IEntitySystemManager _systems = default!;

    public void Initialize()
    {
        _net.RegisterNetMessage<MsgAstraStudioLaunch>(OnStudioLaunch);
        _net.RegisterNetMessage<MsgAstraAuthoring>(OnAuthoring);
    }

    private void OnStudioLaunch(MsgAstraStudioLaunch message)
    {
        if (_systems.TryGetEntitySystem(out AstraClientIntegrationSystem? system))
            system.OnStudioLaunch(message);
    }

    private void OnAuthoring(MsgAstraAuthoring message)
    {
        if (_systems.TryGetEntitySystem(out AstraClientIntegrationSystem? system))
            system.OnAuthoring(message);
    }
}
