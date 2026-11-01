// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#if TOOLS && NC_DIAGNOSTICS
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Content.Client._NC.Meta.Editor;
using NightCity.Diagnostics.Contracts;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Map;
using Robust.Shared.Utility;
using System.Numerics;

namespace Content.Client._NC.Diagnostics;

public sealed partial class NCDiagnosticsClientSystem
{
    private readonly Dictionary<string, Control> _controls = new();
    private readonly Dictionary<string, HashSet<string>> _observedRows = new();
    private readonly Dictionary<string, HashSet<string>> _observedTabs = new();

    private JsonObject InspectUi(JsonObject args)
    {
        var maximum = args.BoundedInt("limit", 200, 1, 512);
        var includeHidden = args["includeHidden"]?.GetValue<bool>() ?? false;
        _controls.Clear();
        _observedRows.Clear();
        _observedTabs.Clear();
        var rowBudget = 256;
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
            if (control is NCMetaCodeEditor editor)
            {
                var redacted = args["includeTextFields"]?.GetValue<bool>() != true || SensitiveTextControl(editor);
                node["editable"] = editor.Editable;
                node["textRedacted"] = redacted;
                node["textTruncated"] = !redacted && editor.Source.Length > 512;
                node["text"] = redacted ? "[redacted]" : BoundText(editor.Source);
                if (!redacted && editor.VisibleInTree)
                    node["editorState"] = JsonSerializer.SerializeToNode(editor.GetDiagnosticEditorState(), DiagnosticProtocol.JsonOptions);
            }
            if (control is TextEdit multiline)
            {
                var redacted = args["includeTextFields"]?.GetValue<bool>() != true || SensitiveTextControl(multiline);
                node["editable"] = multiline.Editable;
                node["textRedacted"] = redacted;
                node["textTruncated"] = !redacted && multiline.TextLength > 512;
                node["text"] = redacted ? "[redacted]" : Rope.CollapseSubstring(multiline.TextRope, 0..Math.Min(512, multiline.TextLength));
            }
            if (control is TabContainer { TabsVisible: true, ChildCount: <= 64 } tabs && !SensitiveTextControl(tabs))
            {
                var titles = new JsonArray();
                var observed = new HashSet<string>(StringComparer.Ordinal);
                for (var tab = 0; tab < tabs.ChildCount; tab++)
                {
                    if (!tabs.GetTabVisible(tab)) continue;
                    var title = tabs.GetActualTabTitle(tab);
                    if (title.Length > 512) continue;
                    observed.Add(title);
                    titles.Add(new JsonObject { ["title"] = title, ["selected"] = tabs.CurrentTab == tab });
                }
                node["tabs"] = titles;
                _observedTabs[id] = observed;
            }
            if (control is ItemList list && !SensitiveTextControl(list))
            {
                var rows = new JsonArray();
                var observed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in list.Take(512))
                {
                    if (!VisibleRow(list, item)) continue;
                    if (rows.Count >= 64 || rowBudget <= 0) break;
                    rowBudget--;
                    rows.Add(new JsonObject { ["text"] = BoundText(item.Text), ["textTruncated"] = item.Text?.Length > 512,
                        ["selected"] = item.Selected, ["selectable"] = item.Selectable, ["disabled"] = item.Disabled });
                    if (item.Text is { Length: <= 512 } rowLabel) observed.Add(rowLabel);
                }
                _observedRows[id] = observed;
                node["rows"] = rows;
                node["rowClickSupported"] = list.Count <= 512;
            }
            nodes.Add(node);
            if (depth >= 32 || UnsafeControl(control))
                continue;
            for (var index = 0; index < Math.Min(control.ChildCount, 128); index++)
            {
                var child = control.Children[index];
                queue.Enqueue((child, path + "/" + (child.Name ?? child.GetType().Name) + "[" + index + "]", depth + 1));
            }
        }
        return new JsonObject { ["snapshotId"] = snapshot, ["frame"] = _frame, ["controls"] = nodes,
            ["windowWidth"] = _graphics ? _clyde.MainWindow.Size.X : (int?) null,
            ["windowHeight"] = _graphics ? _clyde.MainWindow.Size.Y : (int?) null,
            ["framebufferWidth"] = _graphics ? _clyde.MainWindow.RenderTarget.Size.X : (int?) null,
            ["framebufferHeight"] = _graphics ? _clyde.MainWindow.RenderTarget.Size.Y : (int?) null,
            ["uiScale"] = _ui.RootControl.UIScale, ["clipboardTest"] = ClipboardObservation(),
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
            case "editor_input": return InteractEditor(control, args);
            case "scroll": return InteractScroll(control, args);
            case "select_tab":
            {
                RequireGuiInput(control);
                var title = args.RequiredString("text", 512);
                if (control is not TabContainer { TabsVisible: true, ChildCount: <= 64 } tabs ||
                    !_observedTabs.TryGetValue(id, out var observedTabs) || !observedTabs.Contains(title))
                    throw new DiagnosticException("unsupported_control", "Select a visible tab title from the current UI inspection.");
                var tabMatches = Enumerable.Range(0, tabs.ChildCount).Where(index => tabs.GetTabVisible(index) && tabs.GetActualTabTitle(index) == title).Take(2).ToArray();
                if (tabMatches.Length != 1)
                    throw new DiagnosticException("ambiguous_item", "The observed tab title is missing or ambiguous; inspect the UI again.");
                tabs.CurrentTab = tabMatches[0];
                break;
            }
            case "clipboard": return InteractClipboard(control, args);
            case "client_size": return ResizeDiagnosticWindow(control, args, true);
            case "own_window_size": return ResizeDiagnosticWindow(control, args, false);
            case "click":
            case "hover":
                if (!_graphics)
                    throw new DiagnosticException("rendering_unavailable", "Pointer UI interaction requires a graphical client.");
                if (control.PixelSize.X <= 0 || control.PixelSize.Y <= 0)
                    throw new DiagnosticException("control_not_laid_out", "Wait for layout before clicking this control.");
                var position = control.GlobalPixelPosition + (Vector2)control.PixelSize / 2;
                if (args["text"] is JsonValue rowNode && rowNode.TryGetValue<string>(out var requestedRow))
                {
                    if (control is not ItemList list || list.Count > 512 || requestedRow.Length > 512 || SensitiveTextControl(list) ||
                        !_observedRows.TryGetValue(id, out var observed) || !observed.Contains(requestedRow))
                        throw new DiagnosticException("unsupported_control", "Named row clicks require an observed bounded visible ItemList row.");
                    var matches = list.Where(candidate => candidate.Text == requestedRow).Take(2).ToArray();
                    if (matches.Length != 1)
                        throw new DiagnosticException("ambiguous_item", "The row label is missing or ambiguous; inspect the UI again.");
                    var item = matches[0];
                    if (!item.Selectable || item.Disabled || !VisibleRow(list, item) || item.Region is not { } region)
                        throw new DiagnosticException("control_not_laid_out", "The requested row must be enabled, selectable and fully visible.");
                    position = control.GlobalPixelPosition + (region.TopLeft + region.BottomRight) / 2;
                }
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
                    }, press: action == "click");
                break;
            case "set_text":
            case "submit":
                if (control is TextEdit multiline)
                {
                    if (action != "set_text" || !multiline.Editable)
                        throw new DiagnosticException("unsupported_control", "This operation requires an editable text control; multiline editors support set_text.");
                    if (SensitiveTextControl(multiline))
                        throw new DiagnosticException("access_denied", "Secret text fields cannot be filled through diagnostic UI actions.");
                    if (args["text"] is not JsonValue value || !value.TryGetValue<string>(out var replacement) || replacement.Length > 2048)
                        throw new DiagnosticException("invalid_arguments", "UI text must be a string of at most 2048 characters.");
                    multiline.CursorPosition = new(0, TextEdit.LineBreakBias.Top);
                    multiline.SelectionStart = new(multiline.TextLength, TextEdit.LineBreakBias.Top);
                    multiline.InsertAtCursor(replacement);
                    break;
                }
                if (control is NCMetaCodeEditor editor)
                {
                    if (action != "set_text" || !editor.Editable)
                        throw new DiagnosticException("unsupported_control", "This operation requires an editable text control; source editors support set_text.");
                    if (SensitiveTextControl(editor))
                        throw new DiagnosticException("access_denied", "Secret text fields cannot be filled through diagnostic UI actions.");
                    if (args["text"] is not JsonValue sourceNode || !sourceNode.TryGetValue<string>(out var source) || source.Length > 2048)
                        throw new DiagnosticException("invalid_arguments", "UI text must be a string of at most 2048 characters.");
                    if (!editor.TryReplaceDocument(source))
                        throw new DiagnosticException("invalid_arguments", "The source editor rejected the document.");
                    break;
                }
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
                throw new DiagnosticException("unsupported_action", "This UI action is not in the declared diagnostic catalogue.");
        }
        return new JsonObject { ["submitted"] = true, ["action"] = action, ["controlId"] = id,
            ["frame"] = _frame, ["executionPath"] = action is "click" or "hover" ? "sdl-event-queue" : "public-ui-control-api",
            ["outcomeConfirmed"] = false };
    }

    private static bool VisibleRow(ItemList list, ItemList.Item item) => item.Region is { } region && region.Width > 0 && region.Height > 0 &&
        region.Left >= 0 && region.Top >= 0 && region.Right <= list.PixelSize.X && region.Bottom <= list.PixelSize.Y;

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
