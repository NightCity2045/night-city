// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

public sealed partial class DiagnosticApplication : IDisposable
{
    private readonly IDiagnosticBridge _bridge;
    private readonly ConcurrentDictionary<string, DiagnosticTarget> _targets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DiagnosticResponse> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly string _leaseOwner = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, string> _leases = new(StringComparer.Ordinal);
    public WorkspacePaths Paths { get; }
    public ArtifactStore Artifacts { get; }
    public OperationManager Operations { get; } = new();
    public RuntimeSupervisor Runtime { get; }
    public ScenarioRunner Scenarios { get; }
    public ResourceInspector Resources { get; }
    public ValidationRunner Validation { get; }

    public DiagnosticApplication(string workspace, IDiagnosticBridge? bridge = null)
    {
        Paths = new WorkspacePaths(workspace);
        Artifacts = new ArtifactStore(Paths);
        _bridge = bridge ?? new DiagnosticBridge();
        Runtime = new RuntimeSupervisor(Paths, _bridge);
        Scenarios = new ScenarioRunner(this);
        Resources = new ResourceInspector(Paths);
        Validation = new ValidationRunner(Paths, Artifacts);
        var server = Environment.GetEnvironmentVariable("SS14_MCP_SERVER");
        var serverToken = Environment.GetEnvironmentVariable("SS14_MCP_SERVER_TOKEN");
        if (!string.IsNullOrWhiteSpace(server) && !string.IsNullOrWhiteSpace(serverToken))
            AddTarget(new DiagnosticTarget("server", "server", server, serverToken));
        var pipe = Environment.GetEnvironmentVariable("SS14_MCP_CLIENT_PIPE");
        var clientToken = Environment.GetEnvironmentVariable("SS14_MCP_CLIENT_TOKEN");
        if (!string.IsNullOrWhiteSpace(pipe) && !string.IsNullOrWhiteSpace(clientToken))
            AddTarget(new DiagnosticTarget("client", "client", pipe, clientToken));
    }

