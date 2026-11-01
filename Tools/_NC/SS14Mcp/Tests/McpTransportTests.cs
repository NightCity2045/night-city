// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NUnit.Framework;

namespace NightCity.Diagnostics.Tests;

[TestFixture]
public sealed class McpTransportTests
{
    [TestCase("all", true)]
    [TestCase("entities", false)]
    public async Task RealStdioServerDiscoversToolsAndReturnsStructuredErrorsWithoutStdoutNoise(string modules, bool fixtureTools)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ss14-test", Command = "dotnet", Arguments = [typeof(DiagnosticTools).Assembly.Location],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["SS14_MCP_WORKSPACE"] = TestContext.CurrentContext.WorkDirectory,
                ["SS14_MCP_SERVER"] = "", ["SS14_MCP_SERVER_TOKEN"] = "",
                ["SS14_MCP_CLIENT_PIPE"] = "", ["SS14_MCP_CLIENT_TOKEN"] = "",
                ["SS14_MCP_MODULES"] = modules,
            },
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.That(tools.Select(x => x.Name), Does.Contain("environment_list").And.Contain("entity_inspect"));
        Assert.That(tools.Any(tool => tool.Name == "fixture_create"), Is.EqualTo(fixtureTools));
        var list = await client.CallToolAsync("environment_list", new Dictionary<string, object?>(), cancellationToken: timeout.Token);
        var data = JsonNode.Parse(list.Content.OfType<TextContentBlock>().Single().Text)!;
        Assert.That(data["targets"]!.AsArray(), Is.Empty);
        var missing = await client.CallToolAsync("session_get", new Dictionary<string, object?> { ["targetId"] = "missing" }, cancellationToken: timeout.Token);
        Assert.That(missing.IsError, Is.True);
        Assert.That(missing.Content.OfType<TextContentBlock>().Single().Text, Does.Contain("target_not_found"));
    }
}
