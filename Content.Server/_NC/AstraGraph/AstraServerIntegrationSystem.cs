// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Shared._NC.AstraGraph;
using Robust.Server.Player;
using Robust.Shared.Asynchronous;
using Robust.Shared.Network;

namespace Content.Server._NC.AstraGraph;

/// <summary>
/// Connects the single server AstraGraph runtime to Night City admins and the client Studio bridge.
/// </summary>
public sealed partial class AstraServerIntegrationSystem : EntitySystem
{
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IServerNetManager _net = default!;
    [Dependency] private ITaskManager _tasks = default!;

    private readonly Dictionary<NetUserId, INetChannel> _studioChannels = new();

    public override void Initialize()
    {
        base.Initialize();
        _net.RegisterNetMessage<MsgAstraStudioLaunch>(OnStudioLaunch);
        _net.RegisterNetMessage<MsgAstraAuthoring>(OnAuthoring);
        _admins.OnPermsChanged += OnPermsChanged;
        AstraServerRuntime.IndexContentBindings();
        Log.Info(AstraServerRuntime.StartupReport());
    }

    public override void Shutdown()
    {
        _admins.OnPermsChanged -= OnPermsChanged;
        base.Shutdown();
    }

    private void OnPermsChanged(AdminPermsChangedEventArgs args)
    {
        AstraServerRuntime.InvalidateSession(args.Player);
        if (args.Flags == null || (args.Flags.Value & Content.Shared.Administration.AdminFlags.AstraGraph) == 0)
            _studioChannels.Remove(args.Player.UserId);
    }

    private void OnStudioLaunch(MsgAstraStudioLaunch message)
    {
        if (message.IsResponse)
            return;

        var channel = message.MsgChannel;
        var session = _players.GetSessionByChannel(channel);
        var token = AstraServerRuntime.IssueLaunchToken(session);
        if (token != null)
            _studioChannels[session.UserId] = channel;

        _net.ServerSendMessage(new MsgAstraStudioLaunch
        {
            RequestId = message.RequestId,
            IsResponse = true,
            Granted = token != null,
            Token = token ?? string.Empty
        }, channel);
    }

    private void OnAuthoring(MsgAstraAuthoring message)
    {
        if (message.IsResponse || !IsStudioChannel(message.MsgChannel))
            return;

        var channel = message.MsgChannel;
        var requestId = message.RequestId;
        var kind = message.Kind;
        var payload = message.Payload;
        _ = DispatchAuthoringAsync(channel, requestId, kind, payload);
    }

    private async Task DispatchAuthoringAsync(INetChannel channel, int requestId, string kind, byte[] payload)
    {
        try
        {
            var reply = await AstraServerRuntime.HandleAsync(kind, payload);
            if (reply != null)
                SendAuthoring(channel, requestId, reply.Value);
        }
        catch (Exception ex)
        {
            Log.Error($"AstraGraph authoring request failed: {ex.Message}");
        }
    }

    private void SendAuthoring(INetChannel channel, int requestId, AstraWireReply reply)
    {
        var outgoing = new MsgAstraAuthoring
        {
            RequestId = requestId,
            IsResponse = true,
            Kind = reply.Kind,
            Payload = reply.Payload
        };
        _tasks.RunOnMainThread(() => _net.ServerSendMessage(outgoing, channel));
    }

    private bool IsStudioChannel(INetChannel channel)
    {
        foreach (var granted in _studioChannels.Values)
        {
            if (granted == channel)
                return true;
        }

        return false;
    }
}
