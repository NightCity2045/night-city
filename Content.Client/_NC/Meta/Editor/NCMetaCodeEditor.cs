// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Content.Client.Resources;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Robust.Shared.Utility;

namespace Content.Client._NC.Meta.Editor;

/// <summary>Content-owned source editor. UTF-16 carets use grapheme boundaries; wire offsets use UTF-8 bytes.</summary>
public sealed partial class NCMetaCodeEditor : Control
{
    public const int MaximumBytes = 256 * 1024;
    private const int UndoEntries = 32;
    [Dependency] private IResourceCache _resources = default!;
    [Dependency] private IClipboardManager _clipboard = default!;
    private readonly Font _font;
    private readonly Stack<(Rope.Node Text, int Caret)> _undo = [];
    private readonly Stack<(Rope.Node Text, int Caret)> _redo = [];
    private Rope.Node _rope = Rope.Leaf.Empty;
    private string _text = "";
    private readonly List<int> _lines = new() { 0 };
    private readonly List<(int Start, int End, int Line)> _visual = new() { (0, 0, 0) };
    private List<int> _graphemes = new();
    private int _caret;
    private int _anchor;
    private int _firstLine;
    private string _composition = "";
    private int _compositionCaret;
    private bool _dragging;
    private long _inputGeneration;
    private readonly List<(int Start, int End, Color Color)> _syntax = [];
    private readonly List<LexLine> _lexLines = [];
    private readonly List<LayoutLine> _layoutLines = [];
    private enum LexMode : byte { Code, String, BlockComment }
    private sealed record LexLine(string Text, LexMode Input, LexMode Output, (int Start, int End, Color Color)[] Spans);
    private sealed record LayoutLine(string Text, float Width, float Scale, bool Wrap, (int Start, int End)[] Segments);
    private static readonly Color CommentColor = Color.FromHex("#708D75");
    private static readonly Color StringColor = Color.FromHex("#D7B27A");
    private static readonly Color KeywordColor = Color.FromHex("#A28ED4");
    private static readonly Color NumberColor = Color.FromHex("#6EBFCA");
    private static readonly HashSet<string> Keywords = ["module", "import", "as", "pub", "link", "fn", "async", "watch", "let", "var", "const", "struct", "enum", "if", "else", "while", "for", "in", "return", "break", "continue", "match", "some", "none", "ok", "err", "true", "false", "await", "yield", "new", "void", "bool", "byte", "int", "float", "string", "array", "list", "map", "option", "result"];
    private bool _editable = true;
    public bool Editable
    {
        get => _editable;
        set
        {
            if (_editable == value) return;
            _editable = value;
            _inputGeneration++;
            _composition = "";
            _compositionCaret = 0;
            if (!HasKeyboardFocus()) return;
            if (value) Root?.Window?.TextInputStart();
            else Root?.Window?.TextInputStop();
        }
    }
    public bool WordWrap { get; set; } = true;
    public event Action? SourceChanged;
    public event Action? CaretChanged;
    public event Action? ClipboardUnavailable;
    public string Source => _text;
    public int CaretUtf16 => _caret;
    public int SelectionAnchorUtf16 => _anchor;
    public int CaretUtf8 => Encoding.UTF8.GetByteCount(_text.AsSpan(0, _caret));
    public int CaretLine
    {
        get
        {
            var index = _lines.BinarySearch(_caret);
            return Math.Max(0, index >= 0 ? index : ~index - 1);
        }
    }
    public int LineCount => _lines.Count;

    public NCMetaCodeEditor()
    {
        IoCManager.InjectDependencies(this);
        _font = _resources.GetFont(new[] { "/EngineFonts/NotoSans/NotoSans-Regular.ttf", "/Fonts/NotoEmoji.ttf", "/Fonts/NotoSans/NotoSansSymbols2-Regular.ttf" }, 13);
        CanKeyboardFocus = true;
        KeyboardFocusOnClick = true;
        MouseFilter = MouseFilterMode.Stop;
        RectClipContent = true;
        HorizontalExpand = true;
        VerticalExpand = true;
        MinHeight = 200;
        DefaultCursorShape = CursorShape.IBeam;
    }

