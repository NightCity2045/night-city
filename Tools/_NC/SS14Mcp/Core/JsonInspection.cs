// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

public static class JsonInspection
{
    public static JsonNode? At(JsonNode? root, string pointer)
    {
        TryAt(root, pointer, out var value);
        return value;
    }

    public static bool TryAt(JsonNode? root, string pointer, out JsonNode? value)
    {
        if (pointer.Length > 512 || pointer.Length > 0 && !pointer.StartsWith('/'))
            throw new DiagnosticException("invalid_arguments", "Expected a JSON pointer beginning with '/'.");
        if (pointer.Length == 0)
        {
            value = root;
            return true;
        }
        var segments = pointer[1..].Split('/');
        if (segments.Length > 16)
            throw new DiagnosticException("invalid_arguments", "A JSON pointer may contain at most 16 segments.");
        foreach (var encoded in segments)
        {
            var segment = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            switch (root)
            {
                case JsonObject obj when obj.TryGetPropertyValue(segment, out var child):
                    root = child;
                    break;
                case JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count:
                    root = array[index];
                    break;
                default:
                    value = null;
                    return false;
            }
        }
        value = root;
        return true;
    }

    public static bool Equal(JsonNode? left, JsonNode? right, double tolerance = 0)
    {
        if (!double.IsFinite(tolerance) || tolerance < 0)
            throw new DiagnosticException("invalid_arguments", "Comparison tolerance must be finite and nonnegative.");
        if (tolerance > 0 && left is JsonValue first && right is JsonValue second &&
            first.TryGetValue<double>(out var a) && second.TryGetValue<double>(out var b))
            return Math.Abs(a - b) <= tolerance;
        return JsonNode.DeepEquals(left, right);
    }

    public static JsonObject Diff(JsonNode? left, JsonNode? right, string[] pointers)
    {
        if (pointers.Length is < 1 or > 64)
            throw new DiagnosticException("invalid_arguments", "Compare between one and 64 explicit JSON pointers.");
        var differences = new JsonArray();
        var coverageGaps = new JsonArray();
        foreach (var pointer in pointers)
        {
            var beforePresent = TryAt(left, pointer, out var before);
            var afterPresent = TryAt(right, pointer, out var after);
            if (!beforePresent || !afterPresent)
            {
                coverageGaps.Add(new JsonObject { ["path"] = pointer, ["leftPresent"] = beforePresent, ["rightPresent"] = afterPresent });
                continue;
            }
            if (!Equal(before, after))
                differences.Add(new JsonObject { ["path"] = pointer, ["before"] = before?.DeepClone(), ["after"] = after?.DeepClone() });
        }
        return new JsonObject { ["equal"] = differences.Count == 0 && coverageGaps.Count == 0, ["differences"] = differences,
            ["conclusive"] = coverageGaps.Count == 0, ["coverageGaps"] = coverageGaps,
            ["scope"] = "explicit-fields", ["comparedFields"] = pointers.Length };
    }
}
