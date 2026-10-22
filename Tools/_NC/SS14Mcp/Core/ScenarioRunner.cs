// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

public sealed class ScenarioRunner(DiagnosticApplication application)
{
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly AsyncLocal<bool> _executionContext = new();
    private int _running;
    public bool Running => Volatile.Read(ref _running) != 0;
    public bool IsExecutionContext => _executionContext.Value;

    public async Task<JsonObject> LoadAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        JsonObject document;
        if (arguments["scenario"] is JsonObject supplied)
            document = (JsonObject)supplied.DeepClone();
        else
        {
            var relative = arguments.RequiredString("path", 512).Replace('\\', '/');
            if (!relative.StartsWith("Tools/_NC/SS14Mcp/Scenarios/", StringComparison.Ordinal) || !relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new DiagnosticException("invalid_scenario_path", "Scenario files must be JSON documents under Tools/_NC/SS14Mcp/Scenarios/.");
            var path = application.Paths.Resolve(relative);
            if (!File.Exists(path))
                throw new DiagnosticException("scenario_not_found", "The scenario file does not exist.");
            await using var stream = File.OpenRead(path);
            var bytes = await DiagnosticWire.ReadBoundedAsync(stream, DiagnosticProtocol.MaximumRequestBytes, cancellationToken);
            document = JsonNode.Parse(bytes)?.AsObject() ?? throw new DiagnosticException("invalid_scenario", "Expected a scenario object.");
        }
        Validate(document);
        return document;
    }

