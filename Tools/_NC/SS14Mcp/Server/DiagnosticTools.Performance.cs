// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json.Nodes;
using System.ComponentModel;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

public sealed partial class DiagnosticTools
{
    [McpServerTool(Name = "meta_performance_start", ReadOnly = false, Destructive = false), Description("Start an isolated native META A/B/C/C-resident workload with real physical test robots, durable installation/authority and MessagePort delivery. Requires fixture-admin isolation. Returns a capture ID; no player sessions are fabricated.")]
    public Task<CallToolResult> MetaPerformanceStart(JsonObject arguments, CancellationToken cancellationToken = default) => Call("meta_performance_start", arguments, cancellationToken);
    [McpServerTool(Name = "meta_performance_status", ReadOnly = true), Description("Read bounded actual admission, workload and metric coverage for one isolated META capture. Elapsed spans are not thread CPU measurements.")]
    public Task<CallToolResult> MetaPerformanceStatus(string captureId, string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("meta_performance_status", new JsonObject { ["captureId"] = captureId, ["targetId"] = targetId }, cancellationToken);
    [McpServerTool(Name = "meta_performance_read", ReadOnly = true), Description("Read at most512 detached raw samples after a META capture has stopped. Overflow is explicitly reported by capture status.")]
    public Task<CallToolResult> MetaPerformanceRead(string captureId, int offset = 0, int count = 512, string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("meta_performance_read", new JsonObject { ["captureId"] = captureId, ["offset"] = offset, ["count"] = count, ["targetId"] = targetId }, cancellationToken);
    [McpServerTool(Name = "meta_performance_stop", ReadOnly = false, Destructive = false), Description("Stop an owned isolated META capture. Optional cleanup removes only its native fixture map; durable fixture data belongs to the isolated test database.")]
    public Task<CallToolResult> MetaPerformanceStop(string captureId, bool cleanup = false, string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("meta_performance_stop", new JsonObject { ["captureId"] = captureId, ["cleanup"] = cleanup, ["targetId"] = targetId }, cancellationToken);
}
