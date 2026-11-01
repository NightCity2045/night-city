// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

public sealed partial class DiagnosticApplication
{
    private JsonObject OperationInfo(string id)
    {
        var operation = Operations.Get(id);
        var node = JsonSerializer.SerializeToNode(operation, DiagnosticProtocol.JsonOptions)!.AsObject();
        if (Encoding.UTF8.GetByteCount(node.ToJsonString()) <= DiagnosticProtocol.MaximumResponseBytes)
            return node;
        var summary = new JsonObject { ["truncated"] = true, ["coverage"] = "read-retained-artifact-for-complete-result" };
        foreach (var name in new[] { "artifact", "jsonArtifact", "htmlArtifact", "passed" })
            summary[name] = operation.Result?[name]?.DeepClone();
        node["result"] = summary;
        return node;
    }

    private async Task<JsonNode?> ReadArtifactAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var id = args.RequiredString("id", 100);
        var document = JsonNode.Parse(await Artifacts.ReadJsonAsync(id, cancellationToken));
        var pointer = args["jsonPointer"]?.GetValue<string>() ?? "";
        if (!JsonInspection.TryAt(document, pointer, out var selected))
            throw new DiagnosticException("artifact_path_missing", "The JSON pointer selects an absent artifact field.");
        if (selected is JsonArray array)
        {
            var offset = args.BoundedInt("offset", 0, 0, 1_000_000);
            var limit = args.BoundedInt("limit", 100, 1, 100);
            var page = new JsonArray();
            var bytes = 0;
            foreach (var value in array.Skip(offset).Take(limit))
            {
                var size = Encoding.UTF8.GetByteCount(value?.ToJsonString() ?? "null");
                if (bytes + size > DiagnosticProtocol.MaximumResponseBytes - 4096)
                    break;
                page.Add(value?.DeepClone()); bytes += size;
            }
            return new JsonObject { ["items"] = page, ["total"] = array.Count, ["offset"] = offset,
                ["nextOffset"] = offset + page.Count < array.Count && page.Count != 0 ? offset + page.Count : null,
                ["truncated"] = offset + page.Count < array.Count,
                ["coverage"] = page.Count == 0 && offset < array.Count ? "select-a-field-within-the-large-array-item" : "array-page" };
        }
        if (Encoding.UTF8.GetByteCount(selected?.ToJsonString() ?? "null") <= DiagnosticProtocol.MaximumResponseBytes - 4096)
            return selected?.DeepClone();
        return new JsonObject { ["id"] = id, ["truncated"] = true, ["jsonPointer"] = pointer,
            ["fields"] = selected is JsonObject obj ? JsonSerializer.SerializeToNode(obj.Select(pair => pair.Key)) : null,
            ["coverage"] = "select-a-smaller-field-or-array-with-jsonPointer" };
    }
}
