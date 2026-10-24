// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using System.Text;

namespace Content.Meta.Language;

public readonly record struct SourceSpan(string FileId, string Revision, int StartUtf8Byte, int EndUtf8Byte)
{
    public int Length => EndUtf8Byte - StartUtf8Byte;
    public static SourceSpan Cover(SourceSpan first, SourceSpan last) => first with { EndUtf8Byte = last.EndUtf8Byte };
}

public readonly record struct SourcePosition(int Line, int ScalarColumn, int Utf16Column);
public enum MetaDiagnosticSeverity : byte { Warning, Error }
public sealed record MetaRelatedLocation(SourceSpan Span, string MessageKey);
public sealed record MetaDiagnostic(string Code, MetaDiagnosticSeverity Severity, string FileId, string SourceRevision,
    int StartUtf8Byte, int EndUtf8Byte, string MessageKey, IReadOnlyList<string> Arguments,
    IReadOnlyList<MetaRelatedLocation> Related)
{
    public static MetaDiagnostic Error(string code, SourceSpan span, params string[] args) => new(code,
        MetaDiagnosticSeverity.Error, span.FileId, span.Revision, span.StartUtf8Byte, span.EndUtf8Byte,
        "meta-diagnostic-" + code.ToLowerInvariant(), args.Take(4).Select(a => Truncate(a, 128)).ToArray(), Array.Empty<MetaRelatedLocation>());
    private static string Truncate(string value, int maxBytes)
    {
        var length = 0; var chars = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (length + rune.Utf8SequenceLength > maxBytes) break;
            length += rune.Utf8SequenceLength; chars += rune.Utf16SequenceLength;
        }
        return value[..chars];
    }
}

/// <summary>Immutable normalized source. All public offsets are UTF-8 byte offsets, never CLR string indices.</summary>
public sealed class SourceText
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly int[] _utf16ToByte;
    private readonly int[] _lineStarts;
    public string FileId { get; }
    public string Revision { get; }
    public string Text { get; }
    public ReadOnlyMemory<byte> Utf8 { get; }
    public IReadOnlyList<int> LineStarts => _lineStarts;
    public SourceSpan Span => new(FileId, Revision, 0, Utf8.Length);

    public SourceText(string fileId, string text) : this(fileId, StrictUtf8.GetBytes(text)) { }

    public SourceText(string fileId, ReadOnlySpan<byte> utf8)
    {
        FileId = fileId;
        var decoded = StrictUtf8.GetString(utf8);
        if (decoded.Length > 0 && decoded[0] == '\uFEFF')
            decoded = decoded[1..];
        Text = decoded.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var bytes = StrictUtf8.GetBytes(Text);
        Utf8 = bytes;
        Revision = Convert.ToHexStringLower(SHA256.HashData(bytes));
        _utf16ToByte = new int[Text.Length + 1];
        var lines = new List<int> { 0 };
        var offset = 0;
        for (var i = 0; i < Text.Length;)
        {
            var rune = Rune.GetRuneAt(Text, i);
            _utf16ToByte[i] = offset;
            if (rune.Utf16SequenceLength == 2)
                _utf16ToByte[i + 1] = -1;
            offset += rune.Utf8SequenceLength;
            i += rune.Utf16SequenceLength;
            _utf16ToByte[i] = offset;
            if (rune.Value == '\n')
                lines.Add(offset);
        }
        _lineStarts = lines.ToArray();
    }

    public int ByteOffset(int utf16Offset)
    {
        if ((uint) utf16Offset >= _utf16ToByte.Length || _utf16ToByte[utf16Offset] < 0)
            throw new ArgumentOutOfRangeException(nameof(utf16Offset));
        return _utf16ToByte[utf16Offset];
    }

    public bool IsScalarBoundary(int byteOffset) => byteOffset >= 0 && byteOffset <= Utf8.Length &&
        (byteOffset == Utf8.Length || (Utf8.Span[byteOffset] & 0xC0) != 0x80);

    public int Utf16Offset(int byteOffset)
    {
        if (!IsScalarBoundary(byteOffset)) throw new ArgumentOutOfRangeException(nameof(byteOffset));
        return StrictUtf8.GetCharCount(Utf8.Span[..byteOffset]);
    }

    public SourceSpan GetSpan(int startUtf16, int endUtf16) => new(FileId, Revision, ByteOffset(startUtf16), ByteOffset(endUtf16));

    public SourcePosition GetPosition(int byteOffset)
    {
        if (!IsScalarBoundary(byteOffset))
            throw new ArgumentOutOfRangeException(nameof(byteOffset));
        var index = Array.BinarySearch(_lineStarts, byteOffset);
        var line = index >= 0 ? index : ~index - 1;
        var prefix = StrictUtf8.GetString(Utf8.Span[_lineStarts[line]..byteOffset]);
        return new SourcePosition(line, prefix.EnumerateRunes().Count(), prefix.Length);
    }
}

public sealed record MetaCompilerLimits(int MaxFileBytes = 256 * 1024, int MaxProjectBytes = 1024 * 1024,
    int MaxFiles = 64, int MaxTokens = 200000, int MaxNodes = 100000, int MaxSyntaxDepth = 128,
    int MaxTypeDepth = 16, int MaxConstantSteps = 50000, int MaxDiagnostics = 100, int MaxStringBytes = 65536);

internal sealed class CompilationBudget
{
    public MetaCompilerLimits Limits { get; }
    public CancellationToken Cancellation { get; }
    public List<MetaDiagnostic> Diagnostics { get; } = new();
    private int _tokens;
    private int _nodes;
    private int _diagnosticBytes;
    public int ConstantSteps;
    public CompilationBudget(MetaCompilerLimits limits, CancellationToken cancellation) => (Limits, Cancellation) = (limits, cancellation);
    public void Token(SourceSpan span) { Cancellation.ThrowIfCancellationRequested(); if (++_tokens > Limits.MaxTokens) throw Limit("E401", span); }
    public void Node(SourceSpan span) { Cancellation.ThrowIfCancellationRequested(); if (++_nodes > Limits.MaxNodes) throw Limit("E402", span); }
    public void Report(string code, SourceSpan span, params string[] args)
    {
        var diagnostic = MetaDiagnostic.Error(code, span, args);
        var size = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(diagnostic).Length;
        if (Diagnostics.Count < Limits.MaxDiagnostics && _diagnosticBytes + size <= 64 * 1024)
        {
            Diagnostics.Add(diagnostic); _diagnosticBytes += size;
        }
        else
            throw new MetaCompilationAbortException();
    }
    public MetaCompilationAbortException Limit(string code, SourceSpan span) { Report(code, span); return new MetaCompilationAbortException(); }
}

internal sealed class MetaCompilationAbortException : Exception;
