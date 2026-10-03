// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using Content.Client.Administration.Managers;
using Content.Shared._NC.AstraGraph;
using Content.Shared.Administration;
using Robust.Client.Player;
using Robust.Shared.Console;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Client._NC.AstraGraph;

/// <summary>
/// Requests a short-lived launch URL and opens the server-hosted Astra Studio.
/// </summary>
public sealed partial class AstraClientIntegrationSystem : EntitySystem
{
    [Dependency] private IClientNetManager _net = default!;
    [Dependency] private IClientAdminManager _admin = default!;
    [Dependency] private IPlayerManager _players = default!;

    private readonly Dictionary<int, PendingLaunch> _launches = new();
    private int _nextRequest = 1;

    public bool TryOpenStudio(IConsoleShell shell)
    {
        if (!_admin.HasFlag(AdminFlags.AstraGraph))
        {
            shell.WriteError(Loc.GetString("nc-astra-studio-denied"));
            return false;
        }

        if (_players.LocalSession == null)
        {
            shell.WriteError(Loc.GetString("nc-astra-studio-unavailable"));
            return false;
        }

        var requestId = _nextRequest++;
        _launches[requestId] = new PendingLaunch(shell);
        _net.ClientSendMessage(new MsgAstraStudioLaunch { RequestId = requestId });
        shell.WriteLine(Loc.GetString("nc-astra-studio-opening"));
        return true;
    }

    internal void OnStudioLaunch(MsgAstraStudioLaunch message)
    {
        if (!message.IsResponse || !_launches.Remove(message.RequestId, out var pending))
            return;

        if (!message.Granted || string.IsNullOrEmpty(message.StudioUrl))
        {
            pending.Shell.WriteError(Loc.GetString("nc-astra-studio-denied"));
            return;
        }

        if (AstraStudioBridge.TryOpen(message.StudioUrl))
            pending.Shell.WriteLine(Loc.GetString("nc-astra-studio-opened"));
        else
            pending.Shell.WriteError(Loc.GetString("nc-astra-studio-unavailable"));
    }

    private sealed class PendingLaunch
    {
        public PendingLaunch(IConsoleShell shell)
        {
            Shell = shell;
        }

        public IConsoleShell Shell { get; }
    }
}
