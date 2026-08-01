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

namespace Content.Shared._NC.Coordinates.Serialization;

/// <summary>
/// Stores Z-network identities as canonical GUID strings in YAML.
/// </summary>
[TypeSerializer]
public sealed class NCZNetworkIdSerializer :
    ITypeSerializer<NCZNetworkId, ValueDataNode>,
    ITypeCopyCreator<NCZNetworkId>
{
    public ValidationNode Validate(
        ISerializationManager serializationManager,
        ValueDataNode node,
        IDependencyCollection dependencies,
        ISerializationContext? context = null)
    {
        return NCZNetworkId.TryParse(node.Value, out _)
            ? new ValidatedValueNode(node)
            : new ErrorNode(node, "Expected a non-empty GUID for NCZNetworkId.");
    }

    public NCZNetworkId Read(
        ISerializationManager serializationManager,
        ValueDataNode node,
        IDependencyCollection dependencies,
        SerializationHookContext hookCtx,
        ISerializationContext? context = null,
        ISerializationManager.InstantiationDelegate<NCZNetworkId>? instanceProvider = null)
    {
        if (!NCZNetworkId.TryParse(node.Value, out var networkId))
            throw new InvalidMappingException($"Invalid NCZNetworkId: '{node.Value}'.");

        return networkId;
    }

    public DataNode Write(
        ISerializationManager serializationManager,
        NCZNetworkId value,
        IDependencyCollection dependencies,
        bool alwaysWrite = false,
        ISerializationContext? context = null)
    {
        return new ValueDataNode(value.ToString());
    }

    public NCZNetworkId CreateCopy(
        ISerializationManager serializationManager,
        NCZNetworkId source,
        IDependencyCollection dependencies,
        SerializationHookContext hookCtx,
        ISerializationContext? context = null)
    {
        return source;
    }
}
