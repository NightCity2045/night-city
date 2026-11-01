// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.Text;

namespace Content.Meta.Language;

public enum MetaTokenKind : byte { End, Identifier, Keyword, Integer, Float, String, Symbol, Invalid }
public sealed record MetaTrivia(string Text, SourceSpan Span, bool IsComment);
public sealed record MetaToken(MetaTokenKind Kind, string Text, SourceSpan Span, object? Value,
    IReadOnlyList<MetaTrivia> LeadingTrivia);
public sealed record MetaLexResult(SourceText Source, IReadOnlyList<MetaToken> Tokens, IReadOnlyList<MetaDiagnostic> Diagnostics);

public static class MetaLexer
{
    internal static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "module", "import", "as", "pub", "link", "fn", "async", "watch", "let", "var", "const", "struct", "enum", "if", "else",
        "while", "for", "in", "return", "break", "continue", "match", "some", "none", "ok", "err", "true", "false", "await", "yield", "new",
        "void", "bool", "byte", "int", "float", "string", "array", "list", "map", "option", "result",
        "null", "class", "throw", "catch", "spawn", "eval", "unsafe", "typeof", "using", "_"
    };

    public static MetaLexResult Lex(SourceText source, MetaCompilerLimits? limits = null, CancellationToken cancellation = default)
    {
        var budget = new CompilationBudget(limits ?? new MetaCompilerLimits(), cancellation);
        var tokens = new List<MetaToken>();
        try { Scan(source, budget, tokens); }
        catch (MetaCompilationAbortException) { }
        return new(source, tokens, budget.Diagnostics);
    }

    internal static void Scan(SourceText source, CompilationBudget budget, List<MetaToken> output)
    {
        if (source.Utf8.Length > budget.Limits.MaxFileBytes)
            throw budget.Limit("E400", source.Span);
        var text = source.Text;
        var i = 0;
        while (true)
        {
            var trivia = new List<MetaTrivia>();
            while (i < text.Length)
            {
                var start = i;
                var comment = false;
                if (text[i] is ' ' or '\t' or '\n' or '\r')
                {
                    while (i < text.Length && text[i] is ' ' or '\t' or '\n' or '\r') i++;
                }
                else if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
                {
                    comment = true;
                    i += 2;
                    while (i < text.Length && text[i] != '\n') i += char.IsSurrogatePair(text, i) ? 2 : 1;
                }
                else if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
                {
                    comment = true;
                    i += 2;
                    while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i += char.IsSurrogatePair(text, i) ? 2 : 1;
                    if (i + 1 >= text.Length)
                    {
                        i = text.Length;
                        budget.Report("E101", source.GetSpan(start, i), "unterminated-comment");
                    }
                    else i += 2;
                }
                else break;
                trivia.Add(new(text[start..i], source.GetSpan(start, i), comment));
            }
            var begin = i;
            if (i == text.Length)
            {
                output.Add(new(MetaTokenKind.End, "", source.GetSpan(i, i), null, trivia));
                return;
            }
            var kind = MetaTokenKind.Symbol;
            object? value = null;
            var c = text[i++];
            if (IsIdentifierStart(c))
            {
                while (i < text.Length && IsIdentifierPart(text[i])) i++;
                kind = Keywords.Contains(text[begin..i]) ? MetaTokenKind.Keyword : MetaTokenKind.Identifier;
            }
            else if (char.IsAsciiDigit(c))
            {
                kind = MetaTokenKind.Integer;
                if (c == '0' && i < text.Length && text[i] == 'x')
                {
                    i++;
                    var digits = i;
                    while (i < text.Length && (char.IsAsciiHexDigit(text[i]) || text[i] == '_')) i++;
                    if (!ValidDigits(text[digits..i], true)) budget.Report("E105", source.GetSpan(begin, i), "integer");
                }
                else
                {
                    while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '_')) i++;
                    if (!ValidDigits(text[begin..i], false)) budget.Report("E105", source.GetSpan(begin, i), "integer");
                    if (i < text.Length && text[i] == '.')
                    {
                        kind = MetaTokenKind.Float;
                        var fraction = ++i;
                        while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '_')) i++;
                        if (!ValidDigits(text[fraction..i], false)) budget.Report("E105", source.GetSpan(begin, i), "fraction");
                    }
                    if (i < text.Length && text[i] is 'e' or 'E')
                    {
                        kind = MetaTokenKind.Float;
                        i++;
                        if (i < text.Length && text[i] is '+' or '-') i++;
                        var exponent = i;
                        while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '_')) i++;
                        if (!ValidDigits(text[exponent..i], false)) budget.Report("E105", source.GetSpan(begin, i), "exponent");
                    }
                }
                value = text[begin..i].Replace("_", "", StringComparison.Ordinal);
                if (i < text.Length && IsIdentifierStart(text[i])) budget.Report("E105", source.GetSpan(begin, i), "numeric-suffix");
            }
            else if (c == '"')
            {
                kind = MetaTokenKind.String;
                var builder = new StringBuilder();
                var closed = false;
                while (i < text.Length)
                {
                    c = text[i++];
                    if (c == '"') { closed = true; break; }
                    if (char.IsControl(c))
                    {
                        budget.Report("E106", source.GetSpan(begin, i), "string-control");
                        if (c == '\n') break;
                        continue;
                    }
                    if (c != '\\')
                    {
                        builder.Append(c);
                        if (char.IsHighSurrogate(c)) builder.Append(text[i++]);
                        continue;
                    }
                    if (i >= text.Length) break;
                    c = text[i++];
                    switch (c)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (i < text.Length && text[i] == '{')
                            {
                                var scalarStart = ++i;
                                while (i < text.Length && char.IsAsciiHexDigit(text[i]) && i - scalarStart < 7) i++;
                                if (i < text.Length && text[i] == '}' && i > scalarStart && i - scalarStart <= 6 &&
                                    int.TryParse(text[scalarStart..i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var scalar) && Rune.IsValid(scalar))
                                {
                                    builder.Append(new Rune(scalar)); i++; break;
                                }
                            }
                            budget.Report("E107", source.GetSpan(begin, i), "unicode-escape");
                            break;
                        default: budget.Report("E107", source.GetSpan(begin, i), "escape"); break;
                    }
                    if (builder.Length > budget.Limits.MaxStringBytes) throw budget.Limit("E405", source.GetSpan(begin, i));
                }
                if (!closed) budget.Report("E106", source.GetSpan(begin, i), "unterminated-string");
                value = builder.ToString();
                if (Encoding.UTF8.GetByteCount((string) value) > budget.Limits.MaxStringBytes) throw budget.Limit("E405", source.GetSpan(begin, i));
            }
            else
            {
                if (char.IsHighSurrogate(c)) i++;
                var two = i < text.Length ? text[begin..Math.Min(begin + 2, text.Length)] : "";
                if (two is "->" or "=>" or "==" or "!=" or "<=" or ">=" or "&&" or "||" or "<<" or ">>") i++;
                else if (!"{}()[];,:.+-*/%!~&|^<>=".Contains(c, StringComparison.Ordinal))
                {
                    kind = MetaTokenKind.Invalid;
                    budget.Report("E100", source.GetSpan(begin, i), "invalid-character");
                }
            }
            var span = source.GetSpan(begin, i);
            budget.Token(span);
            output.Add(new(kind, text[begin..i], span, value, trivia));
        }
    }

    private static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c == '_';
    private static bool IsIdentifierPart(char c) => IsIdentifierStart(c) || char.IsAsciiDigit(c);
    private static bool ValidDigits(string text, bool hex) => text.Length != 0 && text[0] != '_' && text[^1] != '_' &&
        !text.Contains("__", StringComparison.Ordinal) && text.All(c => c == '_' || (hex ? char.IsAsciiHexDigit(c) : char.IsAsciiDigit(c)));
}
