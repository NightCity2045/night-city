// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// Persistent diagnostic controller using the real MCP SDK/stdio protocol.
// The selected local server owns its game processes and cleans them on disposal.
if (args.Length != 3 || !Guid.TryParseExact(args[2], "N", out var build))
    throw new ArgumentException("Arguments: workspace local-MCP-server-DLL acceptance-BuildId");
var workspace = Path.GetFullPath(args[0]);
var server = Path.GetFullPath(args[1]);
var prefix = workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
if (!Directory.Exists(workspace) || !server.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(server))
    throw new ArgumentException("The diagnostic MCP server must exist inside the selected workspace.");
var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "meta-owned-acceptance", Command = "dotnet", Arguments = [server],
    EnvironmentVariables = new Dictionary<string, string?>
    {
        ["SS14_MCP_WORKSPACE"] = workspace, ["SS14_MCP_BUILD_ID"] = build.ToString("N"),
        ["SS14_MCP_MODULES"] = "all", ["SS14_MCP_SERVER"] = "", ["SS14_MCP_SERVER_TOKEN"] = "",
        ["SS14_MCP_CLIENT_PIPE"] = "", ["SS14_MCP_CLIENT_TOKEN"] = "",
        ["SS14_MCP_SQLITE_SEED_ID"] = Environment.GetEnvironmentVariable("SS14_MCP_SQLITE_SEED_ID"),
        ["SS14_MCP_SQLITE_SEED_SHA256"] = Environment.GetEnvironmentVariable("SS14_MCP_SQLITE_SEED_SHA256"),
        ["SS14_MCP_SQLITE_MANIFEST_SHA256"] = Environment.GetEnvironmentVariable("SS14_MCP_SQLITE_MANIFEST_SHA256"),
        ["SS14_MCP_SQLITE_HELPER_SHA256"] = Environment.GetEnvironmentVariable("SS14_MCP_SQLITE_HELPER_SHA256"),
    },
});
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; lifetime.Cancel(); };
await using var client = await McpClient.CreateAsync(transport, cancellationToken: lifetime.Token);
var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var availableTools = (await client.ListToolsAsync(cancellationToken: lifetime.Token)).Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
var journalDirectory = Path.Combine(workspace, "artifacts", "meta-mcp-session");
Directory.CreateDirectory(journalDirectory);
var journalPath = Path.Combine(journalDirectory, "controller-" + Guid.NewGuid().ToString("N") + ".jsonl");
using var journal = new StreamWriter(new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
void Emit(object value)
{
    var encoded = JsonSerializer.Serialize(value, json);
    journal.WriteLine(encoded);
    Console.WriteLine(encoded);
}
Emit(new { ready = true, protocol = "MCP-stdio", buildId = build.ToString("N"), journalPath });
try
{
    while (await Console.In.ReadLineAsync(lifetime.Token) is { } line)
    {
        if (Encoding.UTF8.GetByteCount(line) > 64 * 1024) throw new ArgumentException("Controller requests are limited to64KiB.");
        using var request = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
        var root = request.RootElement;
        var tool = root.GetProperty("tool").GetString()!;
        if (tool == "exit") break;
        if (tool == "list")
        {
            Emit(new { tools = availableTools.Order(StringComparer.Ordinal) });
            continue;
        }
        if (tool.Length is < 1 or > 64 || !tool.All(value => char.IsAsciiLetterOrDigit(value) || value == '_'))
            throw new ArgumentException("Invalid local diagnostic tool name.");
        if (!availableTools.Contains(tool))
        {
            Emit(new { tool, error = "unknown_tool", submitted = false });
            continue;
        }
        var arguments = root.TryGetProperty("arguments", out var values)
            ? values.EnumerateObject().ToDictionary(value => value.Name, value => (object?)value.Value.Clone())
            : new Dictionary<string, object?>();
        try
        {
            var result = await client.CallToolAsync(tool, arguments, cancellationToken: lifetime.Token);
            Emit(new
            {
                tool, result.IsError,
                content = result.Content.OfType<TextContentBlock>().Select(value => new { type = "text", text = value.Text }),
                nonTextBlocks = result.Content.Count(value => value is not TextContentBlock),
            });
        }
        catch (ModelContextProtocol.McpProtocolException)
        {
            // A remote protocol error is not grounds to kill an otherwise live test world.
            // Submission may have happened: retain the session for explicit reconciliation.
            Emit(new { tool, error = "tool_protocol_error", outcome = "indeterminate", automaticRetry = false });
        }
    }
}
catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
finally
{
    // Explicitly stop this MCP server's own environments before closing stdio.
    // A cancelled input loop must not rely on child-process EOF timing for cleanup.
    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
    try
    {
        var listed = await client.CallToolAsync("environment_list", new Dictionary<string, object?>(), cancellationToken: cleanup.Token);
        using var data = JsonDocument.Parse(listed.Content.OfType<TextContentBlock>().Single().Text);
        foreach (var environment in data.RootElement.GetProperty("ownedEnvironments").EnumerateArray())
        {
            if (environment.GetProperty("state").GetString() == "stopped") continue;
            var stopped = await client.CallToolAsync("environment_stop", new Dictionary<string, object?>
                { ["environmentId"] = environment.GetProperty("id").GetString() }, cancellationToken: cleanup.Token);
            if (stopped.IsError == true) throw new InvalidOperationException("Owned environment stop was rejected.");
            using var operation = JsonDocument.Parse(stopped.Content.OfType<TextContentBlock>().Single().Text);
            if (!operation.RootElement.TryGetProperty("id", out var id) ||
                !operation.RootElement.TryGetProperty("state", out _)) continue;
            var operationId = id.GetString();
            while (!cleanup.IsCancellationRequested)
            {
                var polled = await client.CallToolAsync("operation_get", new Dictionary<string, object?>
                    { ["operationId"] = operationId }, cancellationToken: cleanup.Token);
                if (polled.IsError == true) throw new InvalidOperationException("Owned stop status is unavailable.");
                using var status = JsonDocument.Parse(polled.Content.OfType<TextContentBlock>().Single().Text);
                if (status.RootElement.GetProperty("state").GetString() is "succeeded" or "failed" or "cancelled") break;
                await Task.Delay(100, cleanup.Token);
            }
        }
    }
    catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
    { Console.Error.WriteLine("Owned MCP cleanup: " + error.GetType().Name); }
}
// No automatic mutation retry. Opaque operation IDs are returned unchanged;
// callers poll/reconcile them through the same MCP endpoint.
