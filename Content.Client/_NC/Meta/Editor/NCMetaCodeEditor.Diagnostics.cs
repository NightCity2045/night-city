// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Robust.Shared.Map;

namespace Content.Client._NC.Meta.Editor;

public sealed partial class NCMetaCodeEditor
{
    public sealed record DiagnosticEditorState(int Utf16Characters, int Utf8Bytes, string Sha256,
        int CaretUtf16, int AnchorUtf16, int CompositionCharacters, int CompositionCaretUtf16,
        int UndoCount, int RedoCount, bool Editable, bool Focused, long InputGeneration, bool PasteHeld);
    private TaskCompletionSource? _diagnosticPasteGate;
    private Guid _diagnosticPasteNonce;
    private bool _diagnosticPasteWaiting;
    private string? _diagnosticExpectedClipboard;

    /// <summary>Caller must apply the diagnostic visibility, explicit text and secret ancestry guards.</summary>
    public DiagnosticEditorState GetDiagnosticEditorState() => new(_text.Length, Encoding.UTF8.GetByteCount(_text),
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_text))), _caret, _anchor, _composition.Length,
        _compositionCaret, _undo.Count, _redo.Count, Editable, HasKeyboardFocus(), _inputGeneration, _diagnosticPasteWaiting);

    /// <summary>Exercises the actual content input handlers; does not emulate an OS input method.</summary>
    public bool TryDiagnosticEditorInput(string operation, string text = "", int startRunes = 0, int lengthRunes = 0)
    {
        if (Disposed || !VisibleInTree || !HasKeyboardFocus() || Root?.Window is not { } window ||
            text == null || text.Length > 2048 || Encoding.UTF8.GetByteCount(text) > 8192 || !IsValidUtf16(text)) return false;
        if (operation == "preedit")
        {
            var runes = 0;
            foreach (var rune in text.EnumerateRunes()) runes++;
            if (!Editable || startRunes < 0 || lengthRunes < 0 || startRunes > runes || lengthRunes > runes - startRunes) return false;
            TextEditing(new GUITextEditingEventArgs(this, new TextEditingEventArgs(text, startRunes, lengthRunes)));
            return true;
        }
        if (operation == "commit")
        {
            if (!Editable) return false;
            TextEntered(new GUITextEnteredEventArgs(this, new TextEnteredEventArgs(text)));
            return true;
        }
        BoundKeyFunction? function = operation switch
        {
            "select-all" => EngineKeyFunctions.TextSelectAll, "left" => EngineKeyFunctions.TextCursorLeft,
            "right" => EngineKeyFunctions.TextCursorRight, "select-left" => EngineKeyFunctions.TextCursorSelectLeft,
            "select-right" => EngineKeyFunctions.TextCursorSelectRight, "copy" => EngineKeyFunctions.TextCopy,
            "cut" when Editable => EngineKeyFunctions.TextCut, "paste" when Editable => EngineKeyFunctions.TextPaste,
            "backspace" when Editable => EngineKeyFunctions.TextBackspace, "delete" when Editable => EngineKeyFunctions.TextDelete,
            _ => (BoundKeyFunction?) null,
        };
        if (function is not { } admittedFunction) return false;
        var position = new ScreenCoordinates(Vector2.Zero, window.Id);
        KeyBindDown(new GUIBoundKeyEventArgs(admittedFunction, BoundKeyState.Down, position, false, Vector2.Zero, Vector2.Zero));
        KeyBindUp(new GUIBoundKeyEventArgs(admittedFunction, BoundKeyState.Up, position, false, Vector2.Zero, Vector2.Zero));
        return true;
    }

    public bool TryArmDiagnosticPaste(Guid nonce)
    {
        if (_diagnosticPasteGate is { Task.IsCompleted: true }) _diagnosticPasteGate = null;
        if (nonce == Guid.Empty || Disposed || !Editable || !VisibleInTree || !HasKeyboardFocus() || _diagnosticPasteGate != null) return false;
        _diagnosticPasteNonce = nonce;
        var gate = _diagnosticPasteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ReleaseAfterTimeoutAsync(gate);
        return true;
    }
    /// <summary>Read the real OS clipboard, but diagnostic tests may paste only their exact leased value.</summary>
    public bool TryDiagnosticPaste(string expected)
    {
        if (Disposed || !Editable || !VisibleInTree || !HasKeyboardFocus() || expected == null || expected.Length > 8192 || !IsValidUtf16(expected)) return false;
        _diagnosticExpectedClipboard = expected;
        _ = TryPasteAsync();
        return true;
    }
    public bool TryReleaseDiagnosticPaste(Guid nonce)
    {
        if (nonce == Guid.Empty || nonce != _diagnosticPasteNonce || _diagnosticPasteGate == null) return false;
        _diagnosticPasteGate.TrySetResult();
        return true;
    }
    private async Task ReleaseAfterTimeoutAsync(TaskCompletionSource gate)
    {
        await Task.Delay(250);
        gate.TrySetResult();
    }
    private async Task WaitDiagnosticClipboardReadAsync(TaskCompletionSource? gate)
    {
        if (gate == null) return;
        _diagnosticPasteWaiting = true;
        try { await gate.Task; }
        finally
        {
            _diagnosticPasteWaiting = false;
            if (ReferenceEquals(_diagnosticPasteGate, gate)) { _diagnosticPasteGate = null; _diagnosticPasteNonce = Guid.Empty; }
        }
    }
}
#endif
