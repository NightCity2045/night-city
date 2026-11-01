// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using System.Text.Json;
using Content.Meta.Bytecode;
using Content.Meta.Runtime;
using Content.Meta.Verification;

namespace Content.Meta.Mui;

/// <summary>Incremental typed input construction; all intermediate guest values have charged process roots.</summary>
public sealed class MuiInputReader : IDisposable
{
    private sealed class Frame(int type, JsonElement element)
    {
        public readonly int Type = type;
        public readonly JsonElement Element = element;
        public bool Entered;
        public int Index;
        public MetaValue[] Values = [];
        public MetaValue[] Keys = [];
        public JsonElement.ArrayEnumerator Array;
        public JsonElement.ObjectEnumerator Map;
    }
    private readonly MetaProcess _process;
    private readonly VerifiedArtifact _artifact;
    private readonly JsonDocument _document;
    private readonly MetaProcess.HostRootLease _roots;
    private readonly Stack<Frame> _frames = [];
    public MuiCodecProgress Progress { get; private set; }
    public MetaValue Result { get; private set; }
    public long ReservationBytes { get; }

    private MuiInputReader(MetaProcess process, VerifiedArtifact artifact, int type, JsonDocument document,
        MetaProcess.HostRootLease roots, int nodes, int jsonLength)
    {
        _process = process; _artifact = artifact; _document = document; _roots = roots;
        ReservationBytes = 4096L + jsonLength * 8L + nodes * 128L;
        _frames.Push(new(type, document.RootElement));
    }

    /// <summary>One bounded validation primitive over at most ActionBytes; caller quotes/reserves it before entry.</summary>
    public static bool TryCreate(MetaProcess process, VerifiedArtifact artifact, int type, string json,
        IReadOnlyDictionary<string, MuiField> schema, out MuiInputReader reader)
    {
        reader = null!; JsonDocument? document = null;
        try
        {
            if (!MuiDataValidator.TryValidate(json, schema, MuiLimits.ActionBytes, out _)) return false;
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var remaining = MuiLimits.Nodes;
            if (!MuiDtoCodec.CanRead(document.RootElement, artifact, type, 0, ref remaining)) return false;
            var nodes = MuiLimits.Nodes - remaining;
            if (!process.TryCreateHostRoots(Math.Max(1, nodes), out var roots)) return false;
            reader = new(process, artifact, type, document, roots, nodes, json.Length); document = null; return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { return false; }
        finally { document?.Dispose(); }
    }

    public int NextGas
    {
        get
        {
            if (!_frames.TryPeek(out var frame)) return 8;
            var type = _artifact.GetType(frame.Type);
            if (!frame.Entered && type.Kind == MetaTypeKind.String) return 24 + frame.Element.GetString()!.Length;
            if (frame.Entered && frame.Index == frame.Values.Length) return 32 + frame.Values.Length * (type.Kind == MetaTypeKind.Map ? 16 : 4);
            return 24 + (type.Kind == MetaTypeKind.Map ? 128 : 0);
        }
    }

    public MuiCodecProgress Step()
    {
        if (Progress != MuiCodecProgress.Working) return Progress;
        try
        {
            var frame = _frames.Peek(); var type = _artifact.GetType(frame.Type);
            if (!frame.Entered)
            {
                switch (type.Kind)
                {
                    case MetaTypeKind.Bool: Finish(MetaValue.Bool(frame.Element.GetBoolean())); return Progress;
                    case MetaTypeKind.Byte: Finish(MetaValue.Byte(frame.Element.GetByte())); return Progress;
                    case MetaTypeKind.Int: Finish(MetaValue.Int(frame.Element.GetInt64())); return Progress;
                    case MetaTypeKind.Float: Finish(MetaValue.Float(frame.Element.GetDouble())); return Progress;
                    case MetaTypeKind.String: Finish(_process.CreateString(frame.Element.GetString()!)); return Progress;
                    case MetaTypeKind.Enum:
                        Finish(_process.CreateEnum(frame.Type, type.Variants.Single(value => value.Name == frame.Element.GetString()).Tag)); return Progress;
                    case MetaTypeKind.Option:
                        if (frame.Element.ValueKind == JsonValueKind.Null) { Finish(_process.CreateVariant(frame.Type, 0)); return Progress; }
                        frame.Values = new MetaValue[1]; break;
                    case MetaTypeKind.Struct: frame.Values = new MetaValue[type.Fields.Length]; break;
                    case MetaTypeKind.Array:
                    case MetaTypeKind.List: frame.Values = new MetaValue[frame.Element.GetArrayLength()]; frame.Array = frame.Element.EnumerateArray(); break;
                    case MetaTypeKind.Map:
                        var count = frame.Element.EnumerateObject().Count(); frame.Values = new MetaValue[count]; frame.Keys = new MetaValue[count];
                        frame.Map = frame.Element.EnumerateObject(); break;
                    default: throw new FormatException("Unsupported input authority type.");
                }
                frame.Entered = true; return Progress;
            }
            if (frame.Index == frame.Values.Length)
            {
                var result = type.Kind switch
                {
                    MetaTypeKind.Struct => _process.CreateRecord(frame.Type, frame.Values),
                    MetaTypeKind.Option => _process.CreateVariant(frame.Type, 1, frame.Values[0]),
                    MetaTypeKind.Array => _process.CreateArray(frame.Type, frame.Values),
                    MetaTypeKind.List => _process.CreateList(frame.Type, frame.Values),
                    MetaTypeKind.Map => _process.CreateMap(frame.Type, frame.Keys.Select((key, index) => new KeyValuePair<MetaValue, MetaValue>(key, frame.Values[index])).ToArray()),
                    _ => throw new FormatException("Invalid input frame.")
                };
                Finish(result); return Progress;
            }
            switch (type.Kind)
            {
                case MetaTypeKind.Struct:
                    var field = type.Fields[frame.Index];
                    if (frame.Element.TryGetProperty(field.Name, out var child)) _frames.Push(new(field.TypeId, child));
                    else
                    {
                        var none = _process.CreateVariant(field.TypeId, 0);
                        Pin(none); frame.Values[frame.Index++] = none;
                    }
                    break;
                case MetaTypeKind.Option: _frames.Push(new(type.Args[0], frame.Element)); break;
                case MetaTypeKind.Array:
                case MetaTypeKind.List:
                    if (!frame.Array.MoveNext()) throw new FormatException("Input array changed.");
                    _frames.Push(new(type.Args[0], frame.Array.Current)); break;
                case MetaTypeKind.Map:
                    if (!frame.Map.MoveNext()) throw new FormatException("Input map changed.");
                    var key = _process.CreateString(frame.Map.Current.Name); Pin(key); frame.Keys[frame.Index] = key;
                    _frames.Push(new(type.Args[1], frame.Map.Current.Value)); break;
            }
        }
        catch (MetaTrap trap) { _process.FailHostConstruction(trap); Progress = MuiCodecProgress.Invalid; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { Progress = MuiCodecProgress.Invalid; }
        return Progress;
    }

    private void Pin(MetaValue value)
    { if (!_roots.TryAdd(value)) throw new FormatException("Input root budget/state changed."); }
    private void Finish(MetaValue value)
    {
        Pin(value); _frames.Pop();
        if (_frames.TryPeek(out var parent)) parent.Values[parent.Index++] = value;
        else { Result = value; Progress = MuiCodecProgress.Complete; }
    }
    public void Dispose() { _roots.Dispose(); _document.Dispose(); _frames.Clear(); }
}