    public void AddTarget(DiagnosticTarget target)
    {
        if (_targets.Count >= 16 || target.Token.Length is < 32 or > 256 || target.Id.Length is < 1 or > 64 ||
            !target.Id.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_'))
            throw new DiagnosticException("invalid_target", "Invalid target identifier, token or target count.");
        if (target.Kind == "server")
            DiagnosticBridge.ValidateHttpAddress(target.Address);
        else if (target.Kind != "client" || !DiagnosticBridge.ValidPipeName(target.Address))
            throw new DiagnosticException("invalid_target", "Expected a server HTTP origin or a client pipe name.");
        if (!_targets.TryAdd(target.Id, target))
            throw new DiagnosticException("target_exists", "The target identifier is already registered.");
    }

    public async Task<JsonNode?> ExecuteAsync(string command, JsonObject arguments, CancellationToken cancellationToken)
    {
        arguments = (JsonObject)arguments.DeepClone();
        if (Scenarios.Running && !Scenarios.IsExecutionContext &&
            (IsMutation(command) || command is "environment_start" or "environment_stop"))
            throw new DiagnosticException("busy", "An active scenario reserves environment and mutation control.", true);
        switch (command)
        {
            case "environment_list":
                return new JsonObject { ["targets"] = JsonSerializer.SerializeToNode(_targets.Values.Select(x => x.PublicInfo()), DiagnosticProtocol.JsonOptions),
                    ["ownedEnvironments"] = JsonSerializer.SerializeToNode(Runtime.List(), DiagnosticProtocol.JsonOptions) };
            case "environment_start":
                return StartOperation(command, arguments, false, async ct =>
                {
                    var started = await Runtime.StartAsync(arguments.BoundedInt("clients", 0, 0, 3),
                        arguments["graphics"]?.GetValue<bool>() ?? false, ct);
                    foreach (var registered in started.Targets)
                        AddTarget(registered);
                    return JsonSerializer.SerializeToNode(started.Info, DiagnosticProtocol.JsonOptions);
                });
            case "environment_stop":
                return StartOperation(command, arguments, false, async ct =>
                {
                    var stopped = await Runtime.StopAsync(arguments.RequiredString("environmentId", 64), ct);
                    foreach (var id in stopped.Targets)
                    {
                        _targets.TryRemove(id, out _);
                        _sessions.TryRemove(id, out _);
                    }
                    return JsonSerializer.SerializeToNode(stopped, DiagnosticProtocol.JsonOptions);
                });
            case "environment_logs":
                return JsonSerializer.SerializeToNode(Runtime.Logs(arguments.RequiredString("environmentId", 64),
                    arguments["cursor"]?.GetValue<long>() ?? 0, arguments.BoundedInt("limit", 100, 1, 200)), DiagnosticProtocol.JsonOptions);
            case "environment_attach":
                var variable = arguments.RequiredString("tokenEnvironment", 100);
                if (!variable.StartsWith("SS14_MCP_", StringComparison.Ordinal) || !variable.EndsWith("TOKEN", StringComparison.Ordinal))
                    throw new DiagnosticException("invalid_arguments", "Token environment variables must use the SS14_MCP_ prefix and TOKEN suffix.");
                var token = Environment.GetEnvironmentVariable(variable)
                    ?? throw new DiagnosticException("missing_token", "The selected token environment variable is not set.");
                var target = new DiagnosticTarget(arguments.RequiredString("id", 64), arguments.RequiredString("kind", 16),
                    arguments.RequiredString("address", 256), token, arguments["environmentId"]?.GetValue<string>());
                AddTarget(target);
                return JsonSerializer.SerializeToNode(target.PublicInfo(), DiagnosticProtocol.JsonOptions);
            case "operation_get":
                return OperationInfo(arguments.RequiredString("operationId", 128));
            case "operation_cancel":
                Operations.Cancel(arguments.RequiredString("operationId", 128));
                return OperationInfo(arguments.RequiredString("operationId", 128));
            case "artifacts_list":
                return JsonSerializer.SerializeToNode(Artifacts.List(), DiagnosticProtocol.JsonOptions);
            case "artifact_get":
                return await ReadArtifactAsync(arguments, cancellationToken);
            case "snapshot_diff":
                var left = JsonNode.Parse(await Artifacts.ReadJsonAsync(arguments.RequiredString("left", 100), cancellationToken));
                var right = JsonNode.Parse(await Artifacts.ReadJsonAsync(arguments.RequiredString("right", 100), cancellationToken));
                var pointers = arguments["paths"]?.AsArray().Select(x => x!.GetValue<string>()).ToArray() ?? [];
                return JsonInspection.Diff(left, right, pointers);
            case "condition_wait":
                return StartOperation(command, arguments, false, ct => WaitForConditionAsync(arguments, ct));
            case "client_capture":
                return StartOperation(command, arguments, false, ct => CaptureClientAsync(arguments, ct));
            case "scenario_validate":
                var document = await Scenarios.LoadAsync(arguments, cancellationToken);
                return new JsonObject { ["valid"] = true, ["id"] = document["id"]!.DeepClone(), ["steps"] = document["steps"]!.AsArray().Count };
            case "scenario_run":
                var scenario = await Scenarios.LoadAsync(arguments, cancellationToken);
                return StartOperation(command, arguments, false, ct => Scenarios.RunAsync(scenario, ct));
            case "scenario_status":
                return OperationInfo(arguments.RequiredString("operationId", 128));
            case "scenario_cancel":
                Operations.Cancel(arguments.RequiredString("operationId", 128));
                return OperationInfo(arguments.RequiredString("operationId", 128));
            case "report_create":
                var reportData = JsonNode.Parse(await Artifacts.ReadJsonAsync(arguments.RequiredString("id", 100), cancellationToken))!;
                return JsonSerializer.SerializeToNode(await Scenarios.CreateReportAsync(reportData, cancellationToken), DiagnosticProtocol.JsonOptions);
            case "resource_inspect":
                return await Resources.InspectAsync(arguments, cancellationToken);
            case "validation_run":
                return StartOperation(command, arguments, false, ct => Validation.RunAsync(arguments, ct));
            case "map_region_capture":
                arguments["mode"] = "viewport";
                return StartOperation(command, arguments, false, ct => CaptureClientAsync(arguments, ct));
            case "state_compare":
                return StartOperation(command, arguments, false, ct => CompareStateAsync(arguments, ct));
            case "trace_start":
                return StartOperation(command, arguments, false, ct => SampleTraceAsync(arguments, ct));
            case "trace_read":
                return OperationInfo(arguments.RequiredString("operationId", 128));
            case "trace_stop":
                Operations.Cancel(arguments.RequiredString("operationId", 128));
                return OperationInfo(arguments.RequiredString("operationId", 128));
            default:
                if (IsMutation(command))
                    return StartOperation(command, arguments, true, ct => ForwardAsync(command, arguments, ct));
                if (!ReadCommands.Contains(command))
                    throw new DiagnosticException("unsupported_command", "The application does not implement this command.");
                var result = await ForwardAsync(command, arguments, cancellationToken);
                if (command == "entity_snapshot")
                {
                    var artifact = await Artifacts.WriteJsonAsync("snapshot", result!, cancellationToken);
                    return new JsonObject { ["snapshot"] = result, ["artifact"] = JsonSerializer.SerializeToNode(artifact, DiagnosticProtocol.JsonOptions) };
                }
                return result;
        }
    }

    private JsonNode StartOperation(string name, JsonObject args, bool mutation, Func<CancellationToken, Task<JsonNode?>> execute)
    {
        var operationId = args["operationId"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
        var copy = (JsonObject)args.DeepClone();
        copy.Remove("operationId");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(copy.ToJsonString())));
        args["operationId"] = operationId;
        return JsonSerializer.SerializeToNode(Operations.Start(name, operationId, fingerprint, mutation, execute), DiagnosticProtocol.JsonOptions)!;
    }

