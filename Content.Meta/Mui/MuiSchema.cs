// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Content.Meta.Mui;

public static class MuiLimits
{
    public const int DocumentBytes = 256 * 1024;
    public const int Nodes = 2048;
    public const int Depth = 32;
    public const int Controls = 512;
    public const int InitialStateBytes = 64 * 1024;
    public const int PatchBytes = 16 * 1024;
    public const int ActionBytes = 8 * 1024;
    public const int ClientSessions = 4;
    public const int InstallationSessions = 64;
    public const int ChartCount = 8;
    public const int ChartPoints = 512;
    public const int Animations = 16;
    public const int PageRows = 64;
    public const int StringLength = 4096;
}

public enum MuiValueKind : byte { String, Integer, Number, Boolean, Object, Array }
public enum MuiControlKind : byte
{
    Window, Page, Row, Column, Grid, Panel, Text, Header, Button, Form, Input, NumberInput,
    Select, Option, Toggle, Tabs, Tab, Scroll, Dialog, List, Table, Validation, Image,
    Chart, Map, Schematic, Component,
}

public sealed record MuiField(MuiValueKind Kind, bool Required = true, int MaxLength = MuiLimits.StringLength,
    double? Minimum = null, double? Maximum = null);
public sealed record MuiActionContract(string Function, ImmutableDictionary<string, MuiField> Inputs,
    ImmutableArray<string> Scopes, string? InputType = null);
public sealed record MuiViewContract(string Function, string Document,
    ImmutableDictionary<string, MuiField> Fields, ImmutableArray<string> Actions, string? StateType = null);
public sealed record MuiExportSignature(string Name, bool Public, ImmutableArray<string> Parameters, string ReturnType);

/// <summary>External package metadata; does not add sections to bytecode v1 or confer grants.</summary>
public sealed record MuiAppContract(
    string Version,
    ImmutableDictionary<string, MuiViewContract> Views,
    ImmutableDictionary<string, MuiActionContract> Actions,
    ImmutableHashSet<string> Resources)
{
    public static readonly MuiAppContract Empty = new("1.0", ImmutableDictionary<string, MuiViewContract>.Empty,
        ImmutableDictionary<string, MuiActionContract>.Empty, ImmutableHashSet<string>.Empty);

    public bool TryValidateExports(IEnumerable<MuiExportSignature> exports, out string error)
    {
        var signatures = new Dictionary<string, MuiExportSignature>(StringComparer.Ordinal);
        foreach (var signature in exports)
        {
            if (!signatures.TryAdd(signature.Name, signature))
            {
                error = "MUI1003";
                return false;
            }
        }
        foreach (var view in Views.Values)
        {
            if (!signatures.TryGetValue(view.Function, out var sig) || !sig.Public ||
                !sig.Parameters.SequenceEqual(["meta.app.AppContext"]) || sig.ReturnType != (view.StateType ?? "ViewState"))
            {
                error = "MUI1001";
                return false;
            }
        }
        foreach (var action in Actions.Values)
        {
            if (!signatures.TryGetValue(action.Function, out var sig) || !sig.Public ||
                !sig.Parameters.SequenceEqual(["meta.app.AppContext", action.InputType ?? "ActionInput"]) || sig.ReturnType != "result<void>")
            {
                error = "MUI1002";
                return false;
            }
        }
        error = "";
        return true;
    }
}

public sealed record MuiDiagnostic(string Code, int Line, int Column, string Detail);
public sealed record MuiBinding(string Path, string? Format, bool Negated = false)
{
    private static readonly Regex PathPattern = new("^[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*){0,7}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool IsPath(string path) => path.Length <= 128 && PathPattern.IsMatch(path);

    public static bool TryParse(string text, out MuiBinding? binding)
    {
        binding = null;
        if (text.Length > 192 || !text.StartsWith("{bind ", StringComparison.Ordinal) || !text.EndsWith('}'))
            return false;
        var expression = text[6..^1].Split('|', StringSplitOptions.TrimEntries);
        if (expression.Length > 2)
            return false;
        var path = expression[0];
        var negated = path.StartsWith('!');
        if (negated)
            path = path[1..];
        if (!IsPath(path))
            return false;
        var format = expression.Length == 2 ? expression[1] : null;
        if (format is not null && format is not ("upper" or "lower" or "number" or "number:0" or "number:1" or "number:2" or "percent"))
            return false;
        binding = new(path, format, negated);
        return true;
    }
}

public sealed record MuiNode(string Id, MuiControlKind Kind,
    ImmutableDictionary<string, string> Properties,
    ImmutableDictionary<string, MuiBinding> Bindings,
    ImmutableArray<MuiNode> Children);

/// <summary>Only a successful allowlist parser can create a renderable document.</summary>
public sealed class ValidatedMuiDocument
{
    public MuiNode Root { get; }
    public int NodeCount { get; }
    internal ValidatedMuiDocument(MuiNode root, int nodeCount) { Root = root; NodeCount = nodeCount; }
}

public static class MuiDataValidator
{
    public static bool TryValidate(string json, IReadOnlyDictionary<string, MuiField> schema, int maxBytes, out string error)
    {
        error = "MUI1101";
        if (Encoding.UTF8.GetByteCount(json) > maxBytes)
            return false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MuiLimits.Depth });
            var root = doc.RootElement;
            var budget = MuiLimits.Nodes;
            if (root.ValueKind != JsonValueKind.Object || !ValidateBudget(root, ref budget))
                return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in root.EnumerateObject())
                if (!seen.Add(prop.Name) || !schema.TryGetValue(prop.Name, out var field) || !Matches(prop.Value, field))
                    return false;
            foreach (var field in schema)
                if (field.Value.Required && !seen.Contains(field.Key))
                    return false;
            error = "";
            return true;
        }
        catch (JsonException) { return false; }
        catch (ArgumentException exception) when (exception.InnerException is EncoderFallbackException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    internal static bool ValidateBudget(JsonElement element, ref int budget)
    {
        if (--budget < 0)
            return false;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in element.EnumerateObject())
                    if (item.Name.Length > 128 || !names.Add(item.Name) || !ValidateBudget(item.Value, ref budget))
                        return false;
                break;
            case JsonValueKind.Array:
                if (element.GetArrayLength() > MuiLimits.ChartPoints)
                    return false;
                foreach (var item in element.EnumerateArray())
                    if (!ValidateBudget(item, ref budget))
                        return false;
                break;
            case JsonValueKind.String: return element.GetString()!.Length <= MuiLimits.StringLength;
            case JsonValueKind.Number: return element.TryGetDouble(out var number) && double.IsFinite(number);
        }
        return true;
    }

    internal static bool Matches(JsonElement value, MuiField field)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return !field.Required;
        switch (field.Kind)
        {
            case MuiValueKind.String: return value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= field.MaxLength;
            case MuiValueKind.Boolean: return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case MuiValueKind.Integer:
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer) && InRange(integer, field);
            case MuiValueKind.Number:
                return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && InRange(number, field);
            case MuiValueKind.Object: return value.ValueKind == JsonValueKind.Object;
            case MuiValueKind.Array: return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= field.MaxLength;
            default: return false;
        }
    }

    private static bool InRange(double value, MuiField field) =>
        (!field.Minimum.HasValue || value >= field.Minimum.Value) && (!field.Maximum.HasValue || value <= field.Maximum.Value);
}
