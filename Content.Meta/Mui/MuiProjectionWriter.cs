// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Content.Meta.Bytecode;
using Content.Meta.Runtime;
using Content.Meta.Verification;

namespace Content.Meta.Mui;

public enum MuiCodecProgress : byte { Working, Complete, Invalid }
public sealed class MuiProjectionResult
{
    public string Json { get; }
    public ImmutableDictionary<string, string> Fields { get; }
    internal MuiProjectionResult(string json, ImmutableDictionary<string, string> fields) { Json = json; Fields = fields; }
}

/// <summary>
/// A typed, bounded projection. The owner must keep the VM's LastResult barrier until completion/disposal.
/// Each step writes one DTO node, property, container boundary or bounded encoded-field copy.
/// </summary>
public sealed class MuiProjectionWriter : IDisposable
{
    private enum Kind : byte { Node, RecordField, ArrayItem, MapItem, EndObject, EndArray, CaptureField, Finish }
    private readonly record struct Work(Kind Kind, int Type = 0, MetaValue Value = default, int Depth = 0,
        int Index = 0, string? Name = null, int Start = 0, MuiField? Field = null);
    private readonly MetaProcess _process;
    private readonly VerifiedArtifact _artifact;
    private readonly IReadOnlyDictionary<string, MuiField> _schema;
    private readonly Stack<Work> _work = [];
    private readonly Dictionary<string, string> _fields = new(StringComparer.Ordinal);
    private readonly Output _bytes = new();
    private readonly Utf8JsonWriter _writer;
    private int _nodes = MuiLimits.Nodes;
    public MuiCodecProgress Progress { get; private set; }
    public MuiProjectionResult? Result { get; private set; }

    public MuiProjectionWriter(MetaProcess process, VerifiedArtifact artifact, int type, MetaValue value,
        IReadOnlyDictionary<string, MuiField> schema)
    {
        _process = process; _artifact = artifact; _schema = schema; _writer = new(_bytes);
        _work.Push(new(Kind.Finish)); _work.Push(new(Kind.Node, type, value));
    }

    private int NextBytes
    {
        get
        {
            if (!_work.TryPeek(out var work)) return 0;
            if (work.Kind == Kind.Finish) return checked((int) _bytes.Length);
            if (work.Kind == Kind.CaptureField) return checked((int) _bytes.Length - work.Start);
            if (work.Kind == Kind.RecordField)
            {
                var type = _artifact.GetType(work.Type);
                return work.Index < type.Fields.Length ? 8 + type.Fields[work.Index].Name.Length * 6 : 1;
            }
            if (work.Kind == Kind.MapItem && work.Index < _process.GetMapCount(work.Value))
                return 8 + _process.GetString(_process.GetMapEntry(work.Value, work.Index).Key).Length * 6;
            if (work.Kind == Kind.Node && _artifact.GetType(work.Type).Kind == MetaTypeKind.String)
                return 8 + _process.GetString(work.Value).Length * 6;
            return 64;
        }
    }

    public int NextGas => 16 + (NextBytes + 15) / 16;
    /// <summary>Conservative native retained/transient memory bound to reserve before taking the next step.</summary>
    public long NextReservationBytes => 8192L + _bytes.Length * 6 + NextBytes * 8L + _work.Count * 128L + _fields.Count * 256L;

