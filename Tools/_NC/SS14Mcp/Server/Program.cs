// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;
using ModelContextProtocol.Server;
using NightCity.Diagnostics.Core;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
using var application = new DiagnosticApplication(
    Environment.GetEnvironmentVariable("SS14_MCP_WORKSPACE") ?? Directory.GetCurrentDirectory());
builder.Services.AddSingleton(application);
var configuredModules = (Environment.GetEnvironmentVariable("SS14_MCP_MODULES") ?? "all")
    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
string[] knownModules = ["all", "common", "runtime", "entities", "content", "maps", "fixtures", "actions", "client", "ui", "replication", "scenarios", "artifacts", "diagnostics"];
if (configuredModules.Count == 0 || configuredModules.Any(module => !knownModules.Contains(module, StringComparer.Ordinal)))
    throw new ArgumentException("SS14_MCP_MODULES contains an unknown or empty module selection.");
var toolInstance = new DiagnosticTools(application);
var tools = typeof(DiagnosticTools).GetMethods().Select(method =>
    (Method: method, Attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
    .Where(entry => entry.Attribute != null && (configuredModules.Contains("all") ||
        Module(entry.Attribute.Name!) == "common" || configuredModules.Contains(Module(entry.Attribute.Name!))))
    .Select(entry => McpServerTool.Create(entry.Method, toolInstance, new McpServerToolCreateOptions { Name = entry.Attribute!.Name }));
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools(tools);
await builder.Build().RunAsync();

static string Module(string name) => name switch
{
    "environment_list" or "environment_attach" or "session_get" or "capabilities_get" or "operation_get" or "operation_cancel" => "common",
    _ when name.StartsWith("environment_", StringComparison.Ordinal) => "runtime",
    _ when name.StartsWith("entity", StringComparison.Ordinal) => "entities",
    _ when name.StartsWith("prototype_", StringComparison.Ordinal) || name.StartsWith("resource_", StringComparison.Ordinal) => "content",
    _ when name.StartsWith("map", StringComparison.Ordinal) => "maps",
    _ when name.StartsWith("fixture_", StringComparison.Ordinal) => "fixtures",
    _ when name.StartsWith("action", StringComparison.Ordinal) => "actions",
    _ when name.StartsWith("client_", StringComparison.Ordinal) => "client",
    _ when name.StartsWith("ui_", StringComparison.Ordinal) => "ui",
    _ when name.StartsWith("replication_", StringComparison.Ordinal) || name == "state_compare" => "replication",
    _ when name.StartsWith("scenario_", StringComparison.Ordinal) || name == "condition_wait" => "scenarios",
    _ when name.StartsWith("artifact", StringComparison.Ordinal) || name is "report_create" or "snapshot_diff" => "artifacts",
    _ => "diagnostics",
};
