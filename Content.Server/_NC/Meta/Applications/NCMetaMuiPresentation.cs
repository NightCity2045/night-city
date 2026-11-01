// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using System.Text.Json;
using Content.Meta.Mui;
using Content.Shared._NC.Meta.UI;

namespace Content.Server._NC.Meta.Applications;

/// <summary>Only independently admitted application data crosses to the renderer; no compiler or VM objects cross.</summary>
internal static class NCMetaMuiPresentationBuilder
{
    public static NCMetaMuiPresentation Build(MuiVerifiedApplication application, string viewId, NCMetaMuiPresentation? schema = null)
    {
        var view = application.Views[viewId];
        return Build(application.Contract, view.Document, application.Styles, schema);
    }

    public static NCMetaMuiPresentation Build(MuiAppContract contract, ValidatedMuiDocument document, MuiStyleSheet styles,
        NCMetaMuiPresentation? schema = null)
    {
        var remaining = MuiLimits.Nodes;
        var result = new NCMetaMuiPresentation
        {
            Version = contract.Version,
            Root = Node(document.Root, 0, ref remaining),
            NodeCount = document.NodeCount,
            Resources = schema?.Resources ?? contract.Resources.ToList(),
            Styles = schema?.Styles ?? styles.Rules.Select(rule => new NCMetaMuiStyleRule(rule.Selector, rule.State,
                rule.MinWidth, rule.MaxWidth, rule.Properties.ToDictionary(pair => pair.Key, pair => pair.Value))).ToList(),
        };
        if (schema != null)
        {
            result.Views = schema.Views; result.Actions = schema.Actions;
            result.Resources = schema.Resources; result.Styles = schema.Styles;
            return result;
        }
        foreach (var pair in contract.Views)
            result.Views.Add(pair.Key, new(pair.Value.Function, pair.Value.Document,
                Fields(pair.Value.Fields), pair.Value.Actions.ToList(), pair.Value.StateType));
        foreach (var pair in contract.Actions)
            result.Actions.Add(pair.Key, new(pair.Value.Function, Fields(pair.Value.Inputs), pair.Value.Scopes.ToList(), pair.Value.InputType));
        return result;
    }

    private static Dictionary<string, NCMetaMuiField> Fields(IReadOnlyDictionary<string, MuiField> fields) =>
        fields.ToDictionary(pair => pair.Key, pair => new NCMetaMuiField((NCMetaMuiValueKind) pair.Value.Kind,
            pair.Value.Required, pair.Value.MaxLength, pair.Value.Minimum, pair.Value.Maximum));

    private static NCMetaMuiNode Node(MuiNode node, int depth, ref int remaining)
    {
        if (depth >= MuiLimits.Depth || --remaining < 0) throw new InvalidOperationException("Invalid admitted MUI tree.");
        var result = new NCMetaMuiNode
        {
            Id = node.Id, Kind = (NCMetaMuiControlKind) node.Kind,
            Properties = node.Properties.ToDictionary(pair => pair.Key, pair => pair.Value),
            Bindings = node.Bindings.ToDictionary(pair => pair.Key,
                pair => new NCMetaMuiBinding(pair.Value.Path, pair.Value.Format, pair.Value.Negated)),
        };
        foreach (var child in node.Children) result.Children.Add(Node(child, depth + 1, ref remaining));
        return result;
    }

    public static NCMetaUiValue ReadValues(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MuiLimits.Depth });
        var remaining = MuiLimits.Nodes;
        return Value(document.RootElement, 0, ref remaining);
    }

    public static long ValueMemory(NCMetaUiValue? value) => value switch
    {
        NCMetaUiScalar scalar => 96L + scalar.Text.Length * 2L,
        NCMetaUiObject obj => 128L + obj.Sum(pair => 64L + pair.Key.Length * 2L + ValueMemory(pair.Value)),
        NCMetaUiArray array => 64L + array.Sum(item => 8L + ValueMemory(item)),
        _ => 0,
    };

    private static NCMetaUiValue Value(JsonElement value, int depth, ref int remaining)
    {
        if (depth > MuiLimits.Depth || --remaining < 0) throw new InvalidOperationException("Invalid admitted MUI values.");
        NCMetaUiValue result;
        var scalar = new NCMetaUiScalar();
        switch (value.ValueKind)
        {
            case JsonValueKind.Null: result = scalar; break;
            case JsonValueKind.String:
                scalar.ValueKind = NCMetaUiValueKind.String; scalar.Text = value.GetString()!;
                if (scalar.Text.Length > MuiLimits.StringLength) throw new InvalidOperationException();
                result = scalar;
                break;
            case JsonValueKind.True: case JsonValueKind.False:
                scalar.ValueKind = NCMetaUiValueKind.Boolean; scalar.Boolean = value.GetBoolean(); result = scalar; break;
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var integer)) { scalar.ValueKind = NCMetaUiValueKind.Integer; scalar.Integer = integer; }
                else { scalar.ValueKind = NCMetaUiValueKind.Number; scalar.Number = value.GetDouble(); if (!double.IsFinite(scalar.Number)) throw new InvalidOperationException(); }
                result = scalar;
                break;
            case JsonValueKind.Object:
                var obj = new NCMetaUiObject();
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Name.Length > 128 || obj.Fields.ContainsKey(property.Name)) throw new InvalidOperationException();
                    obj.Fields.Add(property.Name, Value(property.Value, depth + 1, ref remaining));
                }
                result = obj; break;
            case JsonValueKind.Array:
                var array = new NCMetaUiArray();
                if (value.GetArrayLength() > MuiLimits.ChartPoints) throw new InvalidOperationException();
                foreach (var item in value.EnumerateArray()) array.Items.Add(Value(item, depth + 1, ref remaining));
                result = array; break;
            default: throw new InvalidOperationException("Invalid admitted MUI value kind.");
        }
        return result;
    }
}