    public MuiCodecProgress Step()
    {
        if (Progress != MuiCodecProgress.Working) return Progress;
        try
        {
            if (!_work.TryPop(out var work)) throw new FormatException("Missing projection completion.");
            switch (work.Kind)
            {
                case Kind.Node: WriteNode(work); break;
                case Kind.RecordField: WriteField(work); break;
                case Kind.ArrayItem:
                    var count = _process.GetSequenceCount(work.Value);
                    if (work.Index < count)
                    {
                        _work.Push(work with { Index = work.Index + 1 });
                        _work.Push(new(Kind.Node, _artifact.GetType(work.Type).Args[0], _process.GetSequenceElement(work.Value, work.Index), work.Depth + 1));
                    }
                    break;
                case Kind.MapItem:
                    if (work.Index < _process.GetMapCount(work.Value))
                    {
                        var item = _process.GetMapEntry(work.Value, work.Index); var key = _process.GetString(item.Key);
                        if (key.Length > 128) throw new FormatException("Map property budget.");
                        _writer.WritePropertyName(key);
                        _work.Push(work with { Index = work.Index + 1 });
                        _work.Push(new(Kind.Node, _artifact.GetType(work.Type).Args[1], item.Value, work.Depth + 1));
                    }
                    break;
                case Kind.EndObject: _writer.WriteEndObject(); break;
                case Kind.EndArray: _writer.WriteEndArray(); break;
                case Kind.CaptureField:
                    _writer.Flush();
                    _fields.Add(work.Name!, Encoding.UTF8.GetString(_bytes.Written[work.Start..]));
                    break;
                case Kind.Finish:
                    _writer.Flush();
                    Result = new(Encoding.UTF8.GetString(_bytes.Written), _fields.ToImmutableDictionary(StringComparer.Ordinal));
                    Progress = MuiCodecProgress.Complete;
                    break;
            }
            _writer.Flush();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { Progress = MuiCodecProgress.Invalid; _work.Clear(); }
        return Progress;
    }

    private void WriteField(Work work)
    {
        var type = _artifact.GetType(work.Type);
        if (work.Index >= type.Fields.Length) return;
        var field = type.Fields[work.Index];
        if (field.Name.Length > 128) throw new FormatException("Property budget.");
        MuiField? schema = null;
        if (work.Depth == 0 && !_schema.TryGetValue(field.Name, out schema)) throw new FormatException("Unknown projection property.");
        _writer.WritePropertyName(field.Name); _writer.Flush();
        _work.Push(work with { Index = work.Index + 1 });
        if (work.Depth == 0) _work.Push(new(Kind.CaptureField, Name: field.Name, Start: checked((int) _bytes.Length)));
        _work.Push(new(Kind.Node, field.TypeId, _process.GetField(work.Value, work.Index), work.Depth + 1, Field: schema));
    }

    private void WriteNode(Work work)
    {
        if (work.Depth >= 16 || --_nodes < 0 || work.Value.TypeId != work.Type) throw new FormatException("Projection node budget/type.");
        var type = _artifact.GetType(work.Type); var value = work.Value;
        if (work.Depth == 0 && (type.Kind != MetaTypeKind.Struct || type.Fields.Length != _schema.Count)) throw new FormatException("Projection root/schema.");
        switch (type.Kind)
        {
            case MetaTypeKind.Option:
                if (_process.GetVariantTag(value) == 0)
                { if (work.Field is { Required: true }) throw new FormatException("Required value is absent."); _writer.WriteNullValue(); }
                else _work.Push(new(Kind.Node, type.Args[0], _process.GetVariantPayload(value), work.Depth + 1, Field: work.Field));
                break;
            case MetaTypeKind.Bool:
                Check(work.Field, MuiValueKind.Boolean); _writer.WriteBooleanValue(value.AsBool()); break;
            case MetaTypeKind.Byte:
                Check(work.Field, MuiValueKind.Integer, value.Payload); _writer.WriteNumberValue(value.Payload); break;
            case MetaTypeKind.Int:
                Check(work.Field, MuiValueKind.Integer, value.AsInt()); _writer.WriteNumberValue(value.AsInt()); break;
            case MetaTypeKind.Float:
                var number = value.AsFloat();
                if (!double.IsFinite(number)) throw new FormatException("Nonfinite projection.");
                Check(work.Field, MuiValueKind.Number, number); _writer.WriteNumberValue(number); break;
            case MetaTypeKind.String:
                var text = _process.GetString(value);
                Check(work.Field, MuiValueKind.String, length: text.Length);
                if (text.Length > MuiLimits.StringLength) throw new FormatException("String budget.");
                _writer.WriteStringValue(text); break;
            case MetaTypeKind.Enum:
                var name = type.Variants.Single(item => item.Tag == _process.GetVariantTag(value)).Name;
                Check(work.Field, MuiValueKind.String, length: name.Length); _writer.WriteStringValue(name); break;
            case MetaTypeKind.Struct:
                Check(work.Field, MuiValueKind.Object); _writer.WriteStartObject();
                _work.Push(new(Kind.EndObject)); _work.Push(new(Kind.RecordField, work.Type, value, work.Depth)); break;
            case MetaTypeKind.Array:
            case MetaTypeKind.List:
                var count = _process.GetSequenceCount(value);
                if (count > MuiLimits.ChartPoints) throw new FormatException("Array budget.");
                Check(work.Field, MuiValueKind.Array, length: count); _writer.WriteStartArray();
                _work.Push(new(Kind.EndArray)); _work.Push(new(Kind.ArrayItem, work.Type, value, work.Depth)); break;
            case MetaTypeKind.Map:
                if (_artifact.GetType(type.Args[0]).Kind != MetaTypeKind.String || _process.GetMapCount(value) > 256) throw new FormatException("Map budget/type.");
                Check(work.Field, MuiValueKind.Object); _writer.WriteStartObject();
                _work.Push(new(Kind.EndObject)); _work.Push(new(Kind.MapItem, work.Type, value, work.Depth)); break;
            default: throw new FormatException("Native authority is not a serializable projection.");
        }
    }

    private static void Check(MuiField? field, MuiValueKind kind, double? number = null, int length = 0)
    {
        if (field == null) return;
        if (field.Kind != kind && !(kind == MuiValueKind.Integer && field.Kind == MuiValueKind.Number) ||
            kind is MuiValueKind.String or MuiValueKind.Array && length > field.MaxLength ||
            number is { } value && (field.Minimum is { } min && value < min || field.Maximum is { } max && value > max))
            throw new FormatException("Projection field constraint.");
    }

    public void Dispose() { _writer.Dispose(); _bytes.Dispose(); _work.Clear(); _fields.Clear(); }

    private sealed class Output : Stream
    {
        private readonly MemoryStream _buffer = new();
        public ReadOnlySpan<byte> Written => _buffer.GetBuffer().AsSpan(0, checked((int) _buffer.Length));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        { if (buffer.Length > MuiLimits.InitialStateBytes - Length) throw new IOException("Projection output budget."); _buffer.Write(buffer); }
        protected override void Dispose(bool disposing) { if (disposing) _buffer.Dispose(); base.Dispose(disposing); }
    }
}