    public bool TrySetSource(string source, bool clearHistory = true)
    {
        source = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (Encoding.UTF8.GetByteCount(source) > MaximumBytes || !IsValidUtf16(source))
            return false;
        CancelComposition();
        _text = source;
        _rope = NCMetaEditorRope.Create(source);
        _caret = Math.Min(_caret, source.Length);
        _anchor = _caret;
        Reindex();
        if (clearHistory)
        {
            _undo.Clear();
            _redo.Clear();
            _firstLine = 0;
        }
        _inputGeneration++;
        return true;
    }

    public bool TryReplaceSelection(string insertion)
    {
        if (!Editable || insertion.Length > MaximumBytes * 2)
            return false;
        insertion = insertion.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!IsValidUtf16(insertion))
            return false;
        var start = Math.Min(_caret, _anchor);
        var length = Math.Abs(_caret - _anchor);
        if (Encoding.UTF8.GetByteCount(_text.AsSpan(0, start)) + Encoding.UTF8.GetByteCount(insertion) +
            Encoding.UTF8.GetByteCount(_text.AsSpan(start + length)) > MaximumBytes)
            return false;
        CancelComposition();
        if (_undo.Count >= UndoEntries)
        {
            var retained = _undo.Take(UndoEntries - 1).Reverse().ToArray();
            _undo.Clear();
            foreach (var previous in retained) _undo.Push(previous);
        }
        _undo.Push((_rope, _caret));
        _redo.Clear();
        _rope = NCMetaEditorRope.Replace(_rope, start, length, insertion);
        _text = Rope.Collapse(_rope);
        _caret = _anchor = start + insertion.Length;
        _inputGeneration++;
        Reindex();
        EnsureCaretVisible();
        SourceChanged?.Invoke();
        return true;
    }

    /// <summary>Replaces the document as one normal, undoable edit and keeps the selection if rejected.</summary>
    public bool TryReplaceDocument(string source)
    {
        if (!Editable)
            return false;
        var caret = _caret;
        var anchor = _anchor;
        _anchor = 0;
        _caret = _text.Length;
        if (TryReplaceSelection(source))
            return true;
        _caret = caret;
        _anchor = anchor;
        return false;
    }

    public void Undo() => Restore(_undo, _redo);
    public void Redo() => Restore(_redo, _undo);
    private void Restore(Stack<(Rope.Node Text, int Caret)> source, Stack<(Rope.Node Text, int Caret)> target)
    {
        if (!Editable || !source.TryPop(out var previous))
            return;
        CancelComposition();
        target.Push((_rope, _caret));
        _rope = previous.Text;
        _text = Rope.Collapse(_rope);
        _caret = _anchor = previous.Caret;
        _inputGeneration++;
        Reindex();
        EnsureCaretVisible();
        SourceChanged?.Invoke();
    }

    public bool TryFind(string query, bool next = true)
    {
        if (query.Length is < 1 or > 256)
            return false;
        var start = next ? Math.Min(_text.Length, Math.Max(_caret, _anchor)) : 0;
        var found = _text.IndexOf(query, start, StringComparison.Ordinal);
        if (found < 0)
            found = _text.IndexOf(query, StringComparison.Ordinal);
        if (found < 0)
            return false;
        CancelComposition();
        _anchor = GraphemeFloor(found);
        _caret = GraphemeCeiling(found + query.Length);
        _inputGeneration++;
        EnsureCaretVisible();
        CaretChanged?.Invoke();
        return true;
    }

    public bool TryReplaceAll(string query, string replacement)
    {
        if (!Editable || query.Length is < 1 or > 256 || replacement.Length > 4096 || !IsValidUtf16(query) || !IsValidUtf16(replacement))
            return false;
        replacement = replacement.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var matches = 0;
        for (var index = 0; index <= _text.Length - query.Length;)
        {
            var found = _text.IndexOf(query, index, StringComparison.Ordinal);
            if (found < 0) break;
            matches++;
            index = found + query.Length;
        }
        var projectedBytes = (long) Encoding.UTF8.GetByteCount(_text) +
            (long) (Encoding.UTF8.GetByteCount(replacement) - Encoding.UTF8.GetByteCount(query)) * matches;
        if (matches == 0 || projectedBytes > MaximumBytes)
            return false;
        var result = _text.Replace(query, replacement, StringComparison.Ordinal);
        if (result == _text)
            return false;
        return TryReplaceDocument(result);
    }

    public void GoToUtf8(int offset)
    {
        CancelComposition();
        var bytes = 0;
        var chars = 0;
        foreach (var rune in _text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > offset)
                break;
            bytes += rune.Utf8SequenceLength;
            chars += rune.Utf16SequenceLength;
        }
        _caret = _anchor = GraphemeFloor(chars);
        _inputGeneration++;
        EnsureCaretVisible();
        CaretChanged?.Invoke();
    }

    protected override void TextEntered(GUITextEnteredEventArgs args)
    {
        base.TextEntered(args);
        var composed = _composition.Length != 0;
        _composition = "";
        _compositionCaret = 0;
        if (!Editable) return;
        if (!composed && args.Text.Length == 1 && _caret == _anchor)
        {
            var character = args.Text[0];
            if (_caret < _text.Length && _text[_caret] == character &&
                (character is ')' or ']' or '}' && IsCodeCaret() || character == '"' && _syntax.Any(span => span.Color == StringColor && span.End == _caret + 1)))
            { Move(GraphemeCeiling(_caret + 1), false); EnsureCaretVisible(); return; }
            var close = character switch { '(' => ")", '[' => "]", '{' => "}", '"' => "\"", _ => "" };
            if (close.Length != 0 && IsCodeCaret())
            {
                if (TryReplaceSelection(string.Concat(args.Text, close))) Move(_caret - 1, false);
                return;
            }
        }
        TryReplaceSelection(args.Text);
    }

    protected override void TextEditing(GUITextEditingEventArgs args)
    {
        base.TextEditing(args);
        _inputGeneration++;
        _composition = Editable && args.Event.Text.Length <= 4096 && IsValidUtf16(args.Event.Text) ? args.Event.Text : "";
        _compositionCaret = Math.Clamp(args.Event.GetStartChars(), 0, _composition.Length);
    }

    protected override void KeyboardFocusEntered()
    {
        base.KeyboardFocusEntered();
        if (Editable)
            Root?.Window?.TextInputStart();
    }

    protected override void KeyboardFocusExited()
    {
        _composition = "";
        _compositionCaret = 0;
        _dragging = false;
        _inputGeneration++;
        Root?.Window?.TextInputStop();
        base.KeyboardFocusExited();
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);
        var function = args.Function;
        var selecting = function == EngineKeyFunctions.TextCursorSelectLeft || function == EngineKeyFunctions.TextCursorSelectRight ||
            function == EngineKeyFunctions.TextCursorSelectUp || function == EngineKeyFunctions.TextCursorSelectDown ||
            function == EngineKeyFunctions.TextCursorSelectBegin || function == EngineKeyFunctions.TextCursorSelectEnd ||
            function == EngineKeyFunctions.TextCursorSelectWordLeft || function == EngineKeyFunctions.TextCursorSelectWordRight;
        if (function == EngineKeyFunctions.UIClick || function == EngineKeyFunctions.TextCursorSelect)
        {
            CancelComposition();
            _caret = HitTest(args.RelativePixelPosition);
            if (function != EngineKeyFunctions.TextCursorSelect)
                _anchor = _caret;
            _dragging = true;
            _inputGeneration++;
        }
        else if (function == EngineKeyFunctions.TextCursorLeft || function == EngineKeyFunctions.TextCursorSelectLeft)
            Move(GraphemeFloor(Math.Max(0, _caret - 1)), selecting);
        else if (function == EngineKeyFunctions.TextCursorRight || function == EngineKeyFunctions.TextCursorSelectRight)
            Move(GraphemeCeiling(Math.Min(_text.Length, _caret + 1)), selecting);
        else if (function == EngineKeyFunctions.TextCursorWordLeft || function == EngineKeyFunctions.TextCursorSelectWordLeft)
            Move(WordLeft(), selecting);
        else if (function == EngineKeyFunctions.TextCursorWordRight || function == EngineKeyFunctions.TextCursorSelectWordRight)
            Move(WordRight(), selecting);
        else if (function == EngineKeyFunctions.TextCursorBegin || function == EngineKeyFunctions.TextCursorSelectBegin)
            Move(_lines[CaretLine], selecting);
        else if (function == EngineKeyFunctions.TextCursorEnd || function == EngineKeyFunctions.TextCursorSelectEnd)
            Move(LineEnd(CaretLine), selecting);
        else if (function == EngineKeyFunctions.TextCursorUp || function == EngineKeyFunctions.TextCursorSelectUp)
            MoveVertical(-1, selecting);
        else if (function == EngineKeyFunctions.TextCursorDown || function == EngineKeyFunctions.TextCursorSelectDown)
            MoveVertical(1, selecting);
        else if (function == EngineKeyFunctions.TextBackspace)
        {
            if (_caret == _anchor)
                _anchor = GraphemeFloor(Math.Max(0, _caret - 1));
            TryReplaceSelection("");
        }
        else if (function == EngineKeyFunctions.TextDelete)
        {
            if (_caret == _anchor)
                _anchor = GraphemeCeiling(Math.Min(_text.Length, _caret + 1));
            TryReplaceSelection("");
        }
        else if (function == EngineKeyFunctions.TextWordBackspace || function == EngineKeyFunctions.TextWordDelete)
        {
            if (_caret == _anchor) _anchor = function == EngineKeyFunctions.TextWordBackspace ? WordLeft() : WordRight();
            TryReplaceSelection("");
        }
        else if (function == EngineKeyFunctions.TextNewline)
        {
            var prefix = _text[_lines[CaretLine].._caret];
            var indent = new string(prefix.TakeWhile(x => x == ' ').ToArray());
            var trimmed = prefix.TrimEnd();
            var opens = trimmed.EndsWith('{') && IsCodeOffset(_lines[CaretLine] + trimmed.Length - 1);
            TryReplaceSelection("\n" + indent + (opens ? "    " : ""));
        }
        else if (function == EngineKeyFunctions.TextTabComplete)
            TryReplaceSelection("    ");
        else if (function == EngineKeyFunctions.TextSelectAll)
        {
            _anchor = 0;
            _caret = _text.Length;
            _inputGeneration++;
        }
        else if (function == EngineKeyFunctions.TextCopy || function == EngineKeyFunctions.TextCut)
            TryCopySelection(function == EngineKeyFunctions.TextCut);
        else if (function == EngineKeyFunctions.TextPaste)
            _ = TryPasteAsync();
        else
            return;
        EnsureCaretVisible();
        CaretChanged?.Invoke();
        args.Handle();
    }

    public bool TryCopySelection(bool cut = false)
    {
        if (Disposed || cut && !Editable || _caret == _anchor) return false;
        try
        {
            _clipboard.SetText(_text.Substring(Math.Min(_caret, _anchor), Math.Abs(_caret - _anchor)));
            return !cut || TryReplaceSelection("");
        }
        catch (Exception) { if (!Disposed) ClipboardUnavailable?.Invoke(); return false; }
    }

    public async Task<bool> TryPasteAsync()
    {
        if (Disposed || !Editable || !VisibleInTree || !HasKeyboardFocus()) return false;
        var generation = _inputGeneration;
#if TOOLS && NC_DIAGNOSTICS
        var diagnosticPasteGate = _diagnosticPasteGate;
        var diagnosticExpectedClipboard = _diagnosticExpectedClipboard;
        _diagnosticExpectedClipboard = null;
#endif
        try
        {
            var text = await _clipboard.GetText();
#if TOOLS && NC_DIAGNOSTICS
            if (diagnosticExpectedClipboard != null && text != diagnosticExpectedClipboard) return false;
            await WaitDiagnosticClipboardReadAsync(diagnosticPasteGate);
#endif
            return !Disposed && Editable && VisibleInTree && generation == _inputGeneration && HasKeyboardFocus() && TryReplaceSelection(text);
        }
        catch (Exception)
        {
            if (!Disposed)
                ClipboardUnavailable?.Invoke();
            return false;
        }
    }

    private void CancelComposition()
    {
        if (_composition.Length == 0) return;
        _composition = "";
        _compositionCaret = 0;
        if (!HasKeyboardFocus()) return;
        Root?.Window?.TextInputStop();
        if (Editable) Root?.Window?.TextInputStart();
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);
        if (args.Function == EngineKeyFunctions.UIClick || args.Function == EngineKeyFunctions.TextCursorSelect)
            _dragging = false;
    }

    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);
        if (_dragging)
        {
            _caret = HitTest(args.RelativePixelPosition);
            _inputGeneration++;
            EnsureCaretVisible();
        }
    }

    protected override void MouseWheel(GUIMouseWheelEventArgs args)
    {
        base.MouseWheel(args);
        _firstLine = Math.Clamp(_firstLine - (int) args.Delta.Y * 3, 0, Math.Max(0, _visual.Count - 1));
        args.Handle();
    }

    private void Move(int position, bool selecting)
    {
        CancelComposition();
        _caret = position;
        if (!selecting)
            _anchor = _caret;
        _inputGeneration++;
    }

    private void MoveVertical(int delta, bool selecting)
    {
        var line = CaretVisualLine();
        var horizontal = Measure(_text[_visual[line].Start.._caret]);
        var target = Math.Clamp(line + delta, 0, _visual.Count - 1);
        var x = 0f;
        foreach (var element in NCMetaGraphemes.Elements(_text[_visual[target].Start.._visual[target].End]))
        {
            var width = Measure(element.Text);
            if (horizontal < x + width / 2) { Move(_visual[target].Start + element.Index, selecting); return; }
            x += width;
        }
        Move(_visual[target].End, selecting);
    }

    private static bool WordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
    private int WordLeft()
    {
        var index = _caret;
        while (index > 0 && !WordCharacter(_text[index - 1])) index--;
        while (index > 0 && WordCharacter(_text[index - 1])) index--;
        return GraphemeFloor(index);
    }
    private int WordRight()
    {
        var index = _caret;
        while (index < _text.Length && WordCharacter(_text[index])) index++;
        while (index < _text.Length && !WordCharacter(_text[index])) index++;
        return GraphemeCeiling(index);
    }

    private void EnsureCaretVisible()
    {
        var visibleLines = Math.Max(1, (int) (PixelSize.Y / Math.Max(1, _font.GetLineHeight(UIScale))));
        var line = CaretVisualLine();
        _firstLine = Math.Clamp(_firstLine, Math.Max(0, line - visibleLines + 1), line);
    }

    private int CaretVisualLine()
    {
        var low = 0;
        var high = _visual.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (_visual[middle].Start <= _caret)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return Math.Clamp(high, 0, _visual.Count - 1);
    }

    private int LineEnd(int line) => line + 1 < _lines.Count ? _lines[line + 1] - 1 : _text.Length;
    private int GraphemeFloor(int index)
    {
        if (_graphemes.Count == 0)
            return 0;
        if (index >= _text.Length)
            return _text.Length;
        var position = _graphemes.BinarySearch(index);
        return position >= 0 ? index : _graphemes[Math.Max(0, ~position - 1)];
    }
    private int GraphemeCeiling(int index)
    {
        if (_graphemes.Count == 0)
            return 0;
        if (index >= _text.Length)
            return _text.Length;
        var position = _graphemes.BinarySearch(index);
        return position >= 0 ? index : ~position >= _graphemes.Count ? _text.Length : _graphemes[~position];
    }

    private int HitTest(Vector2 position)
    {
        var line = Math.Clamp(_firstLine + (int) (position.Y / Math.Max(1, _font.GetLineHeight(UIScale))), 0, _visual.Count - 1);
        var visual = _visual[line];
        var x = 52 * UIScale;
        foreach (var element in NCMetaGraphemes.Elements(_text[visual.Start..visual.End]))
        {
            var width = Measure(element.Text);
            if (position.X < x + width / 2)
                return visual.Start + element.Index;
            x += width;
        }
        return visual.End;
    }

    private float Measure(string text)
    {
        var width = 0f;
        foreach (var rune in text.EnumerateRunes())
            width += _font.GetCharMetrics(rune, UIScale)?.Advance ?? 0;
        return width;
    }

    private void Reindex()
    {
        _lines.Clear();
        _lines.Add(0);
        for (var i = 0; i < _text.Length; i++)
            if (_text[i] == '\n')
                _lines.Add(i + 1);
        _graphemes = NCMetaGraphemes.Boundaries(_text);
        _caret = GraphemeFloor(_caret);
        _anchor = GraphemeFloor(_anchor);
        UpdateSyntax();
        RebuildLayout();
    }

    protected override void Resized()
    {
        base.Resized();
        RebuildLayout();
        EnsureCaretVisible();
    }

    private void RebuildLayout()
    {
        _visual.Clear();
        var available = PixelSize.X - 60 * UIScale;
        var previous = _layoutLines.ToArray();
        _layoutLines.Clear();
        var suffix = 0;
        while (suffix < previous.Length && suffix < _lines.Count && previous[previous.Length - suffix - 1].Text ==
            _text[_lines[_lines.Count - suffix - 1]..LineEnd(_lines.Count - suffix - 1)]) suffix++;
        for (var line = 0; line < _lines.Count; line++)
        {
            var start = _lines[line];
            var end = LineEnd(line);
            var text = _text[start..end];
            var prior = line >= _lines.Count - suffix ? previous.Length - (_lines.Count - line) : line;
            if (prior >= 0 && prior < previous.Length && previous[prior] is var cached && cached.Text == text && cached.Width == available &&
                cached.Scale == UIScale && cached.Wrap == WordWrap)
            {
                _layoutLines.Add(cached);
                foreach (var part in cached.Segments) _visual.Add((start + part.Start, start + part.End, line));
                continue;
            }
            var segments = new List<(int Start, int End)>();
            if (!WordWrap || available <= 0)
            {
                _visual.Add((start, end, line));
                _layoutLines.Add(new(text, available, UIScale, WordWrap, [(0, end - start)]));
                continue;
            }
            var segment = start;
            var width = 0f;
            foreach (var element in NCMetaGraphemes.Elements(_text[start..end]))
            {
                var offset = start + element.Index;
                var size = Measure(element.Text);
                if (width + size > available && offset > segment)
                {
                    _visual.Add((segment, offset, line));
                    segments.Add((segment - start, offset - start));
                    segment = offset;
                    width = 0;
                }
                width += size;
            }
            _visual.Add((segment, end, line));
            segments.Add((segment - start, end - start));
            _layoutLines.Add(new(text, available, UIScale, WordWrap, segments.ToArray()));
        }
        _firstLine = Math.Clamp(_firstLine, 0, Math.Max(0, _visual.Count - 1));
    }

    private void UpdateSyntax()
    {
        _syntax.Clear();
        var old = _lexLines.ToArray();
        _lexLines.Clear();
        var suffix = 0;
        while (suffix < old.Length && suffix < _lines.Count && old[old.Length - suffix - 1].Text ==
            _text[_lines[_lines.Count - suffix - 1]..LineEnd(_lines.Count - suffix - 1)]) suffix++;
        var mode = LexMode.Code;
        for (var line = 0; line < _lines.Count; line++)
        {
            var text = _text[_lines[line]..LineEnd(line)];
            var prior = line >= _lines.Count - suffix ? old.Length - (_lines.Count - line) : line;
            var cached = prior >= 0 && prior < old.Length && old[prior].Text == text && old[prior].Input == mode ? old[prior] : Lex(text, mode);
            mode = cached.Output;
            _lexLines.Add(cached);
            foreach (var span in cached.Spans)
                _syntax.Add((_lines[line] + span.Start, _lines[line] + span.End, span.Color));
        }
    }

    private static LexLine Lex(string text, LexMode input)
    {
        var spans = new List<(int Start, int End, Color Color)>();
        var mode = input;
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            if (mode == LexMode.BlockComment || i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            {
                if (mode != LexMode.BlockComment) i += 2;
                mode = LexMode.BlockComment;
                while (i < text.Length)
                    if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '/')
                    { i += 2; mode = LexMode.Code; break; }
                    else i++;
                spans.Add((start, i, CommentColor));
            }
            else if (mode == LexMode.String || text[i] == '"')
            {
                if (mode != LexMode.String) i++;
                mode = LexMode.String;
                while (i < text.Length)
                {
                    if (text[i] == '\\') i = Math.Min(text.Length, i + 2);
                    else if (text[i++] == '"') { mode = LexMode.Code; break; }
                }
                spans.Add((start, i, StringColor));
            }
            else if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
            { spans.Add((i, text.Length, CommentColor)); i = text.Length; }
            else if (char.IsAsciiLetter(text[i]) || text[i] == '_')
            {
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_')) i++;
                if (Keywords.Contains(text[start..i])) spans.Add((start, i, KeywordColor));
            }
            else if (char.IsAsciiDigit(text[i]))
            { while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '.' or '_')) i++; spans.Add((start, i, NumberColor)); }
            else i++;
        }
        return new(text, input, mode, spans.ToArray());
    }

    private bool IsCodeOffset(int offset) => !_syntax.Any(span => offset >= span.Start && offset < span.End &&
        (span.Color == CommentColor || span.Color == StringColor));
    private bool IsCodeCaret() => IsCodeOffset(_caret) && !_syntax.Any(span => span.End == _caret && span.Color == CommentColor) &&
        !(_caret == LineEnd(CaretLine) && _lexLines[CaretLine].Output is LexMode.String or LexMode.BlockComment);

    private Color SyntaxColor(int offset)
    {
        var lo = 0;
        var hi = _syntax.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var span = _syntax[mid];
            if (offset < span.Start)
                hi = mid - 1;
            else if (offset >= span.End)
                lo = mid + 1;
            else
                return span.Color;
        }
        return Color.FromHex("#D2E4EF");
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        handle.DrawRect(PixelSizeBox, Color.FromHex("#09121C"));
        var height = Math.Max(1, _font.GetLineHeight(UIScale));
        var visible = Math.Min(_visual.Count, _firstLine + (int) (PixelSize.Y / height) + 1);
        var start = Math.Min(_caret, _anchor);
        var end = Math.Max(_caret, _anchor);
        for (var line = _firstLine; line < visible; line++)
        {
            var visual = _visual[line];
            var top = (line - _firstLine) * height;
            var baseline = top + _font.GetAscent(UIScale);
            if (visual.Start == _lines[visual.Line])
                handle.DrawString(_font, new Vector2(4 * UIScale, baseline), (visual.Line + 1).ToString(CultureInfo.InvariantCulture), Color.Gray);
            var x = 52 * UIScale;
            var source = _text[visual.Start..visual.End];
            foreach (var element in NCMetaGraphemes.Elements(source))
            {
                var offset = visual.Start + element.Index;
                var width = Measure(element.Text);
                if (offset >= start && offset < end)
                    handle.DrawRect(new UIBox2(x, top, x + width, top + height), Color.FromHex("#285076"));
                if (offset == _caret && HasKeyboardFocus())
                    x += DrawInputCaret(handle, x, top, height, baseline);
                handle.DrawString(_font, new Vector2(x, baseline), element.Text.AsSpan(), UIScale, SyntaxColor(offset));
                x += width;
                if (x > PixelSize.X)
                    break;
            }
            if (_caret == visual.End && line == CaretVisualLine() && HasKeyboardFocus())
                DrawInputCaret(handle, x, top, height, baseline);
        }
    }

    private float DrawInputCaret(DrawingHandleScreen handle, float x, float top, float height, float baseline)
    {
        var width = Measure(_composition);
        var cursor = x;
        if (_composition.Length > 0)
        {
            handle.DrawString(_font, new Vector2(x, baseline), _composition.AsSpan(), UIScale, Color.Yellow);
            handle.DrawLine(new Vector2(x, top + height - 1), new Vector2(x + width, top + height - 1), Color.Yellow);
            cursor += Measure(_composition[.._compositionCaret]);
        }
        handle.DrawLine(new Vector2(cursor, top), new Vector2(cursor, top + height), Color.White);
        var anchor = Math.Clamp(cursor, 0, Math.Max(0, PixelSize.X - 1));
        var rect = new UIBox2(anchor, top, anchor + 1, top + height).Translated(GlobalPixelPosition);
        Root?.Window?.TextInputSetRect((UIBox2i) rect, 0);
        return width;
    }

    private static bool IsValidUtf16(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i]))
                continue;
            if (!char.IsHighSurrogate(text[i]) || ++i >= text.Length || !char.IsLowSurrogate(text[i]))
                return false;
        }
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Visible = false;
            ReleaseKeyboardFocus();
            _inputGeneration++;
            _text = _composition = "";
            _rope = Rope.Leaf.Empty;
            _undo.Clear();
            _redo.Clear();
            _syntax.Clear();
            _lexLines.Clear();
            _layoutLines.Clear();
            _lines.Clear();
            _visual.Clear();
            _graphemes = [];
            SourceChanged = null;
            CaretChanged = null;
            ClipboardUnavailable = null;
        }
        base.Dispose(disposing);
    }
}
