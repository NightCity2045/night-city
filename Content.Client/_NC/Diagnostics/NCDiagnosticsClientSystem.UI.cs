// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#if TOOLS && NC_DIAGNOSTICS
using System.Linq;
using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Map;
using System.Numerics;

namespace Content.Client._NC.Diagnostics;

public sealed partial class NCDiagnosticsClientSystem
{
    private readonly Dictionary<string, Control> _controls = new();

    private JsonObject InspectUi(JsonObject args)
    {
        var maximum = args.BoundedInt("limit", 200, 1, 512);
        var includeHidden = args["includeHidden"]?.GetValue<bool>() ?? false;
        _controls.Clear();
        var snapshot = Guid.NewGuid().ToString("N");
        var queue = new Queue<(Control Control, string Path, int Depth)>();
        queue.Enqueue((_ui.RootControl, "root", 0));
        var nodes = new JsonArray();
        while (queue.Count != 0 && nodes.Count < maximum)
        {
            var (control, path, depth) = queue.Dequeue();
            if (control.Disposed || !includeHidden && !control.VisibleInTree)
                continue;
            var id = snapshot + "-" + nodes.Count;
            _controls[id] = control;
            var node = new JsonObject
            {
                ["id"] = id, ["path"] = path, ["name"] = control.Name, ["type"] = control.GetType().Name,
                ["visible"] = control.VisibleInTree, ["x"] = control.GlobalPixelPosition.X,
                ["y"] = control.GlobalPixelPosition.Y, ["width"] = control.PixelSize.X,
                ["height"] = control.PixelSize.Y, ["childCount"] = control.ChildCount,
                ["interactionBlocked"] = UnsafeControl(control),
            };
            if (control is Label label)
            {
                node["text"] = BoundText(label.Text);
                node["textTruncated"] = label.Text?.Length > 512;
            }
            if (control is Button button)
            {
                node["text"] = BoundText(button.Text);
                node["textTruncated"] = button.Text?.Length > 512;
            }
            if (control is BaseButton baseButton)
                node["disabled"] = baseButton.Disabled;
            if (control is LineEdit edit)
            {
                node["editable"] = edit.Editable;
                node["textRedacted"] = args["includeTextFields"]?.GetValue<bool>() != true || SensitiveTextControl(edit);
                node["textTruncated"] = node["textRedacted"]!.GetValue<bool>() == false && edit.Text.Length > 512;
                node["text"] = args["includeTextFields"]?.GetValue<bool>() == true && !SensitiveTextControl(edit)
                    ? BoundText(edit.Text) : "[redacted]";
            }
            nodes.Add(node);
            if (depth >= 16 || UnsafeControl(control))
                continue;
            for (var index = 0; index < Math.Min(control.ChildCount, 128); index++)
            {
                var child = control.Children[index];
                queue.Enqueue((child, path + "/" + (child.Name ?? child.GetType().Name) + "[" + index + "]", depth + 1));
            }
        }
        return new JsonObject { ["snapshotId"] = snapshot, ["frame"] = _frame, ["controls"] = nodes,
            ["truncated"] = queue.Count != 0, ["handlePolicy"] = "valid-until-next-ui-inspection-or-control-removal" };
    }

    private JsonObject InteractUi(JsonObject args)
    {
        if (!_canInput)
            throw new DiagnosticException("access_denied", "This client only permits observation.");
        var id = args.RequiredString("controlId", 100);
        if (!_controls.TryGetValue(id, out var control) || control.Disposed || !control.VisibleInTree)
            throw new DiagnosticException("stale_control", "Inspect the UI again; the control handle is no longer valid.");
        if (UnsafeControl(control))
            throw new DiagnosticException("access_denied", "Developer consoles and variable editors are outside the playtest UI catalogue.");
        if (control is BaseButton { Disabled: true })
            throw new DiagnosticException("control_disabled", "The selected control is disabled.");
        var action = args.RequiredString("action", 32);
        switch (action)
        {
            case "click":
                if (!_graphics)
                    throw new DiagnosticException("rendering_unavailable", "Pointer UI interaction requires a graphical client.");
                if (control.PixelSize.X <= 0 || control.PixelSize.Y <= 0)
                    throw new DiagnosticException("control_not_laid_out", "Wait for layout before clicking this control.");
                var position = control.GlobalPixelPosition + (Vector2)control.PixelSize / 2;
                var hit = _ui.MouseGetControl(new ScreenCoordinates(position, _clyde.MainWindow.Id));
                var descendant = hit;
                while (descendant != null && descendant != control) descendant = descendant.Parent;
                if (descendant == null || hit == null || UnsafeControl(hit))
                    throw new DiagnosticException("control_obscured", "The selected control is not the pointer hit target. Inspect and dismiss the covering UI first.");
                NCDiagnosticSdlInput.Click(_clyde.MainWindow.Title,
                    position.X, position.Y, failure =>
                    {
                        if (failure != null)
                            Log.Error("diagnostics.input: " + failure.Code + ": " + failure.Message);
                    });
                break;
            case "set_text":
            case "submit":
                if (control is not LineEdit { Editable: true } edit)
                    throw new DiagnosticException("unsupported_control", "This operation requires an editable LineEdit.");
                if (SensitiveTextControl(edit))
                    throw new DiagnosticException("access_denied", "Secret text fields cannot be filled through diagnostic UI actions.");
                if (args["text"]?.GetValue<string>() is { } text)
                {
                    if (text.Length > 2048)
                        throw new DiagnosticException("invalid_arguments", "UI text may not exceed 2048 characters.");
                    edit.SetText(text, invokeEvent: true);
                }
                if (action == "submit")
                    edit.ForceSubmitText();
                break;
            case "focus":
                if (!control.CanKeyboardFocus)
                    throw new DiagnosticException("unsupported_control", "This control cannot receive keyboard focus.");
                _ui.GrabKeyboardFocus(control);
                break;
            default:
                throw new DiagnosticException("unsupported_action", "UI actions are click, set_text, submit or focus.");
        }
        return new JsonObject { ["submitted"] = true, ["action"] = action, ["controlId"] = id,
            ["frame"] = _frame, ["executionPath"] = action == "click" ? "sdl-event-queue" : "public-ui-control-api",
            ["outcomeConfirmed"] = false };
    }

    private static bool UnsafeControl(Control control)
    {
        for (Control? current = control; current != null; current = current.Parent)
        {
            var space = current.GetType().Namespace ?? string.Empty;
            if (space.StartsWith("Robust.Client.Console", StringComparison.Ordinal) ||
                space.StartsWith("Robust.Client.ViewVariables", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool SensitiveTextControl(Control control)
    {
        if (UnsafeControl(control))
            return true;
        for (Control? current = control; current != null; current = current.Parent)
        {
            var description = current.GetType().Name + " " + current.Name;
            if (description.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("authentication", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? BoundText(string? text) => text is { Length: > 512 } ? text[..512] : text;
}

#endif