    public async Task<JsonNode?> ForwardAsync(string command, JsonObject arguments, CancellationToken cancellationToken)
    {
        var targetId = arguments["targetId"]?.GetValue<string>() ?? "server";
        if (!_targets.TryGetValue(targetId, out var target))
            throw new DiagnosticException("target_not_found", "Select a registered environment target.");
        var mutation = IsMutation(command);
        if (mutation && Scenarios.Running && !Scenarios.IsExecutionContext)
            throw new DiagnosticException("busy", "An active scenario reserves mutation control in this MCP process.", true);
        if (mutation)
            await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (!_sessions.TryGetValue(targetId, out var session) || command is "session_get" or "capabilities_get")
            {
                session = await _bridge.ExecuteAsync(target, new DiagnosticRequest { Command = "session_get" }, cancellationToken);
                CheckResponse(session);
                _sessions[targetId] = session;
                if (command is "session_get" or "capabilities_get")
                    return JsonSerializer.SerializeToNode(session, DiagnosticProtocol.JsonOptions);
            }
            var payload = (JsonObject)arguments.DeepClone();
            payload.Remove("targetId");
            payload.Remove("operationId");
            string? leaseToken = null;
            if (mutation)
            {
                if (target.Kind == "client" && target.EnvironmentId is { } group &&
                    _targets.Values.FirstOrDefault(item => item.Kind == "server" && item.EnvironmentId == group) is { } server)
                    await AcquireLeaseAsync(server, cancellationToken);
                leaseToken = await AcquireLeaseAsync(target, cancellationToken);
            }
            var request = new DiagnosticRequest
            {
                Command = command, Arguments = payload,
                RequestId = arguments["operationId"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                ServerInstanceId = arguments["serverInstanceId"]?.GetValue<string>() ?? session.ServerInstanceId,
                WorldGeneration = arguments["worldGeneration"]?.GetValue<long>() ?? session.WorldGeneration,
                LeaseOwner = mutation ? _leaseOwner : null,
                LeaseToken = leaseToken,
            };
            var response = await _bridge.ExecuteAsync(target, request, cancellationToken);
            if (response.Error?.Code == "stale_world")
                _sessions.TryRemove(targetId, out _);
            CheckResponse(response);
            return JsonSerializer.SerializeToNode(response, DiagnosticProtocol.JsonOptions);
        }
        finally
        {
            if (mutation)
                _mutationGate.Release();
        }
    }

