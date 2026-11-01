// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#if TOOLS && NC_DIAGNOSTICS
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using Content.Shared.Access.Components;
using Content.Shared.Hands.Components;
using Content.Shared.Power.Components;
using Robust.Shared.Containers;
using NightCity.Diagnostics.Contracts;

namespace Content.Shared._NC.Diagnostics;

/// <summary>Only declared data/VV fields are inspected. No getters, methods or unknown object graphs execute.</summary>
public static class NCDiagnosticProjection
{
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> Fields = new();

    public static JsonObject Capture(EntityUid uid, IEntityManager entities, string[]? requestedComponents = null)
    {
        if (!entities.EntityExists(uid) || entities.Deleted(uid))
            throw new DiagnosticException("entity_not_found", "The entity no longer exists.");
        var metadata = entities.GetComponent<MetaDataComponent>(uid);
        var transform = entities.GetComponent<TransformComponent>(uid);
        var components = new JsonObject();
        var available = new JsonArray();
        foreach (var component in entities.GetComponents(uid))
        {
            var name = entities.ComponentFactory.GetRegistration(component.GetType()).Name;
            available.Add(name);
            if (requestedComponents != null && !requestedComponents.Contains(name, StringComparer.Ordinal))
                continue;
            if (components.Count < 32)
                components[name] = CaptureComponent(component, entities);
        }
        return new JsonObject
        {
            ["netEntity"] = entities.GetNetEntity(uid).ToString(), ["name"] = metadata.EntityName,
            ["prototype"] = metadata.EntityPrototype?.ID, ["lifeStage"] = metadata.EntityLifeStage.ToString(),
            ["transform"] = new JsonObject
            {
                ["parent"] = entities.TryGetNetEntity(transform.ParentUid, out var parent) ? parent.Value.ToString() : null,
                ["map"] = transform.MapID.ToString(), ["x"] = transform.LocalPosition.X, ["y"] = transform.LocalPosition.Y,
                ["rotation"] = transform.LocalRotation.Theta, ["anchored"] = transform.Anchored,
            },
            ["availableComponents"] = available, ["components"] = components,
            ["readers"] = CaptureReaders(uid, entities),
            ["truncated"] = available.Count > 32 && requestedComponents == null,
        };
    }

    private static JsonObject CaptureReaders(EntityUid uid, IEntityManager entities)
    {
        string? Reference(EntityUid entity) => entities.TryGetNetEntity(entity, out var reference) ? reference.Value.ToString() : null;
        var result = new JsonObject();
        if (entities.TryGetComponent<ContainerManagerComponent>(uid, out var manager))
        {
            var containers = new JsonObject();
            foreach (var (id, container) in manager.Containers.Take(32))
                containers[id] = new JsonObject
                {
                    ["entities"] = new JsonArray(container.ContainedEntities.Take(64).Select(entity => (JsonNode?)JsonValue.Create(Reference(entity))).ToArray()),
                    ["truncated"] = container.ContainedEntities.Count > 64,
                };
            result["containers"] = new JsonObject { ["items"] = containers, ["truncated"] = manager.Containers.Count > 32 };
        }
        if (entities.TryGetComponent<HandsComponent>(uid, out var hands))
        {
            var held = new JsonObject();
            foreach (var id in hands.Hands.Keys.Take(16))
                held[id] = manager != null && manager.Containers.TryGetValue(id, out var slot) && slot.ContainedEntities.Count > 0
                    ? Reference(slot.ContainedEntities[0]) : null;
            result["hands"] = new JsonObject { ["activeHand"] = hands.ActiveHandId, ["held"] = held };
        }
        if (entities.TryGetComponent<AccessComponent>(uid, out var access))
            result["access"] = new JsonObject { ["enabled"] = access.Enabled,
                ["tags"] = new JsonArray(access.Tags.Take(64).Select(tag => (JsonNode?)JsonValue.Create(tag.Id)).ToArray()),
                ["truncated"] = access.Tags.Count > 64 };
        if (entities.TryGetComponent<AccessReaderComponent>(uid, out var reader))
            result["accessReader"] = new JsonObject { ["enabled"] = reader.Enabled,
                ["requirements"] = new JsonArray(reader.AccessLists.Take(16).Select(group => (JsonNode?)new JsonArray(
                    group.Take(32).Select(tag => (JsonNode?)JsonValue.Create(tag.Id)).ToArray())).ToArray()),
                ["truncated"] = reader.AccessLists.Count > 16 || reader.AccessLists.Any(group => group.Count > 32) };
        if (entities.GetComponents(uid).OfType<SharedApcPowerReceiverComponent>().FirstOrDefault() is { } power)
            result["power"] = new JsonObject { ["powered"] = power.Powered, ["needsPower"] = power.NeedsPower,
                ["disabled"] = power.PowerDisabled, ["load"] = power.Load };
        return result;
    }

