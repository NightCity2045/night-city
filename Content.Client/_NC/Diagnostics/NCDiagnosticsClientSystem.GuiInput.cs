// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Content.Client._NC.Meta.Editor;
using Content.Client._NC.Meta.UI;
using Content.Meta.Hosting;
using NightCity.Diagnostics.Contracts;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;

namespace Content.Client._NC.Diagnostics;

public sealed partial class NCDiagnosticsClientSystem
{
    private sealed record EditorInput(string Op, string Text = "", int StartRunes = 0, int LengthRunes = 0, Guid Lease = default, Guid Nonce = default);
    private sealed record ClipboardInput(string Op, string Text = "", Guid Nonce = default);
    private sealed record ResizeInput(int Width = 0, int Height = 0, bool Restore = false);
    private sealed record ScrollInput(int X = 0, int Y = 0);
    private sealed class ClipboardLease(Guid id, long world, FileStream exclusive, string seed, TimeSpan expires)
    {
        public readonly Guid Id = id;
        public readonly long World = world;
        public readonly FileStream Exclusive = exclusive;
        public readonly string Seed = seed;
        public readonly TimeSpan Expires = expires;
        public string? Previous;
        public string? LastWritten;
        public string Phase = "reading-previous";
        public Task<string>? Read;
        public TimeSpan ReadDeadline;
    }
    [Dependency] private IClipboardManager _guiClipboard = default!;
    private static readonly JsonSerializerOptions GuiJson = new(JsonSerializerDefaults.Web)
    { MaxDepth = 4, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly UTF8Encoding GuiUtf8 = new(false, true);
    private readonly MetaRateLimit _guiInputRate = new(12, 4);
    private ClipboardLease? _clipboardLease;
    private Task<string>? _stalledClipboardRead;
    private JsonObject _clipboardResult = new() { ["state"] = "none" };
    private Vector2i? _originalClientSize;
    private readonly Dictionary<Control, Vector2> _originalControlSizes = [];

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
    private void RequireGuiInput(Control control)
    {
        if (!_graphics || !_canInput || SensitiveTextControl(control) || control.Root?.Window != _clyde.MainWindow)
            throw new DiagnosticException("access_denied", "Input requires a graphical, non-secret control in this client's own window.");
        if (!_guiInputRate.TryConsume((long) _timing.RealTime.TotalMilliseconds))
            throw new DiagnosticException("busy", "Diagnostic GUI input rate exceeded.", true);
    }
    private ClipboardLease RequireClipboardLease(Guid id)
    {
        if (_clipboardLease is not { } lease || lease.Id != id || lease.World != _pump.WorldGeneration ||
            lease.Phase != "seeded" || _timing.RealTime >= lease.Expires)
            throw new DiagnosticException("stale_clipboard_lease", "A current seeded clipboard test lease is required.");
        return lease;
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
    private JsonObject InteractEditor(Control control, JsonObject arguments)
    {
        RequireGuiInput(control);
        if (control is not NCMetaCodeEditor editor) throw new DiagnosticException("unsupported_control", "Editor input requires the native META editor.");
        var input = ParseGuiInput<EditorInput>(arguments);
        ClipboardLease? lease = null;
        if (input.Op is "copy" or "cut" or "paste" or "paste_hold" or "paste_release") lease = RequireClipboardLease(input.Lease);
        bool submitted;
        if (input.Op == "paste_hold") submitted = editor.TryArmDiagnosticPaste(input.Nonce);
        else if (input.Op == "paste_release") submitted = editor.TryReleaseDiagnosticPaste(input.Nonce);
        else if (input.Op == "paste") submitted = editor.TryDiagnosticPaste(lease!.LastWritten!);
        else if (input.Op is "copy" or "cut")
        {
            if (!editor.HasKeyboardFocus()) throw new DiagnosticException("control_not_focused", "Focus the observed editor first.");
            var selection = editor.GetDiagnosticEditorState();
            var copied = editor.Source.Substring(Math.Min(selection.CaretUtf16, selection.AnchorUtf16), Math.Abs(selection.CaretUtf16 - selection.AnchorUtf16));
            if (Encoding.UTF8.GetByteCount(copied) > 8192) throw new DiagnosticException("invalid_arguments", "Clipboard test selection exceeds 8192 UTF-8 bytes.");
            submitted = editor.TryCopySelection(input.Op == "cut");
            if (submitted) lease!.LastWritten = copied;
        }
        else submitted = editor.TryDiagnosticEditorInput(input.Op, input.Text, input.StartRunes, input.LengthRunes);
        if (!submitted) throw new DiagnosticException("input_rejected", "The native editor rejected this focused input operation.");
        return new JsonObject { ["submitted"] = true, ["outcomeConfirmed"] = false, ["frame"] = _frame,
            ["executionPath"] = input.Op is "copy" or "cut" ? "native-control-clipboard-api" : "native-content-input-handler",
            ["osImeCandidateCoverage"] = false };
    }
    private JsonObject InteractClipboard(Control control, JsonObject arguments)
    {
        RequireGuiInput(control);
        if (control is not NCMetaCodeEditor editor) throw new DiagnosticException("unsupported_control", "Clipboard testing requires an observed META editor.");
        var input = ParseGuiInput<ClipboardInput>(arguments);
        if (input.Op == "status")
        {
            if (_clipboardLease is { } active && active.Id == input.Nonce) return ClipboardObservation();
            if (_clipboardResult["nonce"]?.GetValue<string>() == input.Nonce.ToString("D")) return (JsonObject) _clipboardResult.DeepClone();
            throw new DiagnosticException("stale_clipboard_lease", "The clipboard test identity is not retained.");
        }
        if (input.Op == "release")
        {
            if (_clipboardLease is not { } active || active.Id != input.Nonce) throw new DiagnosticException("stale_clipboard_lease", "No matching clipboard test lease exists.");
            BeginClipboardRestore(active);
            return ClipboardObservation();
        }
        if (input.Op != "seed" || !ValidGuiText(input.Text, 2048, 8192) ||
            input.Text.EnumerateRunes().Any(rune => rune.Value == 0))
            throw new DiagnosticException("invalid_arguments", "Clipboard seed requires a bounded literal text value without NUL.");
        if (!editor.Editable || !editor.HasKeyboardFocus()) throw new DiagnosticException("control_not_focused", "Seed the clipboard test from a focused editable editor.");
        if (_clipboardLease != null || _stalledClipboardRead is { IsCompleted: false }) throw new DiagnosticException("busy", "A clipboard read or lease is still active.", true);
        FileStream exclusive;
        try { exclusive = new FileStream(Path.Combine(Path.GetTempPath(), "night-city-diagnostic-clipboard.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new DiagnosticException("busy", "Another client owns the OS clipboard test lease.", true); }
        catch (UnauthorizedAccessException) { throw new DiagnosticException("clipboard_unavailable", "The process cannot acquire its exclusive clipboard test lock."); }
        var lease = new ClipboardLease(Guid.NewGuid(), _pump.WorldGeneration, exclusive, input.Text, _timing.RealTime + TimeSpan.FromSeconds(30));
        _clipboardLease = lease;
        try { StartClipboardRead(lease); }
        catch { EndClipboardLease(lease, "clipboard-unavailable"); }
        return ClipboardObservation();
    }
    private void StartClipboardRead(ClipboardLease lease)
    {
        lease.ReadDeadline = _timing.RealTime + TimeSpan.FromSeconds(1);
        lease.Read = _guiClipboard.GetText();
    }
    private void BeginClipboardRestore(ClipboardLease lease)
    {
        if (lease.Phase == "checking-restore") return;
        if (lease.LastWritten == null) { EndClipboardLease(lease, "cancelled-before-write"); return; }
        lease.Phase = "checking-restore";
        try { StartClipboardRead(lease); }
        catch { EndClipboardLease(lease, "restore-unconfirmed"); }
    }
    private void UpdateGuiDiagnostics()
    {
        if (_stalledClipboardRead is { IsCompleted: true } finished) { _ = finished.Exception; _stalledClipboardRead = null; }
        var lease = _clipboardLease;
        if (lease == null) return;
        if (lease.World != _pump.WorldGeneration || _timing.RealTime >= lease.Expires)
        {
            BeginClipboardRestore(lease);
            if (_clipboardLease == null) return;
        }
        if (lease.Read is not { } read) return;
        if (!read.IsCompleted)
        {
            if (_timing.RealTime < lease.ReadDeadline) return;
            _stalledClipboardRead = read; // Quarantine additional reads until the actual provider completes.
            EndClipboardLease(lease, lease.LastWritten == null ? "clipboard-timeout" : "restore-unconfirmed");
            return;
        }
        lease.Read = null;
        if (!read.IsCompletedSuccessfully)
        {
            _ = read.Exception;
            EndClipboardLease(lease, lease.LastWritten == null ? "clipboard-unavailable" : "restore-unconfirmed");
            return;
        }
        var current = read.GetAwaiter().GetResult(); // Already completed; never blocks the UI thread.
        if (current == null) { EndClipboardLease(lease, "clipboard-unavailable"); return; }
        if (lease.Phase == "reading-previous")
        {
            if (current.Length > 1024 * 1024 || Encoding.UTF8.GetByteCount(current) > 1024 * 1024) { EndClipboardLease(lease, "clipboard-too-large"); return; }
            lease.Previous = current;
            lease.LastWritten = lease.Seed;
            try { _guiClipboard.SetText(lease.Seed); lease.Phase = "seeded"; }
            catch { BeginClipboardRestore(lease); }
        }
        else
        {
            if (current != lease.LastWritten) { EndClipboardLease(lease, "restore-skipped-concurrent-change"); return; }
            try { _guiClipboard.SetText(lease.Previous!); EndClipboardLease(lease, "restored"); }
            catch { EndClipboardLease(lease, "restore-unconfirmed"); }
        }
    }
    private void EndClipboardLease(ClipboardLease lease, string state)
    {
        if (lease.Read is { IsCompleted: false } pending) _stalledClipboardRead = pending;
        lease.Exclusive.Dispose(); lease.Previous = lease.LastWritten = null;
        if (ReferenceEquals(_clipboardLease, lease)) _clipboardLease = null;
        _clipboardResult = new() { ["nonce"] = lease.Id.ToString("D"), ["state"] = state, ["frame"] = _frame };
        if (state == "restore-unconfirmed") Log.Warning("Diagnostic clipboard restoration could not be confirmed; no unconditional overwrite was performed.");
    }
    private JsonObject ClipboardObservation() => _clipboardLease is { } lease
        ? new() { ["nonce"] = lease.Id.ToString("D"), ["state"] = lease.Phase,
            ["expiresInMilliseconds"] = Math.Max(0, (lease.Expires - _timing.RealTime).TotalMilliseconds), ["frame"] = _frame }
        : (JsonObject) _clipboardResult.DeepClone();

    private JsonObject ResizeDiagnosticWindow(Control control, JsonObject arguments, bool client)
    {
        RequireGuiInput(control);
        if (control is not (NCMetaWorkspaceWindow or NCMetaApplicationWindow)) throw new DiagnosticException("unsupported_control", "Only observed META workspace/application windows may be resized.");
        var input = ParseGuiInput<ResizeInput>(arguments);
        if (client)
        {
            if (input.Restore)
            {
                if (_originalClientSize is not { } previous) throw new DiagnosticException("invalid_arguments", "No client size was retained.");
                _clyde.MainWindow.Size = previous; _originalClientSize = null;
            }
            else
            {
                if (input.Width is not (1024 or 1280 or 1920) || input.Height is not (768 or 900 or 1080)) throw new DiagnosticException("invalid_arguments", "Client size must use an admitted acceptance width and height.");
                _originalClientSize ??= _clyde.MainWindow.Size;
                _clyde.MainWindow.Size = new(input.Width, input.Height);
            }
        }
        else if (input.Restore)
        {
            if (!_originalControlSizes.Remove(control, out var previous)) throw new DiagnosticException("invalid_arguments", "No control size was retained.");
            control.SetSize = previous;
        }
        else
        {
            foreach (var removed in _originalControlSizes.Keys.Where(candidate => candidate.Disposed).ToArray()) _originalControlSizes.Remove(removed);
            var valid = control is NCMetaWorkspaceWindow ? input.Width is 820 or 1024 or 1280 or 1440 : input.Width is 320 or 640 or 800 or 1024;
            if (!valid || input.Height is < 240 or > 1080 || control is NCMetaWorkspaceWindow && input.Height < 600)
                throw new DiagnosticException("invalid_arguments", "Window size exceeds the admitted META layout sizes.");
            if (!_originalControlSizes.ContainsKey(control) && _originalControlSizes.Count >= 4) throw new DiagnosticException("busy", "Four windows are already tracked.", true);
            _originalControlSizes.TryAdd(control, control.SetSize);
            control.SetSize = new(input.Width, input.Height);
        }
        return new() { ["submitted"] = true, ["outcomeConfirmed"] = false, ["frame"] = _frame,
            ["executionPath"] = client ? "public-native-window-size" : "public-meta-control-layout",
            ["windowWidth"] = _clyde.MainWindow.Size.X, ["windowHeight"] = _clyde.MainWindow.Size.Y,
            ["framebufferWidth"] = _clyde.MainWindow.RenderTarget.Size.X, ["framebufferHeight"] = _clyde.MainWindow.RenderTarget.Size.Y };
    }
    private void ResetGuiDiagnostics(bool shuttingDown = false)
    {
        if (_clipboardLease is { } lease)
        {
            BeginClipboardRestore(lease);
            UpdateGuiDiagnostics();
            if (shuttingDown && _clipboardLease != null) EndClipboardLease(lease, "restore-unconfirmed");
        }
        if (_originalClientSize is { } size && !_clyde.MainWindow.IsDisposed) _clyde.MainWindow.Size = size;
        _originalClientSize = null;
        foreach (var (control, previous) in _originalControlSizes) if (!control.Disposed) control.SetSize = previous;
        _originalControlSizes.Clear();
    }
}
#endif
