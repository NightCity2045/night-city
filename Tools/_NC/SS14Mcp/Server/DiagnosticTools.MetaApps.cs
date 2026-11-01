// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public sealed partial class DiagnosticTools
{
    [McpServerTool(Name = "meta_app_load_prepare", ReadOnly = false, Destructive = false), Description("Prepare one fixed source app through actual ownership, worker, release, install and service consent on an isolated fixture host. Requires a real connected owner; no source is accepted.")]
    public Task<CallToolResult> MetaAppLoadPrepare(JsonObject arguments, CancellationToken cancellationToken = default) => Call("meta_app_load_prepare", arguments, cancellationToken);
    [McpServerTool(Name = "meta_app_load_status", ReadOnly = true), Description("Read bounded native app fixture admission and actual connected-actor counts.")]
    public Task<CallToolResult> MetaAppLoadStatus(JsonObject arguments, CancellationToken cancellationToken = default) => Call("meta_app_load_status", arguments, cancellationToken);
    [McpServerTool(Name = "meta_app_load_open", ReadOnly = false, Destructive = false), Description("Open the fixed fixture app through production AppSystem and native BUI for one actual connected InGame actor. Normal app quotas and physical route apply.")]
    public Task<CallToolResult> MetaAppLoadOpen(JsonObject arguments, CancellationToken cancellationToken = default) => Call("meta_app_load_open", arguments, cancellationToken);
    [McpServerTool(Name = "meta_app_load_attach", ReadOnly = false, Destructive = false), Description("Attach an actual connected user via native mind/ticker to one fixed MobHuman on the admitted app fixture grid. Fixed placement and bounded50 users/64 fixture entities; no fabricated sessions.")]
    public Task<CallToolResult> MetaAppLoadAttach(JsonObject arguments, CancellationToken cancellationToken = default) => Call("meta_app_load_attach", arguments, cancellationToken);
    [McpServerTool(Name = "meta_app_load_stop", ReadOnly = false, Destructive = false), Description("Stop only the exact runtime installation owned by the isolated app load fixture.")]
    public Task<CallToolResult> MetaAppLoadStop(JsonObject arguments, CancellationToken cancellationToken = default) => Call("meta_app_load_stop", arguments, cancellationToken);
    [McpServerTool(Name = "meta_app_frontend_list", ReadOnly = true), Description("Read up to four actual app BUI renderer sessions in the selected native client. No private view state is returned.")]
    public Task<CallToolResult> MetaAppFrontendList(JsonObject arguments, CancellationToken cancellationToken = default) => Call("meta_app_frontend_list", arguments, cancellationToken);
    [McpServerTool(Name = "meta_app_frontend_read", ReadOnly = true), Description("Read bounded native reducer/action/ACK observations from an actual app frontend, including raw monotonic timestamps and explicit overflow.")]
    public Task<CallToolResult> MetaAppFrontendRead(JsonObject arguments, CancellationToken cancellationToken = default) => Call("meta_app_frontend_read", arguments, cancellationToken);
    [McpServerTool(Name = "meta_app_frontend_capture", ReadOnly = false, Destructive = false), Description("Start bounded recording on an actual open app BUI. Explicitly isolated playtest client only.")]
    public Task<CallToolResult> MetaAppFrontendCapture(JsonObject arguments, CancellationToken cancellationToken = default) => Call("meta_app_frontend_capture", arguments, cancellationToken);
    [McpServerTool(Name = "meta_app_frontend_action", ReadOnly = false, Destructive = false), Description("Submit only a declared typed app action through the real renderer-to-BUI path; one pending request, normal authority and rate limits. No arbitrary UI or console input.")]
    public Task<CallToolResult> MetaAppFrontendAction(JsonObject arguments, CancellationToken cancellationToken = default) => Call("meta_app_frontend_action", arguments, cancellationToken);
    [McpServerTool(Name = "meta_app_frontend_stop", ReadOnly = false, Destructive = false), Description("Stop bounded app frontend recording without inventing an action result.")]
    public Task<CallToolResult> MetaAppFrontendStop(JsonObject arguments, CancellationToken cancellationToken = default) => Call("meta_app_frontend_stop", arguments, cancellationToken);
}
