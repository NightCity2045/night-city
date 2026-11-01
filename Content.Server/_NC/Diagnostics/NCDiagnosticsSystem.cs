// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#if TOOLS && NC_DIAGNOSTICS
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using NightCity.Diagnostics.Contracts;
using Robust.Server.Player;
using Robust.Server;
using Robust.Server.ServerStatus;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Asynchronous;
using Serilog.Events;

namespace Content.Server._NC.Diagnostics;

/// <summary>Opt-in local diagnostics. All ECS work is dispatched from Update through a bounded request pump.</summary>
public sealed partial class NCDiagnosticsSystem : EntitySystem
{
    [Dependency] private IStatusHost _statusHost = default!;
    [Dependency] private ILogManager _logs = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transforms = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private ITaskManager _tasks = default!;
    [Dependency] private IBaseServer _server = default!;
    [Dependency] private Content.Server._NC.Medical.Cyberware.NCCyberwareSystem _cyberwareMetrics = default!;
    [Dependency] private Content.Server._NC.Medical.Physiology.NCPhysiologySchedulerSystem _physiologyMetrics = default!;

    private readonly DiagnosticRequestPump _pump = new("server");
    private readonly DiagnosticLogBuffer _logBuffer = new();
    private readonly CancellationTokenSource _shutdown = new();
    private string _token = string.Empty;
    private DiagnosticProfile _profile;
    private bool _isolated;
    private volatile bool _enabled;
    private int _dispatchScheduled;
    private DiagnosticParentLifetime? _parentLifetime;
    private CaptureLogHandler? _logHandler;

    private static readonly string[] ReadCommands =
    [
        "session_get", "capabilities_get", "entities_query", "entity_inspect", "entity_snapshot", "maps_list",
        "map_region_inspect", "prototype_get", "prototype_search", "logs_read", "metrics_capture", "actions_list", "request_status",
        "meta_performance_status", "meta_performance_read", "meta_app_load_status",
        "meta_demo_list", "meta_demo_status",
    ];

    private static readonly string[] MutationCommands =
    [
        "action_execute", "fixture_create", "fixture_spawn", "fixture_destroy", "fixture_reset", "fixture_attach", "fixture_access", "fixture_power",
        "meta_performance_start", "meta_performance_stop", "meta_app_load_prepare", "meta_app_load_open", "meta_app_load_attach", "meta_app_load_stop",
        "meta_demo_create", "meta_demo_destroy",
    ];

    public override void Initialize()
    {
        base.Initialize();
        _token = Environment.GetEnvironmentVariable(DiagnosticProtocol.TokenEnvironment) ?? string.Empty;
        if (_token.Length is < 32 or > 256)
            return;
        _profile = DiagnosticProtocol.ParseProfile(Environment.GetEnvironmentVariable(DiagnosticProtocol.ProfileEnvironment));
        _isolated = Environment.GetEnvironmentVariable("NC_DIAGNOSTICS_ISOLATED") == "1";
        if (_profile == DiagnosticProfile.FixtureAdmin && !_isolated)
            _profile = DiagnosticProfile.Observe;
        _enabled = true;
        _parentLifetime = DiagnosticParentLifetime.FromEnvironment();
        _logHandler = new CaptureLogHandler(_logBuffer);
        _logs.RootSawmill.AddHandler(_logHandler);
        _statusHost.AddHandler(HandleRequestAsync);
        Log.Info("NC diagnostics HTTP bridge initialized.");
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    public override void Shutdown()
    {
        _enabled = false;
        _shutdown.Cancel();
        _pump.Stop();
        if (_logHandler != null)
            _logs.RootSawmill.RemoveHandler(_logHandler);
        _shutdown.Dispose();
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_enabled && _parentLifetime?.IsAlive() == false)
        {
            _server.Shutdown(null);
            return;
        }
        if (_enabled && _pump.QueueLength != 0)
            _pump.Process(_timing.CurTick.Value, DispatchLogged, IsMutation);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _pump.ResetWorld();
        _querySnapshots.Clear();
        _fixtures.Clear();
    }

