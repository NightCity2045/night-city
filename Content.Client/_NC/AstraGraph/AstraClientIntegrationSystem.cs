// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AstraGraph.Editor.Protocol;
using AstraGraph.Robust.Client;
using Content.Client.Administration.Managers;
using Content.Shared._NC.AstraGraph;
using Content.Shared.Administration;
using Robust.Client.Player;
using Robust.Shared.Asynchronous;
using Robust.Shared.Console;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Client._NC.AstraGraph;

/// <summary>
/// Opens Astra Studio through the local browser bridge and forwards authoring to the server.
/// </summary>
public sealed partial class AstraClientIntegrationSystem : EntitySystem
{
    private static readonly TimeSpan AuthoringTimeout = TimeSpan.FromSeconds(30);

    [Dependency] private IClientNetManager _net = default!;
    [Dependency] private IClientAdminManager _admin = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private ITaskManager _tasks = default!;
    [Dependency] private ClientAstraGraphSystem _astra = default!;

    private readonly Dictionary<int, PendingLaunch> _launches = new();
    private readonly Dictionary<int, TaskCompletionSource<MsgAstraAuthoring>> _authoring = new();
    private AstraStudioServerProxy? _proxy;
    private int _nextRequest = 1;

    public override void Initialize()
    {
        base.Initialize();
        _net.RegisterNetMessage<MsgAstraStudioLaunch>(OnStudioLaunch);
        _net.RegisterNetMessage<MsgAstraAuthoring>(OnAuthoring);
    }

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

    public Task<AuthoringMessage?> SendAuthoringAsync(AuthoringMessage message, CancellationToken cancellation)
    {
        var done = new TaskCompletionSource<AuthoringMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.RunOnMainThread(() => _ = SendOnMainThreadAsync(message, cancellation, done));
        return done.Task;
    }

    private async Task SendOnMainThreadAsync(
        AuthoringMessage message,
        CancellationToken cancellation,
        TaskCompletionSource<AuthoringMessage?> done)
    {
        var requestId = _nextRequest++;
        var pending = new TaskCompletionSource<MsgAstraAuthoring>(TaskCreationOptions.RunContinuationsAsynchronously);
        _authoring[requestId] = pending;
        _net.ClientSendMessage(new MsgAstraAuthoring
        {
            RequestId = requestId,
            Kind = message.Kind,
            Payload = AstraAuthoringCodec.Serialize(message)
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(AuthoringTimeout);
        await using var registration = timeout.Token.Register(() => pending.TrySetCanceled(timeout.Token));
        try
        {
            var response = await pending.Task;
            done.TrySetResult(AstraAuthoringCodec.Deserialize(response.Kind, response.Payload));
        }
        catch (OperationCanceledException)
        {
            done.TrySetCanceled(cancellation);
        }
        catch (Exception ex)
        {
            done.TrySetException(ex);
        }
        finally
        {
            _authoring.Remove(requestId);
        }
    }

    private void OnStudioLaunch(MsgAstraStudioLaunch message)
    {
        if (!message.IsResponse || !_launches.Remove(message.RequestId, out var pending))
            return;

        if (!message.Granted || string.IsNullOrEmpty(message.Token))
        {
            pending.Shell.WriteError(Loc.GetString("nc-astra-studio-denied"));
            return;
        }

        _proxy ??= new AstraStudioServerProxy(this);
        _proxy.Token = message.Token;
        _ = OpenStudioAsync(pending.Shell);
    }

    private async Task OpenStudioAsync(IConsoleShell shell)
    {
        try
        {
            if (_proxy == null)
            {
                shell.WriteError(Loc.GetString("nc-astra-studio-unavailable"));
                return;
            }

            await _astra.LaunchStudioAsync(_proxy);
            shell.WriteLine(Loc.GetString("nc-astra-studio-opened"));
        }
        catch (Exception ex)
        {
            shell.WriteError(Loc.GetString("nc-astra-studio-failed", ("reason", ex.Message)));
        }
    }

    private void OnAuthoring(MsgAstraAuthoring message)
    {
        if (!message.IsResponse)
            return;

        if (_authoring.TryGetValue(message.RequestId, out var pending))
            pending.TrySetResult(message);
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

/// <summary>
/// Local bridge handler. The Studio page does not receive the server token in its launch URL,
/// so an empty handshake token is replaced with the token the server just issued.
/// </summary>
public sealed class AstraStudioServerProxy : IAuthoringMessageHandler
{
    private readonly AstraClientIntegrationSystem _client;

    public AstraStudioServerProxy(AstraClientIntegrationSystem client)
    {
        _client = client;
    }

    public string Token { get; set; } = string.Empty;

    public Task<AuthoringMessage?> HandleAsync(AuthoringMessage message, CancellationToken cancellation)
    {
        if (message is AuthHandshakeRequestMsg handshake && string.IsNullOrEmpty(handshake.AuthorToken))
        {
            message = new AuthHandshakeRequestMsg
            {
                MessageId = handshake.MessageId,
                Timestamp = handshake.Timestamp,
                ClientVersion = handshake.ClientVersion,
                AuthorName = handshake.AuthorName,
                AuthorToken = Token
            };
        }

        return _client.SendAuthoringAsync(message, cancellation);
    }
}
