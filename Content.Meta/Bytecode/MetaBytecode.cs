// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Content.Meta.Bytecode;

public static class MetaCanonicalJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static byte[] Serialize<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, Options), new JsonDocumentOptions { MaxDepth = 16 });
        var builder = new StringBuilder();
        Append(document.RootElement, builder);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static T Deserialize<T>(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var builder = new StringBuilder();
            Append(document.RootElement, builder);
            if (!bytes.Span.SequenceEqual(Encoding.UTF8.GetBytes(builder.ToString())))
                throw new MetaFormatException("Metadata JSON is not canonical.");
            var result = JsonSerializer.Deserialize<T>(bytes.Span, Options) ?? throw new MetaFormatException("Null metadata.");
            // Re-encoding the allowlisted DTO also rejects missing fields and noncanonical enum forms.
            if (!bytes.Span.SequenceEqual(Serialize(result))) throw new MetaFormatException("Metadata schema mismatch.");
            return result;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new MetaFormatException("Invalid metadata JSON: " + exception.GetType().Name);
        }
    }

    private static void Append(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var names = new HashSet<string>(StringComparer.Ordinal);
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!names.Add(property.Name) || property.Name.Any(c => c > 127)) throw new MetaFormatException("Duplicate or non-ASCII metadata key.");
                    if (!first) builder.Append(',');
                    first = false;
                    AppendString(property.Name, builder);
                    builder.Append(':');
                    if (property.Value.ValueKind == JsonValueKind.Null && property.Name is not ("mainFunctionId" or "initializerFunctionId" or "contractHash" or "payloadTypeId" or "value" or "version" or "contentHash" or "capabilityTypeId" or "returnType"))
                        throw new MetaFormatException("Null in required metadata field.");
                    Append(property.Value, builder);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Null) throw new MetaFormatException("Null array item.");
                    if (!first) builder.Append(',');
                    first = false;
                    Append(item, builder);
                }
                builder.Append(']');
                break;
            case JsonValueKind.String: AppendString(element.GetString()!, builder); break;
            case JsonValueKind.Number:
                if (!element.TryGetUInt32(out var number)) throw new MetaFormatException("Metadata number is not u32.");
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True: builder.Append("true"); break;
            case JsonValueKind.False: builder.Append("false"); break;
            case JsonValueKind.Null: builder.Append("null"); break;
            default: throw new MetaFormatException("Invalid JSON value.");
        }
    }

    private static void AppendString(string value, StringBuilder builder)
    {
        builder.Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '"') builder.Append("\\\"");
            else if (character == '\\') builder.Append("\\\\");
            else if (character < 32) builder.Append("\\u").Append(((int) character).ToString("x4", CultureInfo.InvariantCulture));
            else if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) throw new MetaFormatException("Unpaired Unicode surrogate.");
                builder.Append(character).Append(value[++index]);
            }
            else if (char.IsLowSurrogate(character)) throw new MetaFormatException("Unpaired Unicode surrogate.");
            else builder.Append(character);
        }
        builder.Append('"');
    }
}

public static class MetaBytecodeWriter
{
    public static byte[] Write(MetaModule module)
    {
        var sections = new List<(MetaSectionKind Kind, byte[] Bytes, int Count)>
        {
            (MetaSectionKind.Manifest, MetaCanonicalJson.Serialize(module.Manifest), 1),
            (MetaSectionKind.Types, MetaCanonicalJson.Serialize(module.Types), module.Types.Length),
            (MetaSectionKind.Constants, MetaCanonicalJson.Serialize(module.Constants), module.Constants.Length),
            (MetaSectionKind.Functions, MetaCanonicalJson.Serialize(module.Functions), module.Functions.Length),
            (MetaSectionKind.Code, module.Code, MetaInstructionCodec.Decode(module.Code).Length),
            (MetaSectionKind.Imports, MetaCanonicalJson.Serialize(module.Imports), module.Imports.Length),
            (MetaSectionKind.Links, MetaCanonicalJson.Serialize(module.Links), module.Links.Length),
            (MetaSectionKind.Watches, MetaCanonicalJson.Serialize(module.Watches), module.Watches.Length)
        };
        if (module.Debug != null) sections.Add((MetaSectionKind.Debug, MetaCanonicalJson.Serialize(module.Debug), 1));
        sections.Add((MetaSectionKind.Globals, MetaCanonicalJson.Serialize(module.Globals), module.Globals.Length));
        var directoryEnd = checked(64 + sections.Count * 16);
        var length = sections.Aggregate(directoryEnd, (total, section) => checked(total + section.Bytes.Length));
        var output = new byte[length];
        "META"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16), checked((uint) length));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(20), checked((ushort) sections.Count));
        var offset = directoryEnd;
        for (var index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            var entry = output.AsSpan(64 + index * 16, 16);
            BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort) section.Kind);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], checked((uint) offset));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], checked((uint) section.Bytes.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], checked((uint) section.Count));
            section.Bytes.CopyTo(output, offset);
            offset += section.Bytes.Length;
        }
        SHA256.HashData(output).CopyTo(output, 24);
        return output;
    }
}

