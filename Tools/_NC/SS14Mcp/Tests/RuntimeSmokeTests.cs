// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;
using NightCity.Diagnostics.Core;
using NUnit.Framework;

namespace NightCity.Diagnostics.Tests;

[TestFixture, NonParallelizable]
public sealed class RuntimeSmokeTests
{
    [Test, Category("Runtime")]
    public async Task OwnedServerAndClientsExerciseNativeBridges()
    {
        if (Environment.GetEnvironmentVariable("SS14_MCP_RUNTIME_TESTS") != "1")
            Assert.Ignore("Set SS14_MCP_RUNTIME_TESTS=1 and SS14_MCP_WORKSPACE to run owned game processes.");
        var workspace = Environment.GetEnvironmentVariable("SS14_MCP_WORKSPACE") ?? Directory.GetCurrentDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var app = new DiagnosticApplication(workspace);
        var clients = int.TryParse(Environment.GetEnvironmentVariable("SS14_MCP_TEST_CLIENTS"), out var count) ? count : 2;
        var graphics = Environment.GetEnvironmentVariable("SS14_MCP_TEST_GRAPHICS") == "1";
        try
        {
            var started = await Complete(app, "environment_start", new JsonObject { ["clients"] = clients, ["graphics"] = graphics }, deadline.Token);
            var targets = started!["targets"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
            var serverId = targets[0];
            var session = await Complete(app, "session_get", new JsonObject { ["targetId"] = serverId }, deadline.Token);
            var scope = new JsonObject { ["targetId"] = serverId, ["serverInstanceId"] = session!["serverInstanceId"]!.DeepClone(),
                ["worldGeneration"] = session["worldGeneration"]!.DeepClone() };
            async Task<JsonNode?> Server(string command, JsonObject? extra = null)
            {
                var args = (JsonObject)scope.DeepClone();
                if (extra != null) foreach (var (key, value) in extra) args[key] = value?.DeepClone();
                return await Complete(app, command, args, deadline.Token);
            }
            var fixture = await Server("fixture_create");
            var fixtureId = fixture!["data"]!["fixtureId"]!.GetValue<string>();
            var actor = await Server("fixture_spawn", new JsonObject { ["fixtureId"] = fixtureId, ["prototype"] = "MobHuman", ["x"] = 0, ["y"] = 0 });
            var actorId = actor!["data"]!["netEntity"]!.GetValue<string>();
            var item = await Server("fixture_spawn", new JsonObject { ["fixtureId"] = fixtureId, ["prototype"] = "Crowbar", ["x"] = 0.5, ["y"] = 0 });
            var itemId = item!["data"]!["netEntity"]!.GetValue<string>();
            var pickup = await Server("action_execute", new JsonObject { ["actor"] = actorId, ["target"] = itemId, ["action"] = "pickup" });
            Assert.That(pickup!["data"]!["handled"]!.GetValue<bool>(), Is.True);
            var inspected = await Server("entity_inspect", new JsonObject { ["netEntity"] = actorId, ["components"] = new JsonArray("Hands") });
            Assert.That(inspected!["data"]!["readers"]!["hands"]!["held"]!.AsObject().Any(pair => pair.Value?.GetValue<string>() == itemId), Is.True);
            await Server("action_execute", new JsonObject { ["actor"] = actorId, ["action"] = "drop" });
            for (var index = 1; index < targets.Length; index++)
            {
                var clientSession = await Complete(app, "session_get", new JsonObject { ["targetId"] = targets[index] }, deadline.Token);
                var clientScope = new JsonObject { ["targetId"] = targets[index], ["serverInstanceId"] = clientSession!["serverInstanceId"]!.DeepClone(),
                    ["worldGeneration"] = clientSession["worldGeneration"]!.DeepClone() };
                var bodyId = actorId;
                if (index > 1)
                    bodyId = (await Server("fixture_spawn", new JsonObject { ["fixtureId"] = fixtureId, ["prototype"] = "MobHuman", ["x"] = 2, ["y"] = 0 }))!["data"]!["netEntity"]!.GetValue<string>();
                var userId = clientSession["data"]!["userId"]?.GetValue<string>();
                if (userId == null) throw new DiagnosticException("client_not_connected", "The client bridge has no connected user identifier.");
                await Server("fixture_attach", new JsonObject { ["fixtureId"] = fixtureId, ["actor"] = bodyId, ["userId"] = userId });
                var wait = new JsonObject { ["targetId"] = targets[index], ["command"] = "client_inspect", ["arguments"] = new JsonObject(),
                    ["path"] = "/data/localEntity", ["equals"] = bodyId, ["timeoutSeconds"] = 30 };
                await Complete(app, "condition_wait", wait, deadline.Token);
                var input = (JsonObject)clientScope.DeepClone();
                var initialPosition = await Server("entity_inspect", new JsonObject { ["netEntity"] = bodyId, ["components"] = new JsonArray("Transform") });
                var initialX = initialPosition!["data"]!["transform"]!["x"]!.GetValue<double>();
                input["function"] = "move_right"; input["durationMilliseconds"] = 300;
                await Complete(app, "client_input", input, deadline.Token);
                var moved = false;
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    await Task.Delay(100, deadline.Token);
                    var observed = await Server("entity_inspect", new JsonObject { ["netEntity"] = bodyId, ["components"] = new JsonArray("Transform") });
                    if (Math.Abs(observed!["data"]!["transform"]!["x"]!.GetValue<double>() - initialX) > 0.01) { moved = true; break; }
                }
                Assert.That(moved, Is.True, "Native client input must change the authoritative actor position.");
                var ui = await Complete(app, "ui_inspect", clientScope, deadline.Token);
                Assert.That(ui!["data"], Is.Not.Null);
                if (graphics)
                {
                    var tree = await Complete(app, "ui_inspect", new JsonObject { ["targetId"] = targets[index], ["limit"] = 512, ["includeHidden"] = true }, deadline.Token);
                    await app.Artifacts.WriteJsonAsync("ui-before-click", tree!, deadline.Token);
                    await Complete(app, "client_capture", new JsonObject { ["targetId"] = targets[index] }, deadline.Token);
                    var controls = tree!["data"]!["controls"]!.AsArray();
                    var toggle = controls.FirstOrDefault(control => control?["name"]?.GetValue<string>() == "InventoryButton" && control["visible"]?.GetValue<bool>() == true)
                        ?? throw new DiagnosticException("ui_control_not_found", "The controlled human's inventory toggle is absent from the rendered UI.");
                    var previous = controls.FirstOrDefault(control => control?["name"]?.GetValue<string>() == "InventoryHotbar")?["visible"]?.GetValue<bool>() ?? false;
                    var click = (JsonObject)clientScope.DeepClone(); click["action"] = "click"; click["controlId"] = toggle["id"]!.DeepClone();
                    await Complete(app, "ui_interact", click, deadline.Token);
                    var clicked = false;
                    for (var attempt = 0; attempt < 30; attempt++)
                    {
                        await Task.Delay(100, deadline.Token);
                        var observed = await Complete(app, "ui_inspect", new JsonObject { ["targetId"] = targets[index], ["limit"] = 512, ["includeHidden"] = true }, deadline.Token);
                        var visible = observed!["data"]!["controls"]!.AsArray().FirstOrDefault(control => control?["name"]?.GetValue<string>() == "InventoryHotbar")?["visible"]?.GetValue<bool>() ?? false;
                        if (visible != previous) { clicked = true; break; }
                    }
                    if (!clicked)
                    {
                        var logs = await Complete(app, "logs_read", new JsonObject { ["targetId"] = targets[index], ["limit"] = 200 }, deadline.Token);
                        await app.Artifacts.WriteJsonAsync("ui-click-failure", logs!, deadline.Token);
                    }
                    Assert.That(clicked, Is.True, "Native SDL UI click must change the inventory hotbar visibility.");
                    var capture = await Complete(app, "client_capture", new JsonObject { ["targetId"] = targets[index] }, deadline.Token);
                    TestContext.Progress.WriteLine("Capture: " + capture!["data"]!["artifact"]!["path"]);
                    var viewport = await Complete(app, "map_region_capture", new JsonObject { ["targetId"] = targets[index] }, deadline.Token);
                    TestContext.Progress.WriteLine("Viewport: " + viewport!["data"]!["artifact"]!["path"]);
                }
            }
            if (targets.Length > 1)
            {
                var door = await Server("fixture_spawn", new JsonObject { ["fixtureId"] = fixtureId, ["prototype"] = "Airlock", ["x"] = 1, ["y"] = 1 });
                var doorId = door!["data"]!["netEntity"]!.GetValue<string>();
                await Server("fixture_power", new JsonObject { ["fixtureId"] = fixtureId, ["netEntity"] = doorId, ["needsPower"] = false });
                foreach (var targetId in targets.Skip(1))
                {
                    await Complete(app, "condition_wait", new JsonObject { ["targetId"] = targetId, ["command"] = "entity_inspect",
                        ["arguments"] = new JsonObject { ["netEntity"] = doorId, ["components"] = new JsonArray("Door") },
                        ["path"] = "/data/prototype", ["equals"] = "Airlock", ["timeoutSeconds"] = 30 }, deadline.Token);
                    await Complete(app, "replication_watch", new JsonObject { ["targetId"] = targetId, ["netEntity"] = doorId }, deadline.Token);
                }
                var waitArgs = (JsonObject)scope.DeepClone(); waitArgs["netEntity"] = doorId; waitArgs["components"] = new JsonArray("Door");
                await Complete(app, "condition_wait", new JsonObject { ["targetId"] = serverId, ["command"] = "entity_inspect", ["arguments"] = waitArgs,
                    ["path"] = "/data/components/Door/fields/State", ["equals"] = "Closed", ["minimumStableMilliseconds"] = 500 }, deadline.Token);
                var compareArgs = (JsonObject)scope.DeepClone(); compareArgs["netEntity"] = doorId;
                compareArgs["clientIds"] = new JsonArray(targets.Skip(1).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                compareArgs["paths"] = new JsonArray("/components/Door/fields/State");
                var comparison = await Complete(app, "state_compare", compareArgs, deadline.Token);
                Assert.That(comparison!["comparison"]!["conclusive"]!.GetValue<bool>(), Is.True);
                Assert.That(comparison["comparison"]!["matched"]!.GetValue<bool>(), Is.True);
                await Server("fixture_access", new JsonObject { ["fixtureId"] = fixtureId, ["netEntity"] = doorId, ["kind"] = "reader", ["tags"] = new JsonArray("Captain") });
                await Server("action_execute", new JsonObject { ["actor"] = actorId, ["target"] = doorId, ["action"] = "interact" });
                await Complete(app, "condition_wait", new JsonObject { ["targetId"] = serverId, ["command"] = "entity_inspect", ["arguments"] = waitArgs.DeepClone(),
                    ["path"] = "/data/components/Door/fields/State", ["equals"] = "Closed", ["minimumStableMilliseconds"] = 700 }, deadline.Token);
            }
            await Server("fixture_destroy", new JsonObject { ["fixtureId"] = fixtureId });
            await Complete(app, "environment_stop", new JsonObject { ["environmentId"] = started["id"]!.DeepClone() }, deadline.Token);
        }
        finally
        {
            foreach (var environment in app.Runtime.List())
            {
                var cursor = app.Runtime.Logs(environment.Id, long.MaxValue, 1).NextCursor;
                var log = app.Runtime.Logs(environment.Id, Math.Max(0, cursor - 200), 200);
                var evidence = await app.Artifacts.WriteJsonAsync("runtime-smoke", new { Environment = environment, Log = log }, CancellationToken.None);
                TestContext.Progress.WriteLine("Runtime evidence: " + evidence.Path);
                foreach (var entry in log.Entries.TakeLast(20)) TestContext.Progress.WriteLine(entry.Message);
            }
        }
    }

    internal static async Task<JsonNode?> Complete(DiagnosticApplication app, string command, JsonObject args, CancellationToken cancellationToken)
    {
        var result = await app.ExecuteAsync(command, args, cancellationToken);
        if (result?["name"] == null || result["id"]?.GetValue<string>() is not { } id) return result;
        while (true)
        {
            var operation = app.Operations.Get(id);
            if (operation.FinishedAt != null)
            {
                if (operation.State != "succeeded") throw new DiagnosticException(operation.Error?.Code ?? "failed", operation.Error?.Message ?? operation.State);
                return operation.Result;
            }
            await Task.Delay(100, cancellationToken);
        }
    }
}
