// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Content.Client._NC.Meta.UI;
using NightCity.Diagnostics.Contracts;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Diagnostics;

public sealed partial class NCDiagnosticsClientSystem
{
    private NCMetaApplicationView[] MetaAppWindows()
    {
        var result = new List<NCMetaApplicationView>();
        var pending = new Queue<Control>(); pending.Enqueue(_ui.RootControl);
        for (var work = 0; work < 8192 && pending.TryDequeue(out var control); work++)
        {
            if (control.Disposed) continue;
            if (control is NCMetaApplicationView window && window.SessionId != Guid.Empty && result.Count < 4) result.Add(window);
            foreach (var child in control.Children) if (pending.Count < 8192) pending.Enqueue(child);
        }
        return result.ToArray();
    }
    private JsonObject InspectMetaApps(JsonObject args)
    {
        var windows = MetaAppWindows();
        if (args["sessionId"] is null) return new JsonObject
        { ["sessions"] = JsonSerializer.SerializeToNode(windows.Select(window => window.GetDiagnosticSnapshot()), DiagnosticProtocol.JsonOptions), ["coverage"] = "actual-native-client-bui-reducer" };
        var window = SelectMetaApp(args, windows);
        var offset = args.BoundedInt("offset", 0, 0, 2048); var count = args.BoundedInt("count", 512, 1, 512);
        return new JsonObject { ["session"] = JsonSerializer.SerializeToNode(window.GetDiagnosticSnapshot(), DiagnosticProtocol.JsonOptions),
            ["samples"] = JsonSerializer.SerializeToNode(window.ReadDiagnosticSamples(offset, count), DiagnosticProtocol.JsonOptions) };
    }
    private static NCMetaApplicationView SelectMetaApp(JsonObject args, NCMetaApplicationView[] windows)
    {
        if (!Guid.TryParse(args.RequiredString("sessionId", 36), out var id) || windows.FirstOrDefault(window => window.SessionId == id) is not { } selected)
            throw new DiagnosticException("app_session_not_found", "Select an actual open native app session in this client.");
        return selected;
    }
    private JsonObject MutateMetaApp(string command, JsonObject args)
    {
        if (!_canInput || Environment.GetEnvironmentVariable("NC_DIAGNOSTICS_ISOLATED") != "1")
            throw new DiagnosticException("access_denied", "App load input requires an explicitly isolated playtest client.");
        var window = SelectMetaApp(args, MetaAppWindows());
        if (command == "meta_app_frontend_capture")
        {
            if (!window.TryStartDiagnosticCapture()) throw new DiagnosticException("busy", "The native app capture is already running.");
        }
        else if (command == "meta_app_frontend_stop") window.StopDiagnosticCapture();
        else if (command == "meta_app_frontend_action")
        {
            var action = args.RequiredString("action", 64); var json = args.RequiredString("json", 4096);
            if (!window.TryRequestDiagnosticAction(action, json, out var request))
                throw new DiagnosticException("action_rejected", "The declared typed app action was refused or remains pending.");
            return new JsonObject { ["requestId"] = request.ToString("D"), ["submitted"] = true, ["outcomeConfirmed"] = false,
                ["executionPath"] = "validated-renderer-action-to-native-bui" };
        }
        return new JsonObject { ["session"] = JsonSerializer.SerializeToNode(window.GetDiagnosticSnapshot(), DiagnosticProtocol.JsonOptions) };
    }
}
#endif
