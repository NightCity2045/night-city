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

/// <summary>
/// Common YAML scalar serializer for typed city identities. Concrete serializers retain strict type separation.
/// </summary>
public abstract class NCCityObjectIdSerializer<T> :
    ITypeSerializer<T, ValueDataNode>,
    ITypeCopyCreator<T>
{
    protected abstract string TypeName { get; }
    protected abstract bool TryParse(string value, out T id);

    public ValidationNode Validate(
        ISerializationManager serializationManager,
        ValueDataNode node,
        IDependencyCollection dependencies,
        ISerializationContext? context = null)
    {
        return TryParse(node.Value, out _)
            ? new ValidatedValueNode(node)
            : new ErrorNode(node, $"Expected a non-empty GUID for {TypeName}.");
    }

    public T Read(
        ISerializationManager serializationManager,
        ValueDataNode node,
        IDependencyCollection dependencies,
        SerializationHookContext hookCtx,
        ISerializationContext? context = null,
        ISerializationManager.InstantiationDelegate<T>? instanceProvider = null)
    {
        if (!TryParse(node.Value, out var id))
            throw new InvalidMappingException($"Invalid {TypeName}: '{node.Value}'.");

        return id;
    }

    public DataNode Write(
        ISerializationManager serializationManager,
        T value,
        IDependencyCollection dependencies,
        bool alwaysWrite = false,
        ISerializationContext? context = null)
    {
        return new ValueDataNode(value?.ToString() ?? string.Empty);
    }

    public T CreateCopy(
        ISerializationManager serializationManager,
        T source,
        IDependencyCollection dependencies,
        SerializationHookContext hookCtx,
        ISerializationContext? context = null)
    {
        return source;
    }
}

[TypeSerializer]
public sealed class NCDistrictIdSerializer : NCCityObjectIdSerializer<NCDistrictId>
{
    protected override string TypeName => nameof(NCDistrictId);
    protected override bool TryParse(string value, out NCDistrictId id) => NCDistrictId.TryParse(value, out id);
}

[TypeSerializer]
public sealed class NCSectorIdSerializer : NCCityObjectIdSerializer<NCSectorId>
{
    protected override string TypeName => nameof(NCSectorId);
    protected override bool TryParse(string value, out NCSectorId id) => NCSectorId.TryParse(value, out id);
}

[TypeSerializer]
public sealed class NCStreetIdSerializer : NCCityObjectIdSerializer<NCStreetId>
{
    protected override string TypeName => nameof(NCStreetId);
    protected override bool TryParse(string value, out NCStreetId id) => NCStreetId.TryParse(value, out id);
}

[TypeSerializer]
public sealed class NCBuildingIdSerializer : NCCityObjectIdSerializer<NCBuildingId>
{
    protected override string TypeName => nameof(NCBuildingId);
    protected override bool TryParse(string value, out NCBuildingId id) => NCBuildingId.TryParse(value, out id);
}

[TypeSerializer]
public sealed class NCApartmentIdSerializer : NCCityObjectIdSerializer<NCApartmentId>
{
    protected override string TypeName => nameof(NCApartmentId);
    protected override bool TryParse(string value, out NCApartmentId id) => NCApartmentId.TryParse(value, out id);
}
