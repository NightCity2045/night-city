// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#if TOOLS && NC_DIAGNOSTICS
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared._NC.Diagnostics;
using Content.Shared.Doors.Components;
using NightCity.Diagnostics.Contracts;
using Robust.Client.GameObjects;
using Robust.Client.GameStates;
using Robust.Client;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Client.State;
using Robust.Client.Timing;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Robust.Shared.GameStates;
using Robust.Shared.Asynchronous;
using Serilog.Events;

namespace Content.Client._NC.Diagnostics;

/// <summary>Authenticated local client diagnostics. Transport never reads ECS, UI, input or renderer state.</summary>
public sealed partial class NCDiagnosticsClientSystem : EntitySystem
{
    [Dependency] private IClientGameTiming _timing = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private ILogManager _logs = default!;
    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IUserInterfaceManager _ui = default!;
    [Dependency] private IInputManager _inputManager = default!;
    [Dependency] private InputSystem _input = default!;
    [Dependency] private IStateManager _states = default!;
    [Dependency] private SharedTransformSystem _transforms = default!;
    [Dependency] private IClientGameStateManager _gameStates = default!;
    [Dependency] private IGameController _gameController = default!;
    [Dependency] private ITaskManager _tasks = default!;

    private readonly DiagnosticRequestPump _pump = new("client");
    private readonly DiagnosticLogBuffer _logBuffer = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, JsonObject> _captures = new();
    private string _token = string.Empty;
    private string _pipeName = string.Empty;
    private bool _enabled;
    private bool _canInput;
    private bool _graphics;
    private uint _lastRealTick;
    private long _frame;
    private Task? _pipeTask;
    private CaptureLogHandler? _logHandler;
    private DiagnosticParentLifetime? _parentLifetime;
    private int _dispatchScheduled;

    private static readonly string[] ReadCommands =
    [
        "session_get", "capabilities_get", "client_inspect", "entity_inspect", "entity_snapshot", "replication_inspect",
        "ui_inspect", "logs_read", "metrics_capture", "client_capture", "capture_get", "request_status",
        "capture_discard",
        "replication_watch", "replication_unwatch", "meta_app_frontend_list", "meta_app_frontend_read",
    ];

    public override void Initialize()
    {
        base.Initialize();
        _token = Environment.GetEnvironmentVariable(DiagnosticProtocol.TokenEnvironment) ?? string.Empty;
        _pipeName = Environment.GetEnvironmentVariable(DiagnosticProtocol.PipeEnvironment) ?? string.Empty;
        if (_token.Length is < 32 or > 256 || _pipeName.Length is < 1 or > 100 ||
            !_pipeName.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_'))
            return;
        _enabled = true;
        _parentLifetime = DiagnosticParentLifetime.FromEnvironment();
        if (_parentLifetime != null)
            _ = WatchParentAsync();
        _canInput = DiagnosticProtocol.ParseProfile(Environment.GetEnvironmentVariable(DiagnosticProtocol.ProfileEnvironment)) >= DiagnosticProfile.Playtest;
        _graphics = !_clyde.GetType().Name.Contains("Headless", StringComparison.Ordinal);
        _logHandler = new CaptureLogHandler(_logBuffer);
        _logs.RootSawmill.AddHandler(_logHandler);
        _gameStates.GameStateApplied += OnGameStateApplied;
        _pipeTask = Task.Run(ServePipeAsync);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);
        if (!_enabled)
            return;
        if (_parentLifetime?.IsAlive() == false)
        {
            _gameController.Shutdown();
            return;
        }
        _frame++;
        if (_timing.LastRealTick.Value < _lastRealTick)
        {
            ResetGuiDiagnostics();
            ReleaseAllInput();
            _pump.ResetWorld();
            _controls.Clear();
            _doorHistory.Clear();
            _replicationWatches.Clear();
            _captures.Clear();
        }
        _lastRealTick = _timing.LastRealTick.Value;
        ReleaseExpiredInput();
        UpdateGuiDiagnostics();
        if (_pump.QueueLength != 0)
            _pump.Process(_timing.CurTick.Value, DispatchLogged, IsClientMutation);
    }