public static class MetaBytecodeLoader
{
    public static MetaModule Load(ReadOnlySpan<byte> bytes, MetaLimits? limits = null)
    {
        limits ??= new MetaLimits();
        if (bytes.Length < 64 || bytes.Length > limits.ArtifactBytes) throw new MetaFormatException("Artifact size limit.");
        if (!bytes[..4].SequenceEqual("META"u8)) throw new MetaFormatException("Invalid magic.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]) != 0)
            throw new MetaFormatException("Unsupported bytecode/language version.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[22..]) != 0 || BinaryPrimitives.ReadUInt64LittleEndian(bytes[56..]) != 0)
            throw new MetaFormatException("Unknown flags or reserved bytes.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) != bytes.Length) throw new MetaFormatException("Total length mismatch.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[20..]);
        if (count is not (9 or 10) || bytes.Length < 64 + 16 * count) throw new MetaFormatException("Invalid section count.");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(bytes[..24]);
        digest.AppendData(new byte[32]);
        digest.AppendData(bytes[56..]);
        if (!CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), bytes.Slice(24, 32))) throw new MetaFormatException("Content digest mismatch.");
        var sections = new Dictionary<MetaSectionKind, (byte[] Bytes, int Count)>();
        var cursor = 64 + count * 16;
        var previous = 0;
        for (var index = 0; index < count; index++)
        {
            var entry = bytes.Slice(64 + 16 * index, 16);
            var kind = (MetaSectionKind) BinaryPrimitives.ReadUInt16LittleEndian(entry);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var items = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (!Enum.IsDefined(kind) || (int) kind <= previous || BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]) != 0)
                throw new MetaFormatException("Unknown, duplicate or unordered section.");
            previous = (int) kind;
            if (offset != cursor || length > bytes.Length - cursor || items > int.MaxValue) throw new MetaFormatException("Section range mismatch.");
            var maximum = kind switch
            {
                MetaSectionKind.Manifest or MetaSectionKind.Debug => 1,
                MetaSectionKind.Functions => limits.Functions,
                MetaSectionKind.Types => limits.Types,
                MetaSectionKind.Constants => limits.Constants,
                MetaSectionKind.Imports => limits.Imports,
                MetaSectionKind.Globals => limits.Globals,
                _ => limits.ArtifactBytes / 2
            };
            if (items > maximum || length == 0 && items != 0) throw new MetaFormatException("Section item limit.");
            sections.Add(kind, (bytes.Slice(cursor, (int) length).ToArray(), (int) items));
            cursor = checked(cursor + (int) length);
        }
        if (cursor != bytes.Length || Enumerable.Range(1, 10).Any(kind => kind != 9 && !sections.ContainsKey((MetaSectionKind) kind))) throw new MetaFormatException("Missing sections or trailing bytes.");
        T Read<T>(MetaSectionKind kind)
        {
            var section = sections[kind];
            var value = MetaCanonicalJson.Deserialize<T>(section.Bytes);
            var actual = value is Array array ? array.Length : 1;
            if (actual != section.Count) throw new MetaFormatException("Section item count mismatch.");
            return value;
        }
        var code = sections[MetaSectionKind.Code];
        if (MetaInstructionCodec.Decode(code.Bytes).Length != code.Count) throw new MetaFormatException("Code instruction count mismatch.");
        return new MetaModule
        {
            Manifest = Read<MetaManifest>(MetaSectionKind.Manifest), Types = Read<MetaTypeDef[]>(MetaSectionKind.Types),
            Constants = Read<MetaConstantDef[]>(MetaSectionKind.Constants), Functions = Read<MetaFunctionDef[]>(MetaSectionKind.Functions),
            Code = code.Bytes, Imports = Read<MetaImportDef[]>(MetaSectionKind.Imports), Links = Read<MetaLinkDef[]>(MetaSectionKind.Links),
            Watches = Read<MetaWatchDef[]>(MetaSectionKind.Watches), Globals = Read<MetaGlobalDef[]>(MetaSectionKind.Globals),
            Debug = sections.ContainsKey(MetaSectionKind.Debug) ? Read<MetaDebugInfo>(MetaSectionKind.Debug) : null
        };
    }
}
