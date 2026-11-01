// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Content.Meta.Mui;

/// <summary>Strict external application contract v1.0. Unknown metadata is rejected rather than ignored.</summary>
public static class MuiContractParser
{
    public static bool TryParse(string json, out MuiAppContract? contract, out string error)
    {
        contract = null;
        error = "MUI1201";
        if (Encoding.UTF8.GetByteCount(json) > MuiLimits.InitialStateBytes)
            return false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            var root = doc.RootElement;
            var budget = MuiLimits.Nodes;
            if (root.ValueKind != JsonValueKind.Object || !MuiDataValidator.ValidateBudget(root, ref budget) ||
                !HasOnly(root, "version", "views", "actions", "resources") || !root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.String || version.GetString() != "1.0")
                return false;
            var actions = ImmutableDictionary.CreateBuilder<string, MuiActionContract>();
            var views = ImmutableDictionary.CreateBuilder<string, MuiViewContract>();
            var resources = ImmutableHashSet.CreateBuilder<string>();
            if (!root.TryGetProperty("actions", out var actionObject) || actionObject.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("views", out var viewObject) || viewObject.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var action in actionObject.EnumerateObject())
            {
                if (!MuiBinding.IsPath(action.Name) || action.Value.ValueKind != JsonValueKind.Object ||
                    !HasOnly(action.Value, "function", "inputs", "scopes", "inputType") || !GetString(action.Value, "function", out var function) ||
                    !TryFields(action.Value, "inputs", out var inputs) || !TryStrings(action.Value, "scopes", out var scopes))
                    return false;
                var inputType = action.Value.TryGetProperty("inputType", out _) ? GetString(action.Value, "inputType", out var input) ? input : null : null;
                if (action.Value.TryGetProperty("inputType", out _) && inputType is null)
                    return false;
                actions.Add(action.Name, new(function, inputs!, scopes, inputType));
            }
            foreach (var view in viewObject.EnumerateObject())
            {
                if (!MuiBinding.IsPath(view.Name) || view.Value.ValueKind != JsonValueKind.Object ||
                    !HasOnly(view.Value, "function", "document", "fields", "actions", "stateType") || !GetString(view.Value, "function", out var function) ||
                    !GetString(view.Value, "document", out var document) || !IsPackagePath(document, ".mui") ||
                    !TryFields(view.Value, "fields", out var fields) || !TryStrings(view.Value, "actions", out var viewActions) ||
                    viewActions.Any(x => !actions.ContainsKey(x)))
                    return false;
                var stateType = view.Value.TryGetProperty("stateType", out _) ? GetString(view.Value, "stateType", out var state) ? state : null : null;
                if (view.Value.TryGetProperty("stateType", out _) && stateType is null)
                    return false;
                views.Add(view.Name, new(function, document, fields!, viewActions, stateType));
            }
            if (root.TryGetProperty("resources", out var resourceArray))
            {
                if (resourceArray.ValueKind != JsonValueKind.Array)
                    return false;
                foreach (var resource in resourceArray.EnumerateArray())
                {
                    if (resource.ValueKind != JsonValueKind.String || !IsPackagePath(resource.GetString()!, null) || !resources.Add(resource.GetString()!))
                        return false;
                }
            }
            if (views.Count is < 1 or > 32 || actions.Count > 128 || resources.Count > 128)
                return false;
            contract = new("1.0", views.ToImmutable(), actions.ToImmutable(), resources.ToImmutable());
            error = "";
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static bool TryFields(JsonElement owner, string name, out ImmutableDictionary<string, MuiField>? fields)
    {
        fields = null;
        if (!owner.TryGetProperty(name, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return false;
        var result = ImmutableDictionary.CreateBuilder<string, MuiField>();
        foreach (var field in obj.EnumerateObject())
        {
            if (!MuiBinding.IsPath(field.Name) || field.Value.ValueKind != JsonValueKind.Object ||
                !HasOnly(field.Value, "type", "required", "maxLength", "min", "max") || !GetString(field.Value, "type", out var type) ||
                !Enum.TryParse<MuiValueKind>(type, true, out var kind) || !Enum.IsDefined(kind) || !type.All(char.IsAsciiLetter))
                return false;
            var required = true;
            var length = MuiLimits.StringLength;
            double? min = null;
            double? max = null;
            if (field.Value.TryGetProperty("required", out var req))
            {
                if (req.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return false;
                required = req.GetBoolean();
            }
            if (field.Value.TryGetProperty("maxLength", out var len) &&
                (len.ValueKind != JsonValueKind.Number || !len.TryGetInt32(out length) || length is < 0 or > MuiLimits.StringLength))
                return false;
            if (field.Value.TryGetProperty("min", out var lower))
            {
                if (lower.ValueKind != JsonValueKind.Number || !lower.TryGetDouble(out var number) || !double.IsFinite(number))
                    return false;
                min = number;
            }
            if (field.Value.TryGetProperty("max", out var upper))
            {
                if (upper.ValueKind != JsonValueKind.Number || !upper.TryGetDouble(out var number) || !double.IsFinite(number))
                    return false;
                max = number;
            }
            if (min.HasValue && max.HasValue && min > max)
                return false;
            result.Add(field.Name, new(kind, required, length, min, max));
        }
        fields = result.ToImmutable();
        return result.Count <= 256;
    }

    private static bool GetString(JsonElement owner, string name, out string value)
    {
        value = "";
        if (!owner.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String)
            return false;
        value = field.GetString()!;
        return value.Length is > 0 and <= 256;
    }

    private static bool TryStrings(JsonElement owner, string name, out ImmutableArray<string> values)
    {
        values = [];
        if (!owner.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Array || field.GetArrayLength() > 128)
            return false;
        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var item in field.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString()!.Length is < 1 or > 128 || builder.Contains(item.GetString()!))
                return false;
            builder.Add(item.GetString()!);
        }
        values = builder.ToImmutable();
        return true;
    }

    private static bool IsPackagePath(string value, string? extension) => value.Length is > 0 and <= 256 &&
        !value.StartsWith('/') && !value.Contains('\\') && !value.Contains(':') && !value.Split('/').Any(x => x is "" or "." or "..") &&
        (extension is null || value.EndsWith(extension, StringComparison.Ordinal));

    private static bool HasOnly(JsonElement obj, params string[] names) => obj.EnumerateObject().All(x => names.Contains(x.Name));
}
