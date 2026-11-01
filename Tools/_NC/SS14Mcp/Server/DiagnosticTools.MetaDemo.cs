// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public sealed partial class DiagnosticTools
{
    [McpServerTool(Name = "meta_demo_list", ReadOnly = true), Description("List physical META scenes owned by the selected isolated fixture-admin server.")]
    public Task<CallToolResult> MetaDemoList(string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("meta_demo_list", new JsonObject { ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "meta_demo_status", ReadOnly = true), Description("Inspect actual physical devices and readiness in an owned META scene.")]
    public Task<CallToolResult> MetaDemoStatus(string fixtureId, string serverInstanceId, long worldGeneration,
        string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("meta_demo_status", new JsonObject { ["fixtureId"] = fixtureId, ["serverInstanceId"] = serverInstanceId,
            ["worldGeneration"] = worldGeneration, ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "meta_demo_create", ReadOnly = false, Destructive = false), Description("Create a physical authored META scene in an isolated fixture-admin world. Ownership and automation are not granted.")]
    public Task<CallToolResult> MetaDemoCreate(string scene, string serverInstanceId, long worldGeneration,
        string targetId = "server", string? operationId = null, CancellationToken cancellationToken = default) =>
        Call("meta_demo_create", new JsonObject { ["scene"] = scene, ["serverInstanceId"] = serverInstanceId,
            ["worldGeneration"] = worldGeneration, ["targetId"] = targetId, ["operationId"] = operationId }, cancellationToken);

    [McpServerTool(Name = "meta_demo_destroy", ReadOnly = false, Destructive = true), Description("Delete only a META scene owned by the selected isolated fixture-admin server.")]
    public Task<CallToolResult> MetaDemoDestroy(string fixtureId, string serverInstanceId, long worldGeneration,
        string targetId = "server", string? operationId = null, CancellationToken cancellationToken = default) =>
        Call("meta_demo_destroy", new JsonObject { ["fixtureId"] = fixtureId, ["serverInstanceId"] = serverInstanceId,
            ["worldGeneration"] = worldGeneration, ["targetId"] = targetId, ["operationId"] = operationId }, cancellationToken);
}
