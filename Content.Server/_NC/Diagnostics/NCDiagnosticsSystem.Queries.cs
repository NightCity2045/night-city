// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#if TOOLS && NC_DIAGNOSTICS
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Content.Shared._NC.Diagnostics;
using NightCity.Diagnostics.Contracts;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._NC.Diagnostics;

public sealed partial class NCDiagnosticsSystem
{
    private sealed record QuerySnapshot(JsonObject[] Entities, DateTimeOffset ExpiresAt, long Tick, bool Truncated);
    private readonly Dictionary<string, QuerySnapshot> _querySnapshots = new();

    private EntityUid ResolveEntity(JsonObject args, string field = "netEntity")
    {
        if (!NetEntity.TryParse(args.RequiredString(field, 32), out var reference) ||
            !TryGetEntity(reference, out var uid) || Deleted(uid))
            throw new DiagnosticException("entity_not_found", "The referenced entity does not exist.");
        return uid.Value;
    }

    private JsonObject InspectEntity(JsonObject args)
    {
        var requested = args["components"]?.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        if (requested is { Length: > 32 })
            throw new DiagnosticException("invalid_arguments", "At most 32 components may be requested.");
        return NCDiagnosticProjection.Capture(ResolveEntity(args), EntityManager, requested);
    }

    private JsonObject QueryEntities(JsonObject args)
    {
        var limit = args.BoundedInt("limit", 50, 1, DiagnosticProtocol.MaximumPageSize);
        QuerySnapshot snapshot;
        string snapshotId;
        var offset = 0;
        if (args["cursor"]?.GetValue<string>() is { } cursor)
        {
            var parts = cursor.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out offset) || offset < 0 ||
                !_querySnapshots.TryGetValue(parts[0], out snapshot!) || snapshot.ExpiresAt < DateTimeOffset.UtcNow)
                throw new DiagnosticException("stale_cursor", "The query cursor expired. Start a new query.");
            snapshotId = parts[0];
        }
        else
        {
            foreach (var expired in _querySnapshots.Where(x => x.Value.ExpiresAt < DateTimeOffset.UtcNow).Select(x => x.Key).ToArray())
                _querySnapshots.Remove(expired);
            if (_querySnapshots.Count >= 8)
                throw new DiagnosticException("busy", "The query snapshot registry is full. Snapshots expire after 30 seconds.", true);
            var anchor = ResolveEntity(args, args["near"] != null ? "near" : "map");
            var coordinates = _transforms.GetMapCoordinates(anchor);
            var position = args["near"] != null ? coordinates.Position : new Vector2(
                args.BoundedFloat("x", 0, -1_000_000, 1_000_000), args.BoundedFloat("y", 0, -1_000_000, 1_000_000));
            var radius = args.BoundedFloat("radius", 8, 0.1f, 16);
            var extent = new Vector2(radius);
            var candidates = _lookup.GetEntitiesIntersecting(coordinates.MapId, new Box2(position - extent, position + extent), LookupFlags.Uncontained);
            if (candidates.Count > 4096)
                throw new DiagnosticException("query_too_dense", "Reduce the query radius; the selected region is too dense.");
            var prototype = args["prototype"]?.GetValue<string>();
            var component = args["component"]?.GetValue<string>();
            Type? componentType = null;
            if (component != null)
            {
                if (!EntityManager.ComponentFactory.TryGetRegistration(component, out var registration))
                    throw new DiagnosticException("unsupported_component", "The component is not registered.");
                componentType = registration.Type;
            }
            var results = new List<JsonObject>();
            foreach (var uid in candidates)
            {
                if (Deleted(uid) || !TryComp(uid, out MetaDataComponent? metadata) ||
                    prototype != null && metadata.EntityPrototype?.ID != prototype ||
                    componentType != null && !HasComp(uid, componentType))
                    continue;
                results.Add(new JsonObject { ["netEntity"] = GetNetEntity(uid).ToString(),
                    ["name"] = metadata.EntityName, ["prototype"] = metadata.EntityPrototype?.ID });
                if (results.Count == 501)
                    break;
            }
            snapshot = new QuerySnapshot(results.Take(500).ToArray(), DateTimeOffset.UtcNow.AddSeconds(30),
                _timing.CurTick.Value, results.Count > 500);
            snapshotId = Guid.NewGuid().ToString("N");
            _querySnapshots[snapshotId] = snapshot;
        }
        var page = new JsonArray(snapshot.Entities.Skip(offset).Take(limit).Select(x => x.DeepClone()).ToArray());
        var next = offset + page.Count;
        return new JsonObject { ["entities"] = page, ["snapshotTick"] = snapshot.Tick,
            ["nextCursor"] = next < snapshot.Entities.Length ? snapshotId + ":" + next : null,
            ["truncated"] = snapshot.Truncated, ["scope"] = "bounded-map-region" };
    }

    private JsonObject ListMaps()
    {
        var result = new JsonArray();
        var query = EntityQueryEnumerator<MapComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out _, out var metadata) && result.Count < 64)
            result.Add(new JsonObject { ["netEntity"] = GetNetEntity(uid).ToString(), ["name"] = metadata.EntityName,
                ["mapId"] = Transform(uid).MapID.ToString() });
        return new JsonObject { ["maps"] = result, ["truncated"] = result.Count == 64 };
    }

    private JsonObject SearchPrototypes(JsonObject args)
    {
        var prefix = args["prefix"]?.GetValue<string>() ?? string.Empty;
        if (prefix.Length > 128)
            throw new DiagnosticException("invalid_arguments", "The prototype prefix is too long.");
        var limit = args.BoundedInt("limit", 50, 1, 100);
        var values = _prototypes.EnumeratePrototypes<EntityPrototype>()
            .Where(x => x.ID.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(limit + 1).Select(x => x.ID).ToArray();
        return new JsonObject { ["ids"] = JsonSerializer.SerializeToNode(values.Take(limit)), ["truncated"] = values.Length > limit };
    }

    private JsonObject GetPrototype(JsonObject args)
    {
        var kind = args["kind"]?.GetValue<string>() ?? "entity";
        var id = args.RequiredString("id");
        if (!_prototypes.TryGetKindType(kind, out var type) || !_prototypes.TryGetMapping(type, id, out var mapping))
            throw new DiagnosticException("prototype_not_found", "The requested prototype mapping does not exist.");
        var budget = 4096;
        var result = ConvertNode(mapping, 0, ref budget);
        var parents = kind == "entity" ? _prototypes.EnumerateAllParents<EntityPrototype>(id).Select(x => x.id).Take(128).ToArray() : [];
        return new JsonObject { ["kind"] = kind, ["id"] = id, ["representation"] = "composed-yaml",
            ["parents"] = JsonSerializer.SerializeToNode(parents), ["data"] = result,
            ["provenance"] = "parent-list-only", ["truncated"] = budget <= 0 };
    }

    private static JsonNode? ConvertNode(DataNode node, int depth, ref int budget)
    {
        if (--budget < 0 || depth > 12)
        {
            budget = 0;
            return JsonValue.Create("[truncated]");
        }
        switch (node)
        {
            case ValueDataNode scalar:
                if (scalar.Value is { Length: > 4096 })
                {
                    budget = 0;
                    return JsonValue.Create("[truncated]");
                }
                return JsonValue.Create(scalar.Value);
            case SequenceDataNode sequence:
                var items = new JsonArray();
                foreach (var item in sequence.Sequence.Take(256))
                {
                    if (budget <= 0)
                        break;
                    items.Add(ConvertNode(item, depth + 1, ref budget));
                }
                if (sequence.Count > 256)
                    budget = 0;
                return items;
            case MappingDataNode mapping:
                var values = new JsonObject();
                foreach (var (key, value) in mapping.Children.Take(256))
                {
                    if (budget <= 0)
                        break;
                    values[key] = ConvertNode(value, depth + 1, ref budget);
                }
                if (mapping.Count > 256)
                    budget = 0;
                return values;
            default:
                return JsonValue.Create("[unsupported-node]");
        }
    }
}

#endif
