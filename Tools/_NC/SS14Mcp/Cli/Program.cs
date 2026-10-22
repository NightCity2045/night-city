// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json;
using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;
using NightCity.Diagnostics.Core;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; cancellation.Cancel(); };
try
{
    if (args.Length == 0 || args[0] is "--help" or "help")
    {
        Console.WriteLine("""
            SS14 diagnostics: <command> [JSON arguments] [--args-file file.json] [--workspace path] [--stay]
            Single commands await operation completion and return JSON.
            environment_start requires --stay: Ctrl+C stops all processes owned by this CLI.
            session reads JSON lines {"command":"...","arguments":{...}} and returns results/operation handles.
            Use operation_get to poll. {"command":"exit"} or EOF stops owned processes.
            """);
        return 0;
    }
    string? Option(string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new DiagnosticException("invalid_arguments", name + " requires a value.");
        return args[index + 1];
    }
    using var application = new DiagnosticApplication(Option("--workspace") ?? Directory.GetCurrentDirectory());
    if (args[0] == "session")
    {
        while (await Console.In.ReadLineAsync(cancellation.Token) is { } line)
        {
            try
            {
                if (System.Text.Encoding.UTF8.GetByteCount(line) > DiagnosticProtocol.MaximumRequestBytes)
                    throw new DiagnosticException("request_too_large", "Session requests may not exceed 64 KiB.");
                var request = JsonNode.Parse(line)!.AsObject();
                var command = request.RequiredString("command", 64);
                if (command == "exit") break;
                var sessionResult = await application.ExecuteAsync(command, request["arguments"]?.AsObject() ?? new JsonObject(), cancellation.Token);
                Console.WriteLine(sessionResult?.ToJsonString(DiagnosticProtocol.JsonOptions) ?? "null");
            }
            catch (DiagnosticException exception) { PrintError(exception); }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
            { PrintError(new DiagnosticException("invalid_arguments", "Expected a command object with valid JSON argument types.")); }
        }
        return 0;
    }
    var stay = args.Contains("--stay", StringComparer.Ordinal);
    if (args[0] == "environment_start" && !stay)
        throw new DiagnosticException("session_required", "Use environment_start --stay or session mode to keep the owning CLI alive.");
    var argumentText = Option("--args-file") is { } file ? await File.ReadAllTextAsync(file, cancellation.Token) :
        args.Length > 1 && args[1].StartsWith('{') ? args[1] : "{}";
    if (System.Text.Encoding.UTF8.GetByteCount(argumentText) > DiagnosticProtocol.MaximumRequestBytes)
        throw new DiagnosticException("request_too_large", "Arguments may not exceed 64 KiB.");
    var arguments = JsonNode.Parse(argumentText)!.AsObject();
    var result = await application.ExecuteAsync(args[0], arguments, cancellation.Token);
    var exitCode = 0;
    if (result is JsonObject operationInfo && operationInfo["name"] != null && operationInfo["id"]?.GetValue<string>() is { } operationId && operationInfo["state"] != null)
    {
        DiagnosticOperation operation;
        do
        {
            await Task.Delay(100, cancellation.Token);
            operation = application.Operations.Get(operationId);
        } while (operation.FinishedAt == null);
        Console.WriteLine(DiagnosticProtocol.Serialize(operation));
        exitCode = operation.State == "succeeded" ? 0 : 1;
    }
    else Console.WriteLine(result?.ToJsonString(DiagnosticProtocol.JsonOptions) ?? "null");
    if (stay && exitCode == 0)
    {
        Console.Error.WriteLine("Owned environment is active. Ctrl+C stops its processes.");
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
    }
    return exitCode;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (DiagnosticException exception)
{
    PrintError(exception);
    return 1;
}
catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
{
    PrintError(new DiagnosticException("invalid_arguments", "Invalid JSON arguments or value types."));
    return 2;
}

static void PrintError(DiagnosticException exception) => Console.WriteLine(DiagnosticProtocol.Serialize(new
{ error = new DiagnosticFault(exception.Code, exception.Message, exception.Retryable) }));