    public override void Shutdown()
    {
        _enabled = false;
        _gameStates.GameStateApplied -= OnGameStateApplied;
        ReleaseAllInput();
        ResetGuiDiagnostics(shuttingDown: true);
        _shutdown.Cancel();
        _pump.Stop();
        if (_logHandler != null)
            _logs.RootSawmill.RemoveHandler(_logHandler);
        base.Shutdown();
    }

    private async Task ServePipeAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_shutdown.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                var envelope = await DiagnosticWire.ReadAsync<DiagnosticPipeRequest>(pipe,
                    DiagnosticProtocol.MaximumRequestBytes, deadline.Token);
                if (!DiagnosticWire.Authenticate(_token, envelope.Token))
                    continue;
                var request = envelope.Request with { DeadlineUnixMilliseconds = Math.Min(envelope.Request.DeadlineUnixMilliseconds,
                    DateTimeOffset.UtcNow.AddSeconds(15).ToUnixTimeMilliseconds()) };
                var pending = _pump.EnqueueAsync(request, deadline.Token);
                SchedulePump();
                var response = await pending;
                await DiagnosticWire.WriteAsync(pipe, response, DiagnosticProtocol.MaximumResponseBytes, deadline.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is IOException or JsonException or DiagnosticException or OperationCanceledException)
            {
                // Bad peers and timed-out clients cannot terminate the listener or access the game thread directly.
            }
            catch (Exception)
            {
                _logBuffer.Add("error", "diagnostics", "The client IPC listener stopped unexpectedly.");
                break;
            }
        }
    }

    private void SchedulePump()
    {
        if (Interlocked.Exchange(ref _dispatchScheduled, 1) == 0)
            _tasks.RunOnMainThread(PumpOnMainThread);
    }

    private void PumpOnMainThread()
    {
        if (_enabled)
        {
            UpdateGuiDiagnostics();
            _pump.Process(_timing.CurTick.Value, DispatchLogged, IsClientMutation);
        }
        Interlocked.Exchange(ref _dispatchScheduled, 0);
        if (_enabled && _pump.QueueLength != 0)
            SchedulePump();
    }

    private async Task WatchParentAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
                if (_parentLifetime?.IsAlive() == false)
                {
                    _tasks.RunOnMainThread(() => _gameController.Shutdown());
                    return;
                }
        }
        catch (OperationCanceledException) { }
    }

    private static bool IsClientMutation(string command) => command is "client_input" or "ui_interact" or "meta_app_frontend_capture" or "meta_app_frontend_action" or "meta_app_frontend_stop";

    private JsonNode? Dispatch(DiagnosticRequest request) => request.Command switch
    {
        "session_get" or "capabilities_get" or "client_inspect" => SessionInfo(),
        "entity_inspect" or "entity_snapshot" => InspectEntity(request.Arguments),
        "replication_inspect" => ReplicationInfo(request.Arguments),
        "replication_watch" => WatchReplication(request.Arguments),
        "replication_unwatch" => UnwatchReplication(request.Arguments),
        "ui_inspect" => InspectUi(request.Arguments),
        "meta_app_frontend_list" or "meta_app_frontend_read" => InspectMetaApps(request.Arguments),
        "meta_app_frontend_capture" or "meta_app_frontend_action" or "meta_app_frontend_stop" => MutateMetaApp(request.Command, request.Arguments),
        "ui_interact" => InteractUi(request.Arguments),
        "client_input" => ExecuteInput(request.Arguments),
        "client_capture" => StartCapture(request.Arguments),
        "capture_get" => GetCapture(request.Arguments),
        "capture_discard" => DiscardCapture(request.Arguments),
        "logs_read" => JsonSerializer.SerializeToNode(_logBuffer.Read(request.Arguments["cursor"]?.GetValue<long>() ?? 0,
            request.Arguments.BoundedInt("limit", 100, 1, 200), request.Arguments["level"]?.GetValue<string>(),
            request.Arguments["category"]?.GetValue<string>()), DiagnosticProtocol.JsonOptions),
        "metrics_capture" => new JsonObject { ["tick"] = _timing.CurTick.Value, ["frame"] = _frame,
            ["lastRealTick"] = _timing.LastRealTick.Value, ["managedMemoryBytes"] = GC.GetTotalMemory(false),
            ["diagnosticQueue"] = _pump.QueueLength, ["scope"] = "process-and-diagnostics" },
        _ => throw new DiagnosticException("unsupported_command", "This client does not implement the requested command."),
    };

    private JsonNode? DispatchLogged(DiagnosticRequest request)
    {
        try { return Dispatch(request); }
        catch (Exception exception) when (exception is not DiagnosticException)
        {
            Log.Error($"Diagnostic command {request.Command} failed: {exception}");
            throw;
        }
    }

    private JsonObject SessionInfo() => new()
    {
        ["instanceId"] = _pump.InstanceId, ["worldGeneration"] = _pump.WorldGeneration, ["source"] = "client",
        ["tick"] = _timing.CurTick.Value, ["lastRealTick"] = _timing.LastRealTick.Value,
        ["lastProcessedTick"] = _timing.LastProcessedTick.Value, ["frame"] = _frame,
        ["graphics"] = _graphics, ["input"] = _canInput, ["uiPointerInput"] = _canInput && _graphics,
        ["inputFunctions"] = new JsonArray("move_up", "move_down", "move_left", "move_right", "use", "context_menu", "activate_in_hand", "drop", "swap_hands"),
        ["localEntity"] = _players.LocalEntity is { } uid ? GetNetEntity(uid).ToString() : null,
        ["editorInputHandler"] = _canInput && _graphics, ["osImeCandidateCoverage"] = false,
        ["clipboardTestLease"] = _canInput && _graphics, ["nativeWindowResize"] = _canInput && _graphics,
        ["uiActions"] = new JsonArray("click", "hover", "set_text", "submit", "focus", "scroll", "select_tab", "editor_input", "clipboard", "client_size", "own_window_size"),
        ["userId"] = _players.LocalSession?.UserId.ToString(),
        ["commands"] = JsonSerializer.SerializeToNode(ReadCommands.Concat(_canInput ? new[] { "client_input", "ui_interact", "meta_app_frontend_capture", "meta_app_frontend_action", "meta_app_frontend_stop" } : [])),
        ["replicationCoverage"] = "last-applied-tick-and-current-client-projection",
        ["predictionHistory"] = false,
        ["authoritativeHistoryAdapters"] = new JsonArray("Door"),
    };

    private EntityUid ResolveEntity(JsonObject args, string name = "netEntity")
    {
        if (!NetEntity.TryParse(args.RequiredString(name, 32), out var reference) ||
            !TryGetEntity(reference, out var uid) || Deleted(uid))
            throw new DiagnosticException("entity_not_available", "The entity is absent from this client's current replica; it may be outside PVS.");
        return uid.Value;
    }

    private JsonObject InspectEntity(JsonObject args)
    {
        var components = args["components"]?.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        if (components is { Length: > 32 })
            throw new DiagnosticException("invalid_arguments", "At most 32 components may be selected.");
        return NCDiagnosticProjection.Capture(ResolveEntity(args), EntityManager, components);
    }

    private JsonObject ReplicationInfo(JsonObject args)
    {
        var result = new JsonObject { ["lastRealTick"] = _timing.LastRealTick.Value,
            ["lastProcessedTick"] = _timing.LastProcessedTick.Value, ["predictedTick"] = _timing.CurTick.Value,
            ["frame"] = _frame, ["representation"] = "current-client-projection",
            ["authoritativeHistoryAvailable"] = false };
        if (args["netEntity"] != null)
        {
            var uid = ResolveEntity(args);
            var metadata = MetaData(uid);
            result["lastEntityStateApplied"] = metadata.LastStateApplied.Value;
            result["detachedFromPvs"] = (metadata.Flags & MetaDataFlags.Detached) != 0;
            result["entity"] = NCDiagnosticProjection.Capture(uid, EntityManager);
            if (_doorHistory.TryGetValue(uid, out var history))
            {
                result["authoritativeDoorHistory"] = new JsonArray(history.Select(x => x.DeepClone()).ToArray());
                result["authoritativeHistoryAvailable"] = history.Count != 0;
            }
        }
        return result;
    }

    private sealed class CaptureLogHandler(DiagnosticLogBuffer buffer) : ILogHandler
    {
        public void Log(string sawmillName, LogEvent message) =>
            buffer.Add(message.Level.ToString(), sawmillName, message.RenderMessage(CultureInfo.InvariantCulture));
    }
}

#endif
