// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public sealed partial class DiagnosticTools
{
    [McpServerTool(Name = "fixture_power", ReadOnly = false, Destructive = true), Description("Configure native APC receiver power requirements on a fixture-owned entity. Isolated fixture-admin only.")]
    public Task<CallToolResult> FixturePower(string fixtureId, string netEntity, string serverInstanceId, long worldGeneration, bool needsPower = true, bool disabled = false, string targetId = "server", string? operationId = null, CancellationToken cancellationToken = default) =>
        Call("fixture_power", new JsonObject { ["fixtureId"] = fixtureId, ["netEntity"] = netEntity, ["serverInstanceId"] = serverInstanceId,
            ["worldGeneration"] = worldGeneration, ["needsPower"] = needsPower, ["disabled"] = disabled,
            ["targetId"] = targetId, ["operationId"] = operationId }, cancellationToken);

    [McpServerTool(Name = "fixture_attach", ReadOnly = false, Destructive = true), Description("Attach a connected client to a fixture-owned actor through the native mind and ticker systems. Isolated fixture-admin only.")]
    public Task<CallToolResult> FixtureAttach(string fixtureId, string actor, string userId, string serverInstanceId, long worldGeneration, string targetId = "server", string? operationId = null, CancellationToken cancellationToken = default) =>
        Call("fixture_attach", new JsonObject { ["fixtureId"] = fixtureId, ["actor"] = actor, ["userId"] = userId,
            ["serverInstanceId"] = serverInstanceId, ["worldGeneration"] = worldGeneration, ["targetId"] = targetId, ["operationId"] = operationId }, cancellationToken);

    [McpServerTool(Name = "fixture_access", ReadOnly = false, Destructive = true), Description("Set access provider tags or reader requirements using native access systems on a fixture-owned entity. kind is provider or reader.")]
    public Task<CallToolResult> FixtureAccess(string fixtureId, string netEntity, string[] tags, string serverInstanceId, long worldGeneration, string kind = "provider", string targetId = "server", string? operationId = null, CancellationToken cancellationToken = default) =>
        Call("fixture_access", new JsonObject { ["fixtureId"] = fixtureId, ["netEntity"] = netEntity, ["tags"] = JsonSerializer.SerializeToNode(tags),
            ["kind"] = kind, ["serverInstanceId"] = serverInstanceId, ["worldGeneration"] = worldGeneration,
            ["targetId"] = targetId, ["operationId"] = operationId }, cancellationToken);

    [McpServerTool(Name = "state_compare", ReadOnly = false, Destructive = false), Description("Compare explicit entity paths across a paired server and up to three clients. Conclusive authoritative coverage requires watched Door state received during a stable server observation window; other replicas report prediction coverage.")]
    public Task<CallToolResult> StateCompare(string targetId, string netEntity, string serverInstanceId, long worldGeneration, string[] clientIds, string[] paths, int timeoutSeconds = 5, CancellationToken cancellationToken = default) =>
        Call("state_compare", new JsonObject { ["targetId"] = targetId, ["netEntity"] = netEntity, ["serverInstanceId"] = serverInstanceId,
            ["worldGeneration"] = worldGeneration, ["clientIds"] = JsonSerializer.SerializeToNode(clientIds),
            ["paths"] = JsonSerializer.SerializeToNode(paths), ["timeoutSeconds"] = timeoutSeconds }, cancellationToken);

    [McpServerTool(Name = "trace_start", ReadOnly = false, Destructive = false), Description("Start a bounded trace of entity, replication, metrics or log observations, up to 60 seconds. This is sampled evidence, not a causal event trace. Returns an operation handle.")]
    public Task<CallToolResult> TraceStart(string command, JsonObject arguments, string targetId = "server", int durationSeconds = 10, int intervalMilliseconds = 200, CancellationToken cancellationToken = default) =>
        Call("trace_start", new JsonObject { ["command"] = command, ["arguments"] = arguments.DeepClone(), ["targetId"] = targetId,
            ["durationSeconds"] = durationSeconds, ["intervalMilliseconds"] = intervalMilliseconds }, cancellationToken);

    [McpServerTool(Name = "trace_read", ReadOnly = true), Description("Read trace operation status and its retained final artifact.")]
    public Task<CallToolResult> TraceRead(string operationId, CancellationToken cancellationToken = default) =>
        Call("trace_read", new JsonObject { ["operationId"] = operationId }, cancellationToken);

    [McpServerTool(Name = "trace_stop", ReadOnly = false, Destructive = false), Description("Stop a sampled trace and preserve the observations already collected.")]
    public Task<CallToolResult> TraceStop(string operationId, CancellationToken cancellationToken = default) =>
        Call("trace_stop", new JsonObject { ["operationId"] = operationId }, cancellationToken);
}
