// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Bytecode;

namespace Content.Meta.Runtime;

public sealed partial class MetaProcess
{
    /// <summary>Copy allowlisted data into this process. No mutable collection or host handle is shared across heaps.</summary>
    public MetaValue CopyFrom(MetaProcess source, MetaValue value, int maximumLogicalBytes = 65536)
    {
        if (maximumLogicalBytes < 16 || maximumLogicalBytes > 65536) throw new ArgumentOutOfRangeException(nameof(maximumLogicalBytes));
        var bytes = source.LogicalBytes(value, maximumLogicalBytes, 0);
        Preflight(checked(bytes * 8 + 256));
        var copied = new Dictionary<MetaValue, MetaValue>();
        MetaValue Copy(MetaValue item, int depth)
        {
            if (depth > 16) throw new MetaTrap(MetaVmFaultCode.InvalidBytecode, "DTO depth limit.");
            source.ValidateValue(item, checked((int) item.TypeId));
            if (item.Kind is MetaValueKind.Capability or MetaValueKind.Opaque) throw new MetaTrap(MetaVmFaultCode.InvalidBytecode, "Authority references cannot cross process boundaries.");
            var sourceType = source._module.Types[item.TypeId];
            var targetId = GetTypeId(sourceType.Name);
            if (targetId < 0) throw new MetaTrap(MetaVmFaultCode.InvalidBytecode, "Target DTO schema is missing.");
            var targetType = _module.Types[targetId];
            if (targetType.Kind != sourceType.Kind || targetType.ContractHash != sourceType.ContractHash || sourceType.Kind is MetaTypeKind.Struct or MetaTypeKind.Enum && sourceType.ContractHash == null && source.ArtifactDigest != ArtifactDigest)
                throw new MetaTrap(MetaVmFaultCode.InvalidBytecode, "DTO schema mismatch.");
            if (!sourceType.Args.Select(id => source._module.Types[id].Name).SequenceEqual(targetType.Args.Select(id => _module.Types[id].Name), StringComparer.Ordinal) ||
                !sourceType.Fields.Select(field => (field.Name, field.Readonly, Type: source._module.Types[field.TypeId].Name)).SequenceEqual(targetType.Fields.Select(field => (field.Name, field.Readonly, Type: _module.Types[field.TypeId].Name))) ||
                !sourceType.Variants.Select(variant => (variant.Name, variant.Tag, Type: variant.PayloadTypeId is { } payload ? source._module.Types[payload].Name : null)).SequenceEqual(targetType.Variants.Select(variant => (variant.Name, variant.Tag, Type: variant.PayloadTypeId is { } payload ? _module.Types[payload].Name : null))))
                throw new MetaTrap(MetaVmFaultCode.InvalidBytecode, "DTO child schema mismatch.");
            if (item.Kind is <= MetaValueKind.Enum) return new MetaValue(item.Kind, targetId, item.Payload);
            if (copied.TryGetValue(item, out var previous)) return previous;
            var obj = source._heap.Get(item);
            MetaValue result;
            switch (item.Kind)
            {
                case MetaValueKind.String: result = CreateString(obj.Text!); break;
                case MetaValueKind.Record: result = CreateRecord(targetId, obj.Values.Select(child => Copy(child, depth + 1)).ToArray()); break;
                case MetaValueKind.Variant: result = CreateVariant(targetId, obj.Tag, obj.Count == 0 ? null : Copy(obj.Values[0], depth + 1)); break;
                case MetaValueKind.Array: result = CreateArray(targetId, obj.Values.Select(child => Copy(child, depth + 1)).ToArray()); break;
                case MetaValueKind.List:
                    result = CreateList(targetId, obj.Values.Take(obj.Count).Select(child => Copy(child, depth + 1)).ToArray()); _heap.Get(result).Version = obj.Version; break;
                case MetaValueKind.Map:
                    result = CreateMap(targetId, obj.Entries!.Select(pair => new KeyValuePair<MetaValue, MetaValue>(Copy(pair.Key, depth + 1), Copy(pair.Value, depth + 1))).ToArray()); break;
                default: throw new MetaTrap(MetaVmFaultCode.InvalidBytecode, "Unsupported DTO data kind.");
            }
            copied.Add(item, result); return result;
        }
        return Copy(value, 0);
    }
}
