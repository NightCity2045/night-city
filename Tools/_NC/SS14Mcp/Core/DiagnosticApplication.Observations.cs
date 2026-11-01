// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

public sealed partial class DiagnosticApplication
{
    private async Task<JsonNode?> CompareStateAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var serverId = args.RequiredString("targetId", 64);
        if (!_targets.TryGetValue(serverId, out var server) || server.Kind != "server" || server.EnvironmentId == null)
            throw new DiagnosticException("unpaired_target", "State comparison requires a server and clients registered in the same environment group.");
        var clients = args["clientIds"]?.AsArray().Select(item => item!.GetValue<string>()).ToArray() ?? [];
        var paths = args["paths"]?.AsArray().Select(item => item!.GetValue<string>()).ToArray() ?? [];
        if (clients.Length is < 1 or > 3 || clients.Distinct().Count() != clients.Length || paths.Length is < 1 or > 32)
            throw new DiagnosticException("invalid_arguments", "Supply one to three distinct clients and one to 32 explicit JSON paths.");
        foreach (var client in clients)
            if (!_targets.TryGetValue(client, out var target) || target.Kind != "client" || target.EnvironmentId != server.EnvironmentId)
                throw new DiagnosticException("unpaired_target", "Every client must belong to the selected server environment.");
        var verifiedPairing = Runtime.List().Any(environment => environment.Id == server.EnvironmentId && environment.State == "running" &&
            environment.Targets.Contains(serverId, StringComparer.Ordinal) && clients.All(client => environment.Targets.Contains(client, StringComparer.Ordinal)));
        var before = await ForwardAsync("entity_inspect", args, cancellationToken);
        var tick = before!["capturedTick"]!.GetValue<long>();
        var checks = new JsonArray();
        foreach (var id in clients)
        {
            var query = new JsonObject { ["targetId"] = id, ["netEntity"] = args["netEntity"]!.DeepClone() };
            JsonNode? replica = null;
            DiagnosticFault? unavailable = null;
            var until = DateTimeOffset.UtcNow.AddSeconds(args.BoundedInt("timeoutSeconds", 5, 1, 30));
            do
            {
                try { replica = await ForwardAsync("replication_inspect", query, cancellationToken); }
                catch (DiagnosticException exception) when (exception.Code == "entity_not_available")
                { unavailable = new DiagnosticFault(exception.Code, exception.Message); }
                if (replica?["data"]?["lastRealTick"]?.GetValue<long>() >= tick)
                    break;
                await Task.Delay(100, cancellationToken);
            } while (DateTimeOffset.UtcNow < until);
            var after = await ForwardAsync("entity_inspect", args, cancellationToken);
            var stable = JsonInspection.Diff(before["data"], after!["data"], paths);
            var diff = JsonInspection.Diff(before["data"], replica?["data"]?["entity"], paths);
            var caughtUp = replica?["data"]?["lastRealTick"]?.GetValue<long>() >= tick;
            var stableWindow = stable["equal"]?.GetValue<bool>() == true && stable["conclusive"]?.GetValue<bool>() == true;
            var history = replica?["data"]?["authoritativeDoorHistory"]?.AsArray().LastOrDefault(item => item?["appliedTick"]?.GetValue<long>() >= tick);
            var authoritative = paths.All(path => path is "/components/Door/fields/State" or "/components/Door/fields/Partial") && history != null;
            var authoritativeMatch = authoritative && paths.All(path =>
                JsonInspection.TryAt(before["data"], path, out var expected) &&
                history!.AsObject().TryGetPropertyValue(path.EndsWith("/State", StringComparison.Ordinal) ? "State" : "Partial", out var observed) &&
                JsonInspection.Equal(expected, observed));
            checks.Add(new JsonObject
            {
                ["clientId"] = id, ["serverCaptureTick"] = tick, ["clientCaughtUp"] = caughtUp, ["serverStableWindow"] = stableWindow,
                ["currentReplicaDiff"] = diff, ["conclusive"] = verifiedPairing && caughtUp && stableWindow && authoritative,
                ["matched"] = verifiedPairing && caughtUp && stableWindow && authoritative ? authoritativeMatch : null,
                ["pairingVerified"] = verifiedPairing,
                ["coverage"] = authoritative ? "Door-state-application-in-stable-observation-window" : "current-replica-may-include-prediction",
                ["authoritativeState"] = history?.DeepClone(), ["replica"] = replica,
                ["unavailable"] = JsonSerializer.SerializeToNode(unavailable, DiagnosticProtocol.JsonOptions),
            });
        }
        var result = new JsonObject { ["serverObservation"] = before, ["checks"] = checks,
            ["pairingCoverage"] = verifiedPairing ? "supervisor-owned-loopback-environment" : "user-declared-group-unverified",
            ["conclusive"] = checks.All(item => item?["conclusive"]?.GetValue<bool>() == true),
            ["matched"] = checks.All(item => item?["matched"]?.GetValue<bool>() == true),
            ["timingCoverage"] = "bounded-live-observation-window-not-an-atomic-world-snapshot" };
        var artifact = await Artifacts.WriteJsonAsync("comparison", result, cancellationToken);
        return new JsonObject { ["comparison"] = result, ["artifact"] = JsonSerializer.SerializeToNode(artifact, DiagnosticProtocol.JsonOptions) };
    }

    private async Task<JsonNode?> SampleTraceAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var command = args.RequiredString("command", 64);
        if (command is not ("entity_inspect" or "replication_inspect" or "metrics_capture" or "logs_read"))
            throw new DiagnosticException("invalid_arguments", "Traces sample entity, replication, metrics or log observations only.");
        var query = (JsonObject)(args["arguments"]?.DeepClone() ?? new JsonObject());
        query["targetId"] = args["targetId"]?.DeepClone();
        var seconds = args.BoundedInt("durationSeconds", 10, 1, 60);
        var interval = args.BoundedInt("intervalMilliseconds", 200, 100, 5000);
        var samples = new JsonArray();
        var until = DateTimeOffset.UtcNow.AddSeconds(seconds);
        var bytes = 0;
        var truncated = false;
        var stopped = false;
        DiagnosticFault? failure = null;
        try
        {
            do
            {
                var observation = await ForwardAsync(command, query, cancellationToken);
                bytes += Encoding.UTF8.GetByteCount(observation!.ToJsonString());
                if (bytes > 4 * 1024 * 1024 || samples.Count == 600)
                { truncated = true; break; }
                samples.Add(new JsonObject { ["observedAt"] = DateTimeOffset.UtcNow.ToString("O"), ["observation"] = observation });
                if (command == "logs_read" && observation["data"]?["nextCursor"] is { } cursor)
                    query["cursor"] = cursor.DeepClone();
                await Task.Delay(interval, cancellationToken);
            } while (DateTimeOffset.UtcNow < until);
        }
        catch (OperationCanceledException) { stopped = true; }
        catch (DiagnosticException exception) { failure = new DiagnosticFault(exception.Code, exception.Message, exception.Retryable); }
        var trace = new JsonObject { ["coverage"] = "sampled-observations-no-event-causality-guarantee", ["command"] = command,
            ["truncated"] = truncated, ["stopped"] = stopped, ["samples"] = samples,
            ["error"] = JsonSerializer.SerializeToNode(failure, DiagnosticProtocol.JsonOptions) };
        var artifact = await Artifacts.WriteJsonAsync("trace", trace, CancellationToken.None);
        var result = new JsonObject { ["trace"] = trace, ["artifact"] = JsonSerializer.SerializeToNode(artifact, DiagnosticProtocol.JsonOptions) };
        if (failure != null) throw new DiagnosticOperationFailedException(failure, result);
        return result;
    }
}