    public static void Validate(JsonObject document)
    {
        if (document.BoundedInt("version", 1, 1, 1) != 1)
            throw new DiagnosticException("invalid_scenario", "Unsupported scenario version.");
        document.RequiredString("id", 100);
        document.BoundedInt("maximumSeconds", 300, 10, 900);
        if (document["targetId"] != null) document.RequiredString("targetId", 64);
        if (document["environment"] is JsonObject environment)
        {
            environment.BoundedInt("clients", 0, 0, 3);
            if (environment["graphics"] != null && (environment["graphics"] is not JsonValue graphics || !graphics.TryGetValue<bool>(out _)))
                throw new DiagnosticException("invalid_scenario", "Environment graphics must be a boolean.");
        }
        if (Encoding.UTF8.GetByteCount(document.ToJsonString()) > DiagnosticProtocol.MaximumRequestBytes ||
            document["steps"] is not JsonArray { Count: > 0 and <= 64 } steps)
            throw new DiagnosticException("invalid_scenario", "A scenario must have 1..64 steps and fit within 64 KiB.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in steps)
        {
            if (item is not JsonObject step || !ids.Add(step.RequiredString("id", 64)))
                throw new DiagnosticException("invalid_scenario", "Every step must be an object with a unique identifier.");
            if ((step["assert"] != null) == (step["command"] != null) ||
                step["assert"] != null && step["assert"] is not JsonObject ||
                step["arguments"] != null && step["arguments"] is not JsonObject ||
                step["assert"] != null && step["expectError"] != null)
                throw new DiagnosticException("invalid_scenario", "A step must contain either an assertion or a command with object arguments and optional expected error.");
            if (step["assert"] is JsonObject assertion)
            {
                if (!assertion.ContainsKey("actual") || !assertion.ContainsKey("equals"))
                    throw new DiagnosticException("invalid_scenario", "Assertions must explicitly contain actual and equals values.");
            }
            else if (!DiagnosticApplication.CanRunInScenario(step.RequiredString("command", 64)))
                throw new DiagnosticException("unsupported_scenario_command", "The step command is outside the declarative scenario catalogue.");
            if (step["expectError"] != null) step.RequiredString("expectError", 64);
        }
    }

    public async Task<JsonNode?> RunAsync(JsonObject document, CancellationToken cancellationToken)
    {
        Validate(document);
        if (!await _runGate.WaitAsync(0, cancellationToken))
            throw new DiagnosticException("busy", "Only one scenario may run in this MCP process at a time.", true);
        Interlocked.Exchange(ref _running, 1);
        _executionContext.Value = true;
        var steps = new JsonArray();
        var cleanup = new JsonArray();
        var context = new JsonObject { ["steps"] = new JsonObject(), ["targets"] = new JsonObject { ["server"] = "server" } };
        var fixtures = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        string? environmentId = null;
        DiagnosticFault? failure = null;
        var cancelled = false;
        string[] reservations = [];
        Task? renewal = null;
        DiagnosticFault? leaseFailure = null;
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(document.BoundedInt("maximumSeconds", 300, 10, 900)));
        using var renewLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        try
        {
            if (document["environment"] is JsonObject environment && environment["start"]?.GetValue<bool>() == true)
            {
                var started = await AwaitResultAsync(await application.ExecuteAsync("environment_start", new JsonObject
                {
                    ["clients"] = environment.BoundedInt("clients", 0, 0, 3),
                    ["graphics"] = environment["graphics"]?.DeepClone() ?? JsonValue.Create(false),
                }, deadline.Token), deadline.Token);
                environmentId = started!["id"]!.GetValue<string>();
                context["environment"] = started.DeepClone();
                var targets = started["targets"]!.AsArray();
                for (var index = 0; index < targets.Count; index++)
                    context["targets"]![index == 0 ? "server" : "client" + index] = targets[index]!.DeepClone();
            }
            else if (document["targetId"] is JsonValue target)
                context["targets"]!["server"] = target.DeepClone();

            if (document["steps"]!.AsArray().Any(step => step?["command"]?.GetValue<string>() is { } command && DiagnosticApplication.IsMutation(command)))
            {
                reservations = application.ReservationTargets(context["targets"]!.AsObject().RequiredString("server", 64));
                await application.RenewReservationsAsync(reservations, false, deadline.Token);
                renewal = Task.Run(async () =>
                {
                    try
                    {
                        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                        while (await timer.WaitForNextTickAsync(renewLifetime.Token))
                            await application.RenewReservationsAsync(reservations, false, renewLifetime.Token);
                    }
                    catch (OperationCanceledException) { }
                    catch (DiagnosticException exception)
                    {
                        leaseFailure = new DiagnosticFault(exception.Code, "The scenario lost its mutation reservation.", exception.Retryable);
                        deadline.Cancel();
                    }
                }, CancellationToken.None);
            }

            foreach (var item in document["steps"]!.AsArray())
            {
                deadline.Token.ThrowIfCancellationRequested();
                var step = item!.AsObject();
                var id = step.RequiredString("id", 64);
                var stepClock = Stopwatch.StartNew();
                JsonNode? assertionEvidence = null;
                try
                {
                    JsonNode? result;
                    if (step["assert"] is JsonObject assertion)
                    {
                        var actual = Resolve(assertion["actual"], context, DiagnosticProtocol.MaximumResponseBytes);
                        var expected = Resolve(assertion["equals"], context, DiagnosticProtocol.MaximumResponseBytes);
                        assertionEvidence = new JsonObject { ["actual"] = actual?.DeepClone(), ["expected"] = expected?.DeepClone() };
                        if (!JsonInspection.Equal(actual, expected, assertion["tolerance"]?.GetValue<double>() ?? 0))
                            throw new DiagnosticException("assertion_failed", "The scenario assertion did not match the observed value.");
                        result = new JsonObject { ["matched"] = true, ["actual"] = actual, ["expected"] = expected };
                    }
                    else
                    {
                        var command = step.RequiredString("command", 64);
                        var args = Resolve(step["arguments"] ?? new JsonObject(), context, DiagnosticProtocol.MaximumRequestBytes)!.AsObject();
                        args["targetId"] ??= context["targets"]!["server"]!.DeepClone();
                        var expectedError = step["expectError"]?.GetValue<string>();
                        var observedExpectedError = false;
                        try
                        {
                            result = await AwaitResultAsync(await application.ExecuteAsync(command, args, deadline.Token), deadline.Token);
                        }
                        catch (DiagnosticException exception) when (exception.Code == expectedError)
                        {
                            result = new JsonObject { ["expectedError"] = true, ["code"] = exception.Code };
                            observedExpectedError = true;
                        }
                        if (expectedError != null && !observedExpectedError)
                            throw new DiagnosticException("expected_error_not_observed", "The command succeeded instead of returning the expected error.");
                        if (command == "fixture_create" && result?["data"]?["fixtureId"]?.GetValue<string>() is { } fixtureId)
                            fixtures[args["targetId"]!.GetValue<string>() + ":" + fixtureId] = new JsonObject
                            {
                                ["targetId"] = args["targetId"]!.DeepClone(), ["fixtureId"] = fixtureId,
                                ["serverInstanceId"] = result["serverInstanceId"]!.DeepClone(),
                                ["worldGeneration"] = result["worldGeneration"]!.DeepClone(),
                            };
                        if (command == "fixture_destroy")
                            fixtures.Remove(args["targetId"]!.GetValue<string>() + ":" + args.RequiredString("fixtureId", 64));
                    }
                    context["steps"]![id] = result?.DeepClone();
                    if (Encoding.UTF8.GetByteCount(context.ToJsonString()) > 4 * 1024 * 1024)
                        throw new DiagnosticException("scenario_result_limit", "The scenario observation context exceeds 4 MiB.");
                    steps.Add(new JsonObject { ["id"] = id, ["passed"] = true, ["milliseconds"] = stepClock.ElapsedMilliseconds, ["result"] = result });
                }
                catch (DiagnosticException exception)
                {
                    failure = new DiagnosticFault(exception.Code, exception.Message, exception.Retryable);
                    steps.Add(new JsonObject { ["id"] = id, ["passed"] = false, ["milliseconds"] = stepClock.ElapsedMilliseconds,
                        ["assertion"] = assertionEvidence,
                        ["error"] = JsonSerializer.SerializeToNode(failure, DiagnosticProtocol.JsonOptions) });
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = cancellationToken.IsCancellationRequested;
            failure = new DiagnosticFault(cancelled ? "cancelled" : "scenario_timeout", "The scenario stopped before completing all steps.");
        }
        catch (DiagnosticException exception)
        {
            failure = new DiagnosticFault(exception.Code, exception.Message, exception.Retryable);
        }
        catch (Exception)
        {
            failure = new DiagnosticFault("scenario_failed", "The scenario stopped unexpectedly.");
        }
        finally
        {
            renewLifetime.Cancel();
            if (renewal != null)
                await renewal;
            failure = leaseFailure ?? failure;
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            foreach (var fixture in fixtures.Values)
            {
                try
                {
                    await AwaitResultAsync(await application.ExecuteAsync("fixture_destroy", fixture, cleanupDeadline.Token), cleanupDeadline.Token);
                    cleanup.Add(new JsonObject { ["fixtureId"] = fixture["fixtureId"]!.DeepClone(), ["passed"] = true });
                }
                catch (Exception)
                {
                    cleanup.Add(new JsonObject { ["fixtureId"] = fixture["fixtureId"]!.DeepClone(), ["passed"] = false });
                }
            }
            try { await application.RenewReservationsAsync(reservations, true, cleanupDeadline.Token); }
            catch (DiagnosticException) { cleanup.Add(new JsonObject { ["reservationRelease"] = true, ["passed"] = false }); }
            if (environmentId != null)
            {
                try
                {
                    await AwaitResultAsync(await application.ExecuteAsync("environment_stop", new JsonObject { ["environmentId"] = environmentId }, cleanupDeadline.Token), cleanupDeadline.Token);
                    cleanup.Add(new JsonObject { ["environmentId"] = environmentId, ["passed"] = true });
                }
                catch (Exception)
                {
                    cleanup.Add(new JsonObject { ["environmentId"] = environmentId, ["passed"] = false });
                }
            }
            _executionContext.Value = false;
            Interlocked.Exchange(ref _running, 0);
            _runGate.Release();
        }
        if (cleanup.Any(x => x?["passed"]?.GetValue<bool>() == false))
            failure ??= new DiagnosticFault("cleanup_incomplete", "Some test resources could not be cleaned up.");
        var report = new JsonObject { ["scenarioId"] = document.RequiredString("id", 100), ["passed"] = failure == null,
            ["milliseconds"] = clock.ElapsedMilliseconds, ["steps"] = steps, ["cleanup"] = cleanup,
            ["error"] = JsonSerializer.SerializeToNode(failure, DiagnosticProtocol.JsonOptions),
            ["targets"] = context["targets"]!.DeepClone(), ["environment"] = context["environment"]?.DeepClone() };
        var artifact = await application.Artifacts.WriteJsonAsync("scenario", report, CancellationToken.None);
        var html = await CreateReportAsync(report, CancellationToken.None);
        var resultNode = new JsonObject { ["passed"] = failure == null, ["report"] = report,
            ["jsonArtifact"] = JsonSerializer.SerializeToNode(artifact, DiagnosticProtocol.JsonOptions),
            ["htmlArtifact"] = JsonSerializer.SerializeToNode(html, DiagnosticProtocol.JsonOptions) };
        if (failure != null)
            throw new DiagnosticOperationFailedException(failure, resultNode,
                failure.Code == "outcome_unknown" ? "indeterminate" : cancelled ? "cancelled" : "failed");
        return resultNode;
    }

    public async Task<DiagnosticArtifact> CreateReportAsync(JsonNode report, CancellationToken cancellationToken)
    {
        var escaped = WebUtility.HtmlEncode(report.ToJsonString(new JsonSerializerOptions(DiagnosticProtocol.JsonOptions) { WriteIndented = true }));
        var html = "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">" +
            "<title>SS14 diagnostic report</title><style>body{margin:2rem;background:#111827;color:#e5e7eb;font:15px system-ui}pre{white-space:pre-wrap;overflow-wrap:anywhere}</style>" +
            "<h1>SS14 diagnostic report</h1><pre>" + escaped + "</pre></html>";
        return await application.Artifacts.WriteAsync("report", "text/html", Encoding.UTF8.GetBytes(html), cancellationToken);
    }

    private async Task<JsonNode?> AwaitResultAsync(JsonNode? result, CancellationToken cancellationToken)
    {
        if (result is not JsonObject obj || obj["name"] == null || obj["state"] == null || obj["id"]?.GetValue<string>() is not { } id)
            return result;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operation = application.Operations.Get(id);
                if (operation.FinishedAt != null)
                {
                    if (operation.State != "succeeded")
                        throw new DiagnosticException(operation.State == "indeterminate" ? "outcome_unknown" : operation.Error?.Code ?? "operation_failed",
                            operation.Error?.Message ?? "The scenario operation failed.");
                    return operation.Result;
                }
                await Task.Delay(50, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            application.Operations.Cancel(id);
            var until = DateTimeOffset.UtcNow.AddSeconds(15);
            while (application.Operations.Get(id).FinishedAt == null && DateTimeOffset.UtcNow < until)
                await Task.Delay(50, CancellationToken.None);
            if (application.Operations.Get(id).FinishedAt == null)
                throw new DiagnosticException("cleanup_incomplete", "Cancellation was requested but a child operation did not finish cleanup before the deadline.");
            throw;
        }
    }

    public static JsonNode? Resolve(JsonNode? source, JsonObject context, int maximumBytes)
    {
        var budget = maximumBytes;
        return ResolveNode(source, context, 0, ref budget);
    }

    private static JsonNode? ResolveNode(JsonNode? source, JsonObject context, int depth, ref int budget)
    {
        if (--budget < 0 || depth > 24)
            throw new DiagnosticException("scenario_reference_limit", "The resolved scenario arguments exceed their size or depth limit.");
        if (source is JsonObject reference && reference.Count == 1 && reference["$ref"] is JsonValue pointer)
        {
            if (!JsonInspection.TryAt(context, pointer.GetValue<string>(), out var value))
                throw new DiagnosticException("scenario_reference_missing", "A scenario reference points to an unobserved value.");
            budget -= value?.ToJsonString().Length ?? 4;
            if (budget < 0)
                throw new DiagnosticException("scenario_reference_limit", "The referenced observation is too large for these arguments.");
            return value?.DeepClone();
        }
        if (source is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var (name, value) in obj)
                result[name] = ResolveNode(value, context, depth + 1, ref budget);
            return result;
        }
        if (source is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var value in array)
                result.Add(ResolveNode(value, context, depth + 1, ref budget));
            return result;
        }
        budget -= source?.ToJsonString().Length ?? 4;
        if (budget < 0)
            throw new DiagnosticException("scenario_reference_limit", "The resolved value exceeds its size limit.");
        return source?.DeepClone();
    }
}
