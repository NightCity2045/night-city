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
    [McpServerTool(Name = "capabilities_get", ReadOnly = true, Destructive = false), Description("Read the target process capability and coverage matrix. Unsupported adapters are explicit.")]
    public Task<CallToolResult> CapabilitiesGet(string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("capabilities_get", new JsonObject { ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "environment_start", ReadOnly = false, Destructive = false), Description("Start an isolated owned server with zero to three clients. Returns an operation handle; graphics are optional.")]
    public Task<CallToolResult> EnvironmentStart(int clients = 0, bool graphics = false, CancellationToken cancellationToken = default) =>
        Call("environment_start", new JsonObject { ["clients"] = clients, ["graphics"] = graphics }, cancellationToken);

    [McpServerTool(Name = "environment_stop", ReadOnly = false, Destructive = true), Description("Stop only processes owned by this MCP process. Test data and reports are retained.")]
    public Task<CallToolResult> EnvironmentStop(string environmentId, CancellationToken cancellationToken = default) =>
        Call("environment_stop", new JsonObject { ["environmentId"] = environmentId }, cancellationToken);

    [McpServerTool(Name = "environment_logs", ReadOnly = true, Destructive = false), Description("Read startup and process output for an owned environment, including failed startup attempts.")]
    public Task<CallToolResult> EnvironmentLogs(string environmentId, long cursor = 0, int limit = 100, CancellationToken cancellationToken = default) =>
        Call("environment_logs", new JsonObject { ["environmentId"] = environmentId, ["cursor"] = cursor, ["limit"] = limit }, cancellationToken);

    [McpServerTool(Name = "maps_list", ReadOnly = true, Destructive = false), Description("List up to 64 currently loaded maps and their network references.")]
    public Task<CallToolResult> MapsList(string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("maps_list", new JsonObject { ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "entities_query", ReadOnly = true, Destructive = false), Description("Query a bounded map region. Supply near or map plus x/y; radius is at most 16, pages at most 100. Cursors refer to short-lived snapshots.")]
    public Task<CallToolResult> EntitiesQuery(string serverInstanceId, long worldGeneration, string? near = null, string? map = null, float x = 0, float y = 0, float radius = 8, int limit = 50, string? prototype = null, string? component = null, string? cursor = null, string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("entities_query", new JsonObject { ["serverInstanceId"] = serverInstanceId, ["worldGeneration"] = worldGeneration, ["near"] = near, ["map"] = map, ["x"] = x, ["y"] = y, ["radius"] = radius, ["limit"] = limit, ["prototype"] = prototype, ["component"] = component, ["cursor"] = cursor, ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "entity_snapshot", ReadOnly = false, Destructive = false), Description("Capture declared entity fields and retain a JSON artifact for explicit-field comparisons.")]
    public Task<CallToolResult> EntitySnapshot(string netEntity, string serverInstanceId, long worldGeneration, string[]? components = null, string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("entity_snapshot", new JsonObject { ["netEntity"] = netEntity, ["serverInstanceId"] = serverInstanceId, ["worldGeneration"] = worldGeneration, ["components"] = JsonSerializer.SerializeToNode(components), ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "snapshot_diff", ReadOnly = true, Destructive = false), Description("Compare explicit JSON pointers in two retained snapshot artifacts. Missing fields produce inconclusive coverage, never equality.")]
    public Task<CallToolResult> SnapshotDiff(string left, string right, string[] paths, CancellationToken cancellationToken = default) =>
        Call("snapshot_diff", new JsonObject { ["left"] = left, ["right"] = right, ["paths"] = JsonSerializer.SerializeToNode(paths) }, cancellationToken);

    [McpServerTool(Name = "prototype_get", ReadOnly = true, Destructive = false), Description("Read a composed prototype mapping and entity parent list, including abstract parents. Exact per-field source provenance is not inferred.")]
    public Task<CallToolResult> PrototypeGet(string id, string kind = "entity", string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("prototype_get", new JsonObject { ["id"] = id, ["kind"] = kind, ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "prototype_search", ReadOnly = true, Destructive = false), Description("Find entity prototypes by ID prefix; at most 100 results.")]
    public Task<CallToolResult> PrototypeSearch(string prefix = "", int limit = 50, string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("prototype_search", new JsonObject { ["prefix"] = prefix, ["limit"] = limit, ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "resource_inspect", ReadOnly = true, Destructive = false), Description("Inspect a repository resource, RSI metadata, PNG dimensions or bounded YAML/FTL/JSON text. This does not run validation.")]
    public Task<CallToolResult> ResourceInspect(string path, CancellationToken cancellationToken = default) =>
        Call("resource_inspect", new JsonObject { ["path"] = path }, cancellationToken);

    [McpServerTool(Name = "validation_run", ReadOnly = false, Destructive = false), Description("Run a fixed validation profile: build-server, build-client, content, integration, yaml, mcp or rsi. Integration requires explicit process-level opt-in.")]
    public Task<CallToolResult> ValidationRun(string kind, string? operationId = null, CancellationToken cancellationToken = default) =>
        Call("validation_run", new JsonObject { ["kind"] = kind, ["operationId"] = operationId }, cancellationToken);

    [McpServerTool(Name = "map_region_inspect", ReadOnly = true, Destructive = false), Description("Inspect entities in a bounded loaded map region, using the same snapshot semantics as entities_query.")]
    public Task<CallToolResult> MapRegionInspect(string map, string serverInstanceId, long worldGeneration, float x = 0, float y = 0, float radius = 8, int limit = 50, string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("map_region_inspect", new JsonObject { ["map"] = map, ["serverInstanceId"] = serverInstanceId, ["worldGeneration"] = worldGeneration, ["x"] = x, ["y"] = y, ["radius"] = radius, ["limit"] = limit, ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "map_region_capture", ReadOnly = false, Destructive = false), Description("Capture the selected graphical client's current game viewport. The result identifies the observed client view, not an arbitrary unseen map region.")]
    public Task<CallToolResult> MapRegionCapture(string targetId, int maximumWidth = 1280, int maximumHeight = 1080, CancellationToken cancellationToken = default) =>
        Call("map_region_capture", new JsonObject { ["targetId"] = targetId, ["maximumWidth"] = maximumWidth, ["maximumHeight"] = maximumHeight }, cancellationToken);

    [McpServerTool(Name = "actions_list", ReadOnly = true, Destructive = false), Description("List native authoritative interaction actions and actor policy for the selected server.")]
    public Task<CallToolResult> ActionsList(string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("actions_list", new JsonObject { ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "action_execute", ReadOnly = false, Destructive = true), Description("Submit interact, pickup, drop or use_in_hand through native gameplay systems. Supply actor and optional target network IDs, plus the original world identity. Submission is not proof of outcome.")]
    public Task<CallToolResult> ActionExecute(string action, string actor, string serverInstanceId, long worldGeneration, string? target = null, bool alternative = false, string? operationId = null, string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("action_execute", new JsonObject { ["action"] = action, ["actor"] = actor, ["serverInstanceId"] = serverInstanceId, ["worldGeneration"] = worldGeneration, ["target"] = target, ["alternative"] = alternative, ["operationId"] = operationId, ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "fixture_create", ReadOnly = false, Destructive = false), Description("Create an owned test map. Requires an explicitly isolated fixture-admin server.")]
    public Task<CallToolResult> FixtureCreate(string serverInstanceId, long worldGeneration, string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("fixture_create", new JsonObject { ["serverInstanceId"] = serverInstanceId, ["worldGeneration"] = worldGeneration, ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "fixture_spawn", ReadOnly = false, Destructive = false), Description("Spawn an existing prototype into an owned fixture at coordinates between -16 and 16. At most 32 explicitly spawned entities per fixture.")]
    public Task<CallToolResult> FixtureSpawn(string fixtureId, string prototype, string serverInstanceId, long worldGeneration, float x = 0, float y = 0, string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("fixture_spawn", new JsonObject { ["fixtureId"] = fixtureId, ["prototype"] = prototype, ["serverInstanceId"] = serverInstanceId, ["worldGeneration"] = worldGeneration, ["x"] = x, ["y"] = y, ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "fixture_reset", ReadOnly = false, Destructive = true), Description("Replace the owned fixture map and invalidate its old entity references.")]
    public Task<CallToolResult> FixtureReset(string fixtureId, string serverInstanceId, long worldGeneration, string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("fixture_reset", new JsonObject { ["fixtureId"] = fixtureId, ["serverInstanceId"] = serverInstanceId, ["worldGeneration"] = worldGeneration, ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "fixture_destroy", ReadOnly = false, Destructive = true), Description("Delete an owned fixture map and its contents.")]
    public Task<CallToolResult> FixtureDestroy(string fixtureId, string serverInstanceId, long worldGeneration, string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("fixture_destroy", new JsonObject { ["fixtureId"] = fixtureId, ["serverInstanceId"] = serverInstanceId, ["worldGeneration"] = worldGeneration, ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "client_inspect", ReadOnly = true, Destructive = false), Description("Read client identity, local entity, rendering capability, current tick and latest applied server tick.")]
    public Task<CallToolResult> ClientInspect(string targetId, CancellationToken cancellationToken = default) =>
        Call("client_inspect", new JsonObject { ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "client_input", ReadOnly = false, Destructive = true), Description("Submit a curated native input function: move_up/down/left/right, use, activate_in_hand, drop or swap_hands. Held input expires within one second.")]
    public Task<CallToolResult> ClientInput(string function, string serverInstanceId, long worldGeneration, string targetId, string? netEntity = null, string state = "press", int durationMilliseconds = 100, string? operationId = null, CancellationToken cancellationToken = default) =>
        Call("client_input", new JsonObject { ["function"] = function, ["serverInstanceId"] = serverInstanceId, ["worldGeneration"] = worldGeneration, ["targetId"] = targetId, ["netEntity"] = netEntity, ["state"] = state, ["durationMilliseconds"] = durationMilliseconds, ["operationId"] = operationId }, cancellationToken);

    [McpServerTool(Name = "client_capture", ReadOnly = false, Destructive = false), Description("Capture a rendered frame and retain a PNG artifact. Headless output is rejected. Returns an operation handle.")]
    public Task<CallToolResult> ClientCapture(string targetId, int maximumWidth = 1280, int maximumHeight = 1080, string mode = "final", CancellationToken cancellationToken = default) =>
        Call("client_capture", new JsonObject { ["targetId"] = targetId, ["maximumWidth"] = maximumWidth, ["maximumHeight"] = maximumHeight, ["mode"] = mode }, cancellationToken);

    [McpServerTool(Name = "ui_inspect", ReadOnly = true, Destructive = false), Description("Read a bounded UI tree and fresh control handles. includeTextFields also permits guarded META editor hash/caret/composition/history evidence. Native window/framebuffer dimensions and clipboard test status are included. Secret controls are protected.")]
    public Task<CallToolResult> UiInspect(string targetId, int limit = 200, bool includeHidden = false, bool includeTextFields = false, CancellationToken cancellationToken = default) =>
        Call("ui_inspect", new JsonObject { ["targetId"] = targetId, ["limit"] = limit, ["includeHidden"] = includeHidden, ["includeTextFields"] = includeTextFields }, cancellationToken);

    [McpServerTool(Name = "ui_interact", ReadOnly = false, Destructive = true), Description("Use an observed control: click (SDL), set_text, submit, focus. Capability-gated actions accept strict JSON in text: editor_input {op,text?,startRunes?,lengthRunes?,lease?,nonce?}; clipboard {op:seed|status|release,text?,nonce?}; client_size/own_window_size {width,height,restore?}. Editor ops: preedit,commit,select-all,left,right,select-left,select-right,copy,cut,paste,paste_hold,paste_release. Clipboard requires its returned lease nonce; no clipboard read endpoint. Native handler composition does not prove OS IME candidate UI. Outcomes require observation.")]
    public Task<CallToolResult> UiInteract(string controlId, string action, string serverInstanceId, long worldGeneration, string targetId, string? text = null, string? operationId = null, CancellationToken cancellationToken = default) =>
        Call("ui_interact", new JsonObject { ["controlId"] = controlId, ["action"] = action, ["serverInstanceId"] = serverInstanceId, ["worldGeneration"] = worldGeneration, ["targetId"] = targetId, ["text"] = text, ["operationId"] = operationId }, cancellationToken);

    [McpServerTool(Name = "replication_inspect", ReadOnly = true, Destructive = false), Description("Read applied server ticks, current entity projection and any explicitly watched authoritative Door state history. Current predicted state is labeled separately.")]
    public Task<CallToolResult> ReplicationInspect(string targetId, string? netEntity = null, CancellationToken cancellationToken = default) =>
        Call("replication_inspect", new JsonObject { ["targetId"] = targetId, ["netEntity"] = netEntity }, cancellationToken);

    [McpServerTool(Name = "replication_watch", ReadOnly = false, Destructive = false), Description("Watch up to 16 doors for authoritative post-state-application history. Start the watch before the tested interaction.")]
    public Task<CallToolResult> ReplicationWatch(string netEntity, string targetId, int durationSeconds = 30, CancellationToken cancellationToken = default) =>
        Call("replication_watch", new JsonObject { ["netEntity"] = netEntity, ["targetId"] = targetId, ["durationSeconds"] = durationSeconds }, cancellationToken);

    [McpServerTool(Name = "replication_unwatch", ReadOnly = false, Destructive = false), Description("Remove a previously installed replication watch.")]
    public Task<CallToolResult> ReplicationUnwatch(string netEntity, string targetId, CancellationToken cancellationToken = default) =>
        Call("replication_unwatch", new JsonObject { ["netEntity"] = netEntity, ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "logs_read", ReadOnly = true, Destructive = false), Description("Read bounded, credential-redacted process logs with a cursor. Overwritten entries are counted.")]
    public Task<CallToolResult> LogsRead(long cursor = 0, int limit = 100, string? level = null, string? category = null, string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("logs_read", new JsonObject { ["cursor"] = cursor, ["limit"] = limit, ["level"] = level, ["category"] = category, ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "metrics_capture", ReadOnly = true, Destructive = false), Description("Read supported process and diagnostic metrics. Coverage is explicit; this does not imply per-system CPU attribution.")]
    public Task<CallToolResult> MetricsCapture(string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("metrics_capture", new JsonObject { ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "condition_wait", ReadOnly = false, Destructive = false), Description("Wait for a read-only observation to match an explicit JSON pointer/value. Missing fields never satisfy a null expectation. Returns an operation handle.")]
    public Task<CallToolResult> ConditionWait(string command, JsonObject arguments, string path, JsonNode? equals, int timeoutSeconds = 10, int intervalMilliseconds = 200, int minimumStableMilliseconds = 0, double tolerance = 0, string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("condition_wait", new JsonObject { ["command"] = command, ["arguments"] = arguments.DeepClone(), ["path"] = path, ["equals"] = equals?.DeepClone(), ["timeoutSeconds"] = timeoutSeconds, ["intervalMilliseconds"] = intervalMilliseconds, ["minimumStableMilliseconds"] = minimumStableMilliseconds, ["tolerance"] = tolerance, ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "request_status", ReadOnly = true, Destructive = false), Description("Retrieve a retained server/client mutation result by its original request ID. Use this to reconcile lost responses before retrying.")]
    public Task<CallToolResult> RequestStatus(string requestId, string targetId = "server", CancellationToken cancellationToken = default) =>
        Call("request_status", new JsonObject { ["requestId"] = requestId, ["targetId"] = targetId }, cancellationToken);

    [McpServerTool(Name = "scenario_validate", ReadOnly = true, Destructive = false), Description("Validate a declarative scenario without launching a game. Supply an inline scenario object or a path under the bundled Scenarios directory.")]
    public Task<CallToolResult> ScenarioValidate(JsonObject? scenario = null, string? path = null, CancellationToken cancellationToken = default) =>
        Call("scenario_validate", new JsonObject { ["scenario"] = scenario?.DeepClone(), ["path"] = path }, cancellationToken);

    [McpServerTool(Name = "scenario_run", ReadOnly = false, Destructive = true), Description("Run a validated bounded scenario, collect observations, clean up its fixtures/environment and write JSON/HTML reports. Returns an operation handle.")]
    public Task<CallToolResult> ScenarioRun(JsonObject? scenario = null, string? path = null, string? operationId = null, CancellationToken cancellationToken = default) =>
        Call("scenario_run", new JsonObject { ["scenario"] = scenario?.DeepClone(), ["path"] = path, ["operationId"] = operationId }, cancellationToken);

    [McpServerTool(Name = "scenario_status", ReadOnly = true, Destructive = false), Description("Read scenario operation status and report artifacts.")]
    public Task<CallToolResult> ScenarioStatus(string operationId, CancellationToken cancellationToken = default) =>
        Call("scenario_status", new JsonObject { ["operationId"] = operationId }, cancellationToken);

    [McpServerTool(Name = "scenario_cancel", ReadOnly = false, Destructive = false), Description("Cancel remaining scenario steps while preserving cleanup and reports.")]
    public Task<CallToolResult> ScenarioCancel(string operationId, CancellationToken cancellationToken = default) =>
        Call("scenario_cancel", new JsonObject { ["operationId"] = operationId }, cancellationToken);

    [McpServerTool(Name = "artifacts_list", ReadOnly = true, Destructive = false), Description("List the most recent retained diagnostic artifacts without reading arbitrary workspace files.")]
    public Task<CallToolResult> ArtifactsList(CancellationToken cancellationToken = default) =>
        Call("artifacts_list", new JsonObject {  }, cancellationToken);

    [McpServerTool(Name = "artifact_get", ReadOnly = true, Destructive = false), Description("Read a retained JSON artifact by its opaque ID.")]
    public Task<CallToolResult> ArtifactGet(string id, string jsonPointer = "", int offset = 0, int limit = 100, CancellationToken cancellationToken = default) =>
        Call("artifact_get", new JsonObject { ["id"] = id, ["jsonPointer"] = jsonPointer, ["offset"] = offset, ["limit"] = limit }, cancellationToken);

    [McpServerTool(Name = "report_create", ReadOnly = false, Destructive = false), Description("Render an escaped static HTML report from a retained JSON artifact.")]
    public Task<CallToolResult> ReportCreate(string id, CancellationToken cancellationToken = default) =>
        Call("report_create", new JsonObject { ["id"] = id }, cancellationToken);
}

