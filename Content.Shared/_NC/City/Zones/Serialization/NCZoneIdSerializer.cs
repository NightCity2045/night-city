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

namespace Content.Shared._NC.City.Zones.Serialization;

[TypeSerializer]
public sealed class NCZoneIdSerializer : ITypeSerializer<NCZoneId, ValueDataNode>, ITypeCopyCreator<NCZoneId>
{
    public ValidationNode Validate(
        ISerializationManager serializationManager,
        ValueDataNode node,
        IDependencyCollection dependencies,
        ISerializationContext? context = null)
    {
        return NCZoneId.TryParse(node.Value, out _)
            ? new ValidatedValueNode(node)
            : new ErrorNode(node, "Expected a non-empty GUID for NCZoneId.");
    }

    public NCZoneId Read(
        ISerializationManager serializationManager,
        ValueDataNode node,
        IDependencyCollection dependencies,
        SerializationHookContext hookCtx,
        ISerializationContext? context = null,
        ISerializationManager.InstantiationDelegate<NCZoneId>? instanceProvider = null)
    {
        if (!NCZoneId.TryParse(node.Value, out var zoneId))
            throw new InvalidMappingException($"Invalid NCZoneId: '{node.Value}'.");

        return zoneId;
    }

    public DataNode Write(
        ISerializationManager serializationManager,
        NCZoneId value,
        IDependencyCollection dependencies,
        bool alwaysWrite = false,
        ISerializationContext? context = null)
    {
        return new ValueDataNode(value.ToString());
    }

    public NCZoneId CreateCopy(
        ISerializationManager serializationManager,
        NCZoneId source,
        IDependencyCollection dependencies,
        SerializationHookContext hookCtx,
        ISerializationContext? context = null)
    {
        return source;
    }
}
