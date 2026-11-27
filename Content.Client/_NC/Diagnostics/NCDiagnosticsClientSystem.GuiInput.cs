// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NightCity.Diagnostics.Contracts;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._NC.Diagnostics;

public sealed partial class NCDiagnosticsClientSystem
{
    private const int GuiInputBurst = 12;
    private const int GuiInputPerSecond = 4;
    private sealed record ScrollInput(int X = 0, int Y = 0);
    private static readonly JsonSerializerOptions GuiJson = new(JsonSerializerDefaults.Web)
    { MaxDepth = 4, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly UTF8Encoding GuiUtf8 = new(false, true);
    private double _guiInputTokens = GuiInputBurst;
    private TimeSpan _guiInputRefilled;

    private static bool ValidGuiText(string? text, int characters, int bytes)
    {
        if (text == null || text.Length > characters) return false;
        try { return GuiUtf8.GetByteCount(text) <= bytes; }
        catch (EncoderFallbackException) { return false; }
    }
    private T ParseGuiInput<T>(JsonObject arguments) where T : class
    {
        var text = arguments.RequiredString("text", 4096);
        if (!ValidGuiText(text, 4096, 16384)) throw new DiagnosticException("invalid_arguments", "Diagnostic input payload exceeds its byte limit or contains invalid text.");
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name)) throw new JsonException();
            return document.RootElement.Deserialize<T>(GuiJson) ?? throw new JsonException();
        }
        catch (JsonException) { throw new DiagnosticException("invalid_arguments", "Expected a bounded diagnostic input object with declared unique properties."); }
    }
    private bool TryConsumeGuiInput()
    {
        var now = _timing.RealTime;
        _guiInputTokens = Math.Min(GuiInputBurst, _guiInputTokens + (now - _guiInputRefilled).TotalSeconds * GuiInputPerSecond);
        _guiInputRefilled = now;
        if (_guiInputTokens < 1) return false;
        _guiInputTokens--;
        return true;
    }
    private void RequireGuiInput(Control control)
    {
        if (!_graphics || !_canInput || SensitiveTextControl(control) || control.Root?.Window != _clyde.MainWindow)
            throw new DiagnosticException("access_denied", "Input requires a graphical, non-secret control in this client's own window.");
        if (!TryConsumeGuiInput())
            throw new DiagnosticException("busy", "Diagnostic GUI input rate exceeded.", true);
    }
    private JsonObject InteractScroll(Control control, JsonObject arguments)
    {
        RequireGuiInput(control);
        if (control is not ScrollContainer scroll)
            throw new DiagnosticException("unsupported_control", "Scrolling requires an observed scroll container.");
        var input = ParseGuiInput<ScrollInput>(arguments);
        if (input.X is < -512 or > 512 || input.Y is < -512 or > 512 || input is { X: 0, Y: 0 })
            throw new DiagnosticException("invalid_arguments", "Scroll deltas must be nonzero and bounded to 512 UI units per axis.");
        scroll.HScrollTarget += input.X;
        scroll.VScrollTarget += input.Y;
        return new JsonObject { ["submitted"] = true, ["outcomeConfirmed"] = false, ["frame"] = _frame,
            ["executionPath"] = "public-ui-control-api" };
    }
}
#endif