    private async Task<bool> HandleRequestAsync(IStatusHandlerContext context)
    {
        if (context.Url.AbsolutePath != DiagnosticProtocol.HttpPath)
            return false;
        if (!_enabled || context.RequestMethod != HttpMethod.Post)
        {
            await context.RespondErrorAsync(HttpStatusCode.NotFound);
            return true;
        }
        if (!IPAddress.IsLoopback(context.RemoteEndPoint.Address) || !context.Url.IsLoopback ||
            context.RequestHeaders.ContainsKey("Origin") ||
            !context.RequestHeaders.TryGetValue("Authorization", out var authorization) || authorization.Count != 1 ||
            !authorization[0]!.StartsWith("Bearer ", StringComparison.Ordinal) ||
            !DiagnosticWire.Authenticate(_token, authorization[0]![7..]))
        {
            await context.RespondErrorAsync(HttpStatusCode.Forbidden);
            return true;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var bytes = await DiagnosticWire.ReadBoundedAsync(context.RequestBody,
                DiagnosticProtocol.MaximumRequestBytes, timeout.Token);
            var request = JsonSerializer.Deserialize<DiagnosticRequest>(bytes, DiagnosticProtocol.JsonOptions);
            if (request == null)
            {
                await context.RespondErrorAsync(HttpStatusCode.BadRequest);
                return true;
            }
            // The server caps even a malicious or incorrectly configured caller's deadline.
            request = request with { DeadlineUnixMilliseconds = Math.Min(request.DeadlineUnixMilliseconds,
                DateTimeOffset.UtcNow.AddSeconds(15).ToUnixTimeMilliseconds()) };
            var pending = _pump.EnqueueAsync(request, timeout.Token);
            SchedulePump();
            var response = await pending;
            context.ResponseHeaders["Cache-Control"] = "no-store";
            await context.RespondAsync(DiagnosticProtocol.Serialize(response), HttpStatusCode.OK, "application/json");
        }
        catch (OperationCanceledException)
        {
            await TryRespondErrorAsync(context, HttpStatusCode.RequestTimeout);
        }
        catch (Exception exception) when (exception is JsonException or DiagnosticException or InvalidOperationException)
        {
            await TryRespondErrorAsync(context, HttpStatusCode.BadRequest);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // A local caller may time out/disconnect after submission. Retained mutation results remain queryable.
        }
        return true;
    }