    private async Task<JsonNode?> WaitForConditionAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var command = arguments.RequiredString("command", 64);
        if (!ReadCommands.Contains(command) || command == "entity_snapshot")
            throw new DiagnosticException("invalid_arguments", "Conditions may only call read-only bridge commands without artifact writes.");
        var query = arguments["arguments"]?.AsObject() ?? new JsonObject();
        query = (JsonObject)query.DeepClone();
        query["targetId"] = arguments["targetId"]?.DeepClone();
        var pointer = arguments.RequiredString("path", 512);
        if (!arguments.ContainsKey("equals"))
            throw new DiagnosticException("invalid_arguments", "A condition must explicitly specify its expected value.");
        var seconds = arguments.BoundedInt("timeoutSeconds", 10, 1, 60);
        var interval = arguments.BoundedInt("intervalMilliseconds", 200, 100, 1000);
        var tolerance = arguments["tolerance"]?.GetValue<double>() ?? 0;
        var stableMilliseconds = arguments.BoundedInt("minimumStableMilliseconds", 0, 0, 5000);
        DateTimeOffset? matchedSince = null;
        var until = DateTimeOffset.UtcNow.AddSeconds(seconds);
        do
        {
            JsonNode? result;
            try { result = await ForwardAsync(command, query, cancellationToken); }
            catch (DiagnosticException exception) when (exception.Code is "entity_not_available" or "entity_not_found")
            {
                matchedSince = null;
                await Task.Delay(interval, cancellationToken);
                continue;
            }
            if (JsonInspection.TryAt(result, pointer, out var actual) && JsonInspection.Equal(actual, arguments["equals"], tolerance))
            {
                matchedSince ??= DateTimeOffset.UtcNow;
                if ((DateTimeOffset.UtcNow - matchedSince.Value).TotalMilliseconds >= stableMilliseconds)
                    return new JsonObject { ["matched"] = true, ["path"] = pointer, ["actual"] = actual?.DeepClone(), ["observation"] = result,
                        ["sampledStableMilliseconds"] = stableMilliseconds, ["pollIntervalMilliseconds"] = interval };
            }
            else matchedSince = null;
            await Task.Delay(interval, cancellationToken);
        } while (DateTimeOffset.UtcNow < until);
        throw new DiagnosticException("condition_not_met", "The expected condition was not observed before the deadline.");
    }

    private static void CheckResponse(DiagnosticResponse response)
    {
        if (response.Error is { } error)
            throw new DiagnosticException(error.Code, error.Message, error.Retryable);
    }

    private static readonly HashSet<string> ReadCommands = new(StringComparer.Ordinal)
    {
        "session_get", "capabilities_get", "entities_query", "entity_inspect", "entity_snapshot", "maps_list",
        "map_region_inspect", "prototype_get", "prototype_search", "logs_read", "metrics_capture", "actions_list", "request_status",
        "meta_performance_status", "meta_performance_read", "meta_app_load_status", "meta_app_frontend_list", "meta_app_frontend_read",
        "meta_demo_list", "meta_demo_status",
        "client_inspect", "client_capture", "capture_get", "capture_discard", "ui_inspect", "replication_inspect", "replication_watch", "replication_unwatch",
    };

    internal static bool IsMutation(string command) => command is "action_execute" or "fixture_create" or "fixture_spawn" or
        "fixture_destroy" or "fixture_reset" or "fixture_attach" or "fixture_access" or "fixture_power" or "client_input" or "ui_interact" or
        "meta_performance_start" or "meta_performance_stop" or "meta_demo_create" or "meta_demo_destroy" or
        "meta_app_load_prepare" or "meta_app_load_open" or "meta_app_load_attach" or "meta_app_load_stop" or "meta_app_frontend_capture" or "meta_app_frontend_action" or "meta_app_frontend_stop";

    internal static bool CanRunInScenario(string command) => ReadCommands.Contains(command) || IsMutation(command) ||
        command is "condition_wait" or "snapshot_diff" or "map_region_capture" or "resource_inspect" or "state_compare";

    public void Dispose()
    {
        Operations.Dispose();
        Runtime.Dispose();
        if (_bridge is IDisposable disposable)
            disposable.Dispose();
        _mutationGate.Dispose();
    }
}
