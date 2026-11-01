// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text;
using System.Text.Json;
using Content.Meta.Bytecode;
using Content.Meta.Runtime;
using Content.Meta.Verification;

namespace Content.Meta.Mui;

/// <summary>Explicit DTO translation; opaque references and capabilities never enter JSON.</summary>
public static class MuiDtoCodec
{
    public static bool TryProject(MetaProcess process, VerifiedArtifact artifact, int typeId, MetaValue value,
        IReadOnlyDictionary<string, MuiField> schema, out string json)
    {
        json = "";
        try
        {
            using var bytes = new BoundedStream(MuiLimits.InitialStateBytes);
            using var writer = new Utf8JsonWriter(bytes);
            var budget = MuiLimits.Nodes;
            Write(writer, process, artifact, typeId, value, 0, ref budget);
            writer.Flush();
            var candidate = Encoding.UTF8.GetString(bytes.ToArray());
            if (!MuiDataValidator.TryValidate(candidate, schema, MuiLimits.InitialStateBytes, out _))
                return false;
            json = candidate;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
    }

    public static MetaValue DecodeInput(MetaProcess process, VerifiedArtifact artifact, int typeId, string json,
        IReadOnlyDictionary<string, MuiField> schema)
    {
        if (!CanDecodeInput(artifact, typeId, json, schema))
            throw new FormatException("MUI action DTO failed schema validation.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var budget = MuiLimits.Nodes;
        return Read(document.RootElement, process, artifact, typeId, 0, ref budget);
    }

    public static bool CanDecodeInput(VerifiedArtifact artifact, int typeId, string json, IReadOnlyDictionary<string, MuiField> schema)
    {
        if (!MuiDataValidator.TryValidate(json, schema, MuiLimits.ActionBytes, out _))
            return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var budget = MuiLimits.Nodes;
            return CanRead(document.RootElement, artifact, typeId, 0, ref budget);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { return false; }
    }

    internal static bool CanRead(JsonElement element, VerifiedArtifact artifact, int typeId, int depth, ref int budget)
    {
        if (depth >= 16 || --budget < 0 || typeId < 0 || typeId >= artifact.TypeCount)
            return false;
        var type = artifact.GetType(typeId);
        switch (type.Kind)
        {
            case MetaTypeKind.Bool: return element.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case MetaTypeKind.Byte: return element.ValueKind == JsonValueKind.Number && element.TryGetByte(out _);
            case MetaTypeKind.Int: return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out _);
            case MetaTypeKind.Float: return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number) && double.IsFinite(number);
            case MetaTypeKind.String: return element.ValueKind == JsonValueKind.String && element.GetString()!.Length <= MuiLimits.StringLength;
            case MetaTypeKind.Enum: return element.ValueKind == JsonValueKind.String && type.Variants.Any(x => x.Name == element.GetString());
            case MetaTypeKind.Option:
                return element.ValueKind == JsonValueKind.Null || CanRead(element, artifact, type.Args[0], depth + 1, ref budget);
            case MetaTypeKind.Struct:
                if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Any(x => !type.Fields.Any(f => f.Name == x.Name)))
                    return false;
                foreach (var field in type.Fields)
                {
                    if (!element.TryGetProperty(field.Name, out var value))
                    {
                        if (artifact.GetType(field.TypeId).Kind != MetaTypeKind.Option || depth + 1 >= 16 || --budget < 0)
                            return false;
                    }
                    else if (!CanRead(value, artifact, field.TypeId, depth + 1, ref budget))
                        return false;
                }
                return true;
            case MetaTypeKind.Array:
            case MetaTypeKind.List:
                if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MuiLimits.ChartPoints)
                    return false;
                foreach (var item in element.EnumerateArray())
                    if (!CanRead(item, artifact, type.Args[0], depth + 1, ref budget))
                        return false;
                return true;
            case MetaTypeKind.Map:
                if (element.ValueKind != JsonValueKind.Object || artifact.GetType(type.Args[0]).Kind != MetaTypeKind.String)
                    return false;
                var entries = 0;
                foreach (var entry in element.EnumerateObject())
                    if (++entries > 256 || entry.Name.Length > 128 || --budget < 0 || !CanRead(entry.Value, artifact, type.Args[1], depth + 1, ref budget))
                        return false;
                return true;
            default: return false;
        }
    }

    private static void Write(Utf8JsonWriter writer, MetaProcess process, VerifiedArtifact artifact, int typeId,
        MetaValue value, int depth, ref int budget)
    {
        if (depth >= 16 || --budget < 0 || value.TypeId != typeId)
            throw new FormatException("MUI projection budget or type mismatch.");
        var type = artifact.GetType(typeId);
        switch (type.Kind)
        {
            case MetaTypeKind.Bool: writer.WriteBooleanValue(value.AsBool()); break;
            case MetaTypeKind.Byte: writer.WriteNumberValue(value.Payload); break;
            case MetaTypeKind.Int: writer.WriteNumberValue(value.AsInt()); break;
            case MetaTypeKind.Float: writer.WriteNumberValue(value.AsFloat()); break;
            case MetaTypeKind.String:
                var text = process.GetString(value);
                if (text.Length > MuiLimits.StringLength)
                    throw new FormatException("MUI string budget.");
                writer.WriteStringValue(text);
                break;
            case MetaTypeKind.Enum:
                writer.WriteStringValue(type.Variants.Single(x => x.Tag == process.GetVariantTag(value)).Name);
                break;
            case MetaTypeKind.Struct:
                writer.WriteStartObject();
                for (var i = 0; i < type.Fields.Length; i++)
                {
                    writer.WritePropertyName(type.Fields[i].Name);
                    Write(writer, process, artifact, type.Fields[i].TypeId, process.GetField(value, i), depth + 1, ref budget);
                }
                writer.WriteEndObject();
                break;
            case MetaTypeKind.Option:
                if (process.GetVariantTag(value) == 0)
                    writer.WriteNullValue();
                else
                    Write(writer, process, artifact, type.Args[0], process.GetVariantPayload(value), depth + 1, ref budget);
                break;
            case MetaTypeKind.Array:
            case MetaTypeKind.List:
                writer.WriteStartArray();
                foreach (var item in process.GetSequenceCopy(value, Math.Min(MuiLimits.ChartPoints, budget)))
                    Write(writer, process, artifact, type.Args[0], item, depth + 1, ref budget);
                writer.WriteEndArray();
                break;
            case MetaTypeKind.Map:
                if (artifact.GetType(type.Args[0]).Kind != MetaTypeKind.String)
                    throw new FormatException("MUI maps require string keys.");
                writer.WriteStartObject();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in process.GetMapEntriesCopy(value, Math.Min(256, budget)))
                {
                    var key = process.GetString(item.Key);
                    if (key.Length > 128 || !names.Add(key))
                        throw new FormatException("MUI map key budget.");
                    writer.WritePropertyName(key);
                    Write(writer, process, artifact, type.Args[1], item.Value, depth + 1, ref budget);
                }
                writer.WriteEndObject();
                break;
            default: throw new FormatException("MUI cannot serialize native authority values.");
        }
    }

    private static MetaValue Read(JsonElement element, MetaProcess process, VerifiedArtifact artifact, int typeId, int depth, ref int budget)
    {
        if (depth >= 16 || --budget < 0)
            throw new FormatException("MUI input DTO budget.");
        var type = artifact.GetType(typeId);
        switch (type.Kind)
        {
            case MetaTypeKind.Bool: return MetaValue.Bool(element.GetBoolean());
            case MetaTypeKind.Byte: return MetaValue.Byte(element.GetByte());
            case MetaTypeKind.Int: return MetaValue.Int(element.GetInt64());
            case MetaTypeKind.Float: return MetaValue.Float(element.GetDouble());
            case MetaTypeKind.String: return process.CreateString(element.GetString()!);
            case MetaTypeKind.Enum:
                var name = element.GetString();
                var variant = type.Variants.SingleOrDefault(x => x.Name == name) ?? throw new FormatException("MUI enum input.");
                return process.CreateEnum(typeId, variant.Tag);
            case MetaTypeKind.Struct:
                if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Any(x => !type.Fields.Any(f => f.Name == x.Name)))
                    throw new FormatException("MUI nested DTO fields.");
                var fields = new MetaValue[type.Fields.Length];
                for (var i = 0; i < fields.Length; i++)
                {
                    var field = type.Fields[i];
                    if (!element.TryGetProperty(field.Name, out var data))
                    {
                        if (artifact.GetType(field.TypeId).Kind != MetaTypeKind.Option)
                            throw new FormatException("MUI required DTO field.");
                        fields[i] = process.CreateVariant(field.TypeId, 0);
                    }
                    else
                        fields[i] = Read(data, process, artifact, field.TypeId, depth + 1, ref budget);
                }
                return process.CreateRecord(typeId, fields);
            case MetaTypeKind.Option:
                return element.ValueKind == JsonValueKind.Null ? process.CreateVariant(typeId, 0) :
                    process.CreateVariant(typeId, 1, Read(element, process, artifact, type.Args[0], depth + 1, ref budget));
            case MetaTypeKind.Array:
            case MetaTypeKind.List:
                if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MuiLimits.ChartPoints)
                    throw new FormatException("MUI input array budget.");
                var items = new List<MetaValue>();
                foreach (var item in element.EnumerateArray())
                    items.Add(Read(item, process, artifact, type.Args[0], depth + 1, ref budget));
                return type.Kind == MetaTypeKind.Array ? process.CreateArray(typeId, items) : process.CreateList(typeId, items);
            case MetaTypeKind.Map:
                if (element.ValueKind != JsonValueKind.Object || artifact.GetType(type.Args[0]).Kind != MetaTypeKind.String)
                    throw new FormatException("MUI input map shape.");
                var entries = new List<KeyValuePair<MetaValue, MetaValue>>();
                foreach (var entry in element.EnumerateObject())
                    entries.Add(new(process.CreateString(entry.Name), Read(entry.Value, process, artifact, type.Args[1], depth + 1, ref budget)));
                return process.CreateMap(typeId, entries);
            default: throw new FormatException("MUI input cannot create native authority values.");
        }
    }

    private sealed class BoundedStream(int maximum) : Stream
    {
        private readonly MemoryStream _bytes = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _bytes.Length;
        public override long Position { get => _bytes.Position; set => throw new NotSupportedException(); }
        public byte[] ToArray() => _bytes.ToArray();
        public override void Flush() => _bytes.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > maximum - Length)
                throw new IOException("MUI encoded projection budget.");
            _bytes.Write(buffer);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _bytes.Dispose();
            base.Dispose(disposing);
        }
    }
}
