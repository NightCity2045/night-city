// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Content.Meta.Mui;

public sealed record MuiStyleRule(string Selector, string State, int MinWidth, int MaxWidth,
    ImmutableDictionary<string, string> Properties);

/// <summary>Restricted mstyle JSON schema. Selectors are local #node or .class; never native type names.</summary>
public sealed class MuiStyleSheet
{
    public ImmutableArray<MuiStyleRule> Rules { get; }
    public static MuiStyleSheet Empty { get; } = new([]);
    private MuiStyleSheet(ImmutableArray<MuiStyleRule> rules) { Rules = rules; }

    public static bool TryParse(string json, MuiAppContract contract, out MuiStyleSheet? sheet, out string error)
    {
        sheet = null;
        error = "MUI2001";
        if (Encoding.UTF8.GetByteCount(json) > MuiLimits.DocumentBytes)
            return false;
        if (string.IsNullOrWhiteSpace(json))
        {
            sheet = Empty;
            error = "";
            return true;
        }
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            var budget = MuiLimits.Nodes;
            if (root.ValueKind != JsonValueKind.Object || !MuiDataValidator.ValidateBudget(root, ref budget) ||
                !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "1.0")
                return false;
            var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
            var rules = ImmutableArray.CreateBuilder<MuiStyleRule>();
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name == "version")
                {
                    if (property.Value.GetString() != "1.0")
                        return false;
                }
                else if (property.Name == "tokens" && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var token in property.Value.EnumerateObject())
                    {
                        if (!MuiBinding.IsPath(token.Name) || token.Value.ValueKind != JsonValueKind.String || !tokens.TryAdd(token.Name, token.Value.GetString()!))
                            return false;
                    }
                }
                else if (property.Name != "rules")
                    return false;
            }
            if (root.TryGetProperty("rules", out var ruleArray))
            {
                if (ruleArray.ValueKind != JsonValueKind.Array || ruleArray.GetArrayLength() > 256)
                    return false;
                foreach (var item in ruleArray.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        return false;
                    var selector = "";
                    var state = "normal";
                    var min = 0;
                    var max = 65536;
                    var properties = ImmutableDictionary.CreateBuilder<string, string>();
                    foreach (var field in item.EnumerateObject())
                    {
                        switch (field.Name)
                        {
                            case "selector" when field.Value.ValueKind == JsonValueKind.String: selector = field.Value.GetString()!; break;
                            case "state" when field.Value.ValueKind == JsonValueKind.String: state = field.Value.GetString()!; break;
                            case "minWidth" when field.Value.TryGetInt32(out var width): min = width; break;
                            case "maxWidth" when field.Value.TryGetInt32(out var width): max = width; break;
                            case "properties" when field.Value.ValueKind == JsonValueKind.Object:
                                foreach (var value in field.Value.EnumerateObject())
                                {
                                    if (value.Value.ValueKind != JsonValueKind.String)
                                        return false;
                                    var text = value.Value.GetString()!;
                                    if (text.StartsWith('$') && !tokens.TryGetValue(text[1..], out text))
                                        return false;
                                    if (!ValidateProperty(value.Name, text!, contract) || !properties.TryAdd(value.Name, text!))
                                        return false;
                                }
                                break;
                            default: return false;
                        }
                    }
                    if (selector.Length < 2 || selector[0] is not ('#' or '.') || !MuiBinding.IsPath(selector[1..]) || selector[1..].Contains('.') ||
                        state is not ("normal" or "hover" or "focus" or "disabled" or "pressed") || min < 0 || max < min || max > 65536)
                        return false;
                    rules.Add(new(selector, state, min, max, properties.ToImmutable()));
                }
            }
            sheet = new(rules.ToImmutable());
            error = "";
            return true;
        }
        catch (JsonException) { return false; }
        catch (ArgumentException exception) when (exception.InnerException is EncoderFallbackException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool ValidateProperty(string name, string value, MuiAppContract contract)
    {
        if (name == "fontSize")
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size is >= 8 and <= 48;
        if (name == "gradient")
        {
            var stops = value.Split(',');
            return stops.Length == 2 && stops.All(x => ValidateProperty("background", x, contract));
        }
        if (name == "animation")
        {
            var parts = value.Split(':');
            return parts.Length == 4 && parts[0] == "pulse" &&
                int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var duration) && duration is >= 100 and <= 10000 &&
                float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var from) && float.IsFinite(from) && from is >= 0 and <= 1 &&
                float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var to) && float.IsFinite(to) && to is >= 0 and <= 1;
        }
        if (name is "color" or "background" or "borderColor")
            return value.Length is 7 or 9 && value[0] == '#' && value[1..].All(Uri.IsHexDigit);
        if (name is "font" or "image")
            return contract.Resources.Contains(value) && (name == "font" ? MuiResourceCatalog.IsFont(value) : MuiResourceCatalog.IsImage(value));
        if (name is "padding" or "margin" or "gap" or "borderWidth" or "fontSize" or "opacity" or "cornerRadius")
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && float.IsFinite(number) &&
                number is >= 0 and <= 256 && (name != "opacity" || number <= 1);
        return false;
    }

    /// <summary>Conservatively admits at most sixteen animated nodes across all local states and breakpoints.</summary>
    public bool FitsBudget(ValidatedMuiDocument document)
    {
        var animated = Rules.Where(x => x.Properties.ContainsKey("animation")).ToArray();
        if (animated.Length == 0) return true;
        var pending = new Stack<MuiNode>();
        pending.Push(document.Root);
        var count = 0;
        while (pending.TryPop(out var node))
        {
            var classes = node.Properties.GetValueOrDefault("class", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (animated.Any(x => x.Selector[0] == '#' ? x.Selector[1..] == node.Id : classes.Contains(x.Selector[1..])) && ++count > MuiLimits.Animations)
                return false;
            foreach (var child in node.Children) pending.Push(child);
        }
        return true;
    }
}