    private static async Task TryRespondErrorAsync(IStatusHandlerContext context, HttpStatusCode status)
    {
        try { await context.RespondErrorAsync(status); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    private void SchedulePump()
    {
        if (Interlocked.Exchange(ref _dispatchScheduled, 1) == 0)
            _tasks.RunOnMainThread(PumpOnMainThread);
    }

    private void PumpOnMainThread()
    {
        if (_enabled)
            _pump.Process(_timing.CurTick.Value, DispatchLogged, IsMutation);
        Interlocked.Exchange(ref _dispatchScheduled, 0);
        if (_enabled && _pump.QueueLength != 0)
            SchedulePump();
    }

    private JsonNode? Dispatch(DiagnosticRequest request)
    {
        var args = request.Arguments;
        return request.Command switch
        {
            "session_get" or "capabilities_get" => SessionInfo(),
            "entities_query" or "map_region_inspect" => QueryEntities(args),
            "entity_inspect" or "entity_snapshot" => InspectEntity(args),
            "maps_list" => ListMaps(),
            "prototype_get" => GetPrototype(args),
            "prototype_search" => SearchPrototypes(args),
            "logs_read" => JsonSerializer.SerializeToNode(_logBuffer.Read(args["cursor"]?.GetValue<long>() ?? 0,
                args.BoundedInt("limit", 100, 1, 200), args["level"]?.GetValue<string>(), args["category"]?.GetValue<string>()), DiagnosticProtocol.JsonOptions),
            "metrics_capture" => new JsonObject { ["tick"] = _timing.CurTick.Value,
                ["diagnosticQueue"] = _pump.QueueLength, ["managedMemoryBytes"] = GC.GetTotalMemory(false),
                ["fixtureCount"] = _fixtures.Count, ["scope"] = "process-and-diagnostics",
                ["cyberware"] = JsonSerializer.SerializeToNode(_cyberwareMetrics.GetRuntimeCounters(), DiagnosticProtocol.JsonOptions),
                ["physiology"] = JsonSerializer.SerializeToNode(_physiologyMetrics.GetRuntimeCounters(), DiagnosticProtocol.JsonOptions) },
            "meta_app_load_prepare" => PrepareMetaAppLoad(args),
            "meta_app_load_status" => ReadMetaAppLoad(args),
            "meta_app_load_open" => OpenMetaAppLoad(args),
            "meta_app_load_attach" => AttachMetaAppLoad(args),
            "meta_app_load_stop" => StopMetaAppLoad(args),
            "meta_performance_start" => StartMetaPerformance(args),
            "meta_performance_stop" => StopMetaPerformance(args),
            "meta_performance_status" => ReadMetaPerformance(args, false),
            "meta_performance_read" => ReadMetaPerformance(args, true),
            "meta_demo_create" => CreateMetaDemo(args),
            "meta_demo_destroy" => DestroyMetaDemo(args),
            "meta_demo_list" => ReadMetaDemos(),
            "meta_demo_status" => ReadMetaDemoStatus(args),
            "actions_list" => ActionCatalogue(),
            "action_execute" => ExecuteAction(args),
            "fixture_create" => CreateFixture(args),
            "fixture_spawn" => SpawnFixtureEntity(args),
            "fixture_destroy" => DestroyFixture(args),
            "fixture_reset" => ResetFixture(args),
            "fixture_attach" => AttachFixtureActor(args),
            "fixture_access" => ConfigureFixtureAccess(args),
            "fixture_power" => ConfigureFixturePower(args),
            _ => throw new DiagnosticException("unsupported_command", "This server does not implement the requested command."),
        };
    }

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
        ["apiVersion"] = DiagnosticProtocol.Version, ["instanceId"] = _pump.InstanceId,
        ["worldGeneration"] = _pump.WorldGeneration, ["tick"] = _timing.CurTick.Value,
        ["source"] = "server", ["isolated"] = _isolated,
        ["profile"] = _profile switch { DiagnosticProfile.Playtest => "playtest", DiagnosticProfile.FixtureAdmin => "fixture-admin", _ => "observe" },
        ["commands"] = JsonSerializer.SerializeToNode(ReadCommands.Concat(_profile switch
        {
            DiagnosticProfile.FixtureAdmin => MutationCommands,
            DiagnosticProfile.Playtest => new[] { "action_execute" },
            _ => Array.Empty<string>(),
        })),
        ["limits"] = new JsonObject { ["queueLength"] = DiagnosticProtocol.MaximumQueueLength,
            ["pageSize"] = DiagnosticProtocol.MaximumPageSize, ["mutationRetentionSeconds"] = 600 },
        ["players"] = JsonSerializer.SerializeToNode(_players.Sessions.Take(32).Select(session => new
        {
            userId = session.UserId.ToString(), status = session.Status.ToString(),
            attachedEntity = session.AttachedEntity is { } uid && TryGetNetEntity(uid, out var reference)
                ? reference.Value.ToString() : null,
        }), DiagnosticProtocol.JsonOptions),
    };

    private static bool IsMutation(string command) => MutationCommands.Contains(command, StringComparer.Ordinal);

    private sealed class CaptureLogHandler(DiagnosticLogBuffer buffer) : ILogHandler
    {
        public void Log(string sawmillName, LogEvent message) =>
            buffer.Add(message.Level.ToString(), sawmillName, message.RenderMessage(CultureInfo.InvariantCulture));
    }
}

#endif