    public static JsonObject CaptureComponent(IComponent component, IEntityManager entities)
    {
        var values = new JsonObject();
        var omitted = new JsonArray();
        var fields = Fields.GetOrAdd(component.GetType(), type => type.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(field => field.GetCustomAttributesData().Any(attribute =>
                attribute.AttributeType.Name is "DataFieldAttribute" or "ViewVariablesAttribute"))
            .OrderBy(field => field.Name, StringComparer.Ordinal).ToArray());
        foreach (var field in fields.Take(48))
        {
            if (Sensitive(field.Name))
                omitted.Add(field.Name + ":redacted");
            else if (TryValue(field.GetValue(component), entities, 0, out var value))
                values[field.Name] = value;
            else
                omitted.Add(field.Name + ":unsupported_or_oversize");
        }
        return new JsonObject { ["fields"] = values, ["omitted"] = omitted,
            ["truncated"] = fields.Length > 48, ["representation"] = "declared-fields" };
    }

    private static bool TryValue(object? value, IEntityManager entities, int depth, out JsonNode? node)
    {
        node = null;
        switch (value)
        {
            case null: return true;
            case string text when text.Length <= 512: node = JsonValue.Create(text); return true;
            case bool boolean: node = JsonValue.Create(boolean); return true;
            case byte number: node = JsonValue.Create(number); return true;
            case sbyte number: node = JsonValue.Create(number); return true;
            case short number: node = JsonValue.Create(number); return true;
            case ushort number: node = JsonValue.Create(number); return true;
            case int number: node = JsonValue.Create(number); return true;
            case uint number: node = JsonValue.Create(number); return true;
            case long number: node = JsonValue.Create(number); return true;
            case ulong number: node = JsonValue.Create(number); return true;
            case float number when float.IsFinite(number): node = JsonValue.Create(number); return true;
            case double number when double.IsFinite(number): node = JsonValue.Create(number); return true;
            case Enum enumeration: node = JsonValue.Create(enumeration.ToString()); return true;
            case TimeSpan time: node = JsonValue.Create(time.TotalSeconds); return true;
            case Vector2 vector: node = new JsonObject { ["x"] = vector.X, ["y"] = vector.Y }; return true;
            case EntityUid uid:
                node = entities.TryGetNetEntity(uid, out var reference) ? JsonValue.Create(reference.Value.ToString()) : null;
                return true;
            case NetEntity networkReference: node = JsonValue.Create(networkReference.ToString()); return true;
            case Array array when depth < 2 && array.Length <= 32:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    if (!TryValue(item, entities, depth + 1, out var converted))
                        return false;
                    items.Add(converted);
                }
                node = items;
                return true;
            default: return false;
        }
    }

    private static bool Sensitive(string name) => name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("credential", StringComparison.OrdinalIgnoreCase) || name.Contains("privatekey", StringComparison.OrdinalIgnoreCase);
}

#endif
