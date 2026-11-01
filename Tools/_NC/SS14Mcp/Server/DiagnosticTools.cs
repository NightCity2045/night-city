// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NightCity.Diagnostics.Contracts;
using NightCity.Diagnostics.Core;

[McpServerToolType]
public sealed partial class DiagnosticTools(DiagnosticApplication application)
{
    [McpServerTool(Name = "environment_list", ReadOnly = true), Description("List registered local SS14 servers and clients. Tokens are never returned.")]
    public Task<CallToolResult> EnvironmentList(CancellationToken cancellationToken) => Call("environment_list", [], cancellationToken);

    [McpServerTool(Name = "session_get", ReadOnly = true), Description("Inspect the selected target's instance, world generation, capabilities and limits before referring to entities.")]
    public Task<CallToolResult> SessionGet(string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("session_get", new JsonObject { ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "environment_attach"), Description("Register an existing local bridge. The secret is read from a configured SS14_MCP_*TOKEN environment variable.")]
    public Task<CallToolResult> EnvironmentAttach(string id, string kind, string address, string tokenEnvironment, string? environmentId = null, CancellationToken cancellationToken = default) =>
        Call("environment_attach", new JsonObject { ["id"] = id, ["kind"] = kind, ["address"] = address,
            ["tokenEnvironment"] = tokenEnvironment, ["environmentId"] = environmentId }, cancellationToken);

    [McpServerTool(Name = "entity_inspect", ReadOnly = true), Description("Read an entity's declared diagnostic fields. Pin serverInstanceId and worldGeneration from its original observation to reject stale references.")]
    public Task<CallToolResult> EntityInspect(string netEntity, string serverInstanceId, long worldGeneration, string[]? components = null,
        string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("entity_inspect", new JsonObject { ["netEntity"] = netEntity, ["serverInstanceId"] = serverInstanceId,
            ["worldGeneration"] = worldGeneration, ["components"] = JsonSerializer.SerializeToNode(components), ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "operation_get", ReadOnly = true), Description("Read an operation's progress or result. Indeterminate mutations must be reconciled before retrying.")]
    public Task<CallToolResult> OperationGet(string operationId, CancellationToken cancellationToken) =>
        Call("operation_get", new JsonObject { ["operationId"] = operationId }, cancellationToken);

    [McpServerTool(Name = "operation_cancel"), Description("Request cancellation of remaining work. Already performed game actions cannot be rolled back.")]
    public Task<CallToolResult> OperationCancel(string operationId, CancellationToken cancellationToken) =>
        Call("operation_cancel", new JsonObject { ["operationId"] = operationId }, cancellationToken);

    private async Task<CallToolResult> Call(string command, JsonObject arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await application.ExecuteAsync(command, arguments, cancellationToken);
            return new CallToolResult { Content = [new TextContentBlock { Text = result?.ToJsonString() ?? "null" }],
                StructuredContent = JsonSerializer.SerializeToElement(result is JsonObject ? result : new JsonObject { ["data"] = result }) };
        }
        catch (DiagnosticException exception)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock
                { Text = DiagnosticProtocol.Serialize(new DiagnosticFault(exception.Code, exception.Message, exception.Retryable)) }] };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock
                { Text = DiagnosticProtocol.Serialize(new DiagnosticFault("invalid_arguments", "A supplied argument has an invalid shape or value type.")) }] };
        }
    }
}
