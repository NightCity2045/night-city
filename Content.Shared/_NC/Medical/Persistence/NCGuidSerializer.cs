// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.IoC;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Validation;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;
using Robust.Shared.Utility;

namespace Content.Shared._NC.Medical.Persistence;

/// <summary>
/// Serializes semantic medical identifiers as canonical GUID strings. Robust's prototype serializer does
/// not provide a GUID primitive, while persistence snapshots use GUIDs specifically to avoid EntityUid.
/// </summary>
[TypeSerializer]
public sealed class NCGuidSerializer : ITypeSerializer<Guid, ValueDataNode>, ITypeCopyCreator<Guid>
{
    public ValidationNode Validate(
        ISerializationManager serializationManager,
        ValueDataNode node,
        IDependencyCollection dependencies,
        ISerializationContext? context = null)
    {
        return Guid.TryParseExact(node.Value, "D", out _)
            ? new ValidatedValueNode(node)
            : new ErrorNode(node, "Expected a GUID in canonical D format.");
    }

    public Guid Read(
        ISerializationManager serializationManager,
        ValueDataNode node,
        IDependencyCollection dependencies,
        SerializationHookContext hookCtx,
        ISerializationContext? context = null,
        ISerializationManager.InstantiationDelegate<Guid>? instanceProvider = null)
    {
        if (!Guid.TryParseExact(node.Value, "D", out var value))
            throw new InvalidMappingException($"Invalid medical GUID: '{node.Value}'.");

        return value;
    }

    public DataNode Write(
        ISerializationManager serializationManager,
        Guid value,
        IDependencyCollection dependencies,
        bool alwaysWrite = false,
        ISerializationContext? context = null)
    {
        return new ValueDataNode(value.ToString("D"));
    }

    public Guid CreateCopy(
        ISerializationManager serializationManager,
        Guid source,
        IDependencyCollection dependencies,
        SerializationHookContext hookCtx,
        ISerializationContext? context = null)
    {
        return source;
    }
}
