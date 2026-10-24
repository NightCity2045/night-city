// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Xml;

namespace Content.Meta.Mui;

/// <summary>Streaming, bounded, namespace-free XML dialect. Does not instantiate CLR types or load resources.</summary>
public static class MuiParser
{
    private static readonly HashSet<string> Common = ["id", "class", "visible", "enabled", "width", "height", "minWidth", "maxWidth", "minHeight", "maxHeight", "margin", "tooltip"];
    private static readonly Dictionary<MuiControlKind, string[]> Properties = new()
    {
        [MuiControlKind.Window] = ["title"], [MuiControlKind.Page] = ["title"],
        [MuiControlKind.Row] = ["gap"], [MuiControlKind.Column] = ["gap"],
        [MuiControlKind.Grid] = ["columns", "gap"], [MuiControlKind.Panel] = [],
        [MuiControlKind.Text] = ["text"], [MuiControlKind.Header] = ["text"],
        [MuiControlKind.Button] = ["text", "action"], [MuiControlKind.Form] = ["action"],
        [MuiControlKind.Input] = ["value", "field", "placeholder", "maxLength"],
        [MuiControlKind.NumberInput] = ["value", "field", "min", "max", "step"],
        [MuiControlKind.Select] = ["value", "field"], [MuiControlKind.Option] = ["value", "text"],
        [MuiControlKind.Toggle] = ["value", "field", "text"],
        [MuiControlKind.Tabs] = [], [MuiControlKind.Tab] = ["title"], [MuiControlKind.Scroll] = [],
        [MuiControlKind.Dialog] = ["title"], [MuiControlKind.List] = ["items", "page", "pageSize", "pageAction"],
        [MuiControlKind.Table] = ["items", "page", "pageSize", "pageAction", "sortAction", "filterAction", "rowAction", "rowKey", "rowField", "columns"],
        [MuiControlKind.Validation] = ["text", "field"], [MuiControlKind.Image] = ["resource"],
        [MuiControlKind.Chart] = ["items", "min", "max"], [MuiControlKind.Map] = ["items"],
        [MuiControlKind.Schematic] = ["items"],
        [MuiControlKind.Component] = ["resource"],
    };
    private static readonly HashSet<string> Bindable = ["text", "title", "value", "visible", "enabled", "items", "tooltip", "page"];
    private static readonly HashSet<string> Numeric = ["width", "height", "minWidth", "maxWidth", "minHeight", "maxHeight", "margin", "gap", "columns", "maxLength", "min", "max", "step", "page", "pageSize"];

    public static bool TryParse(string source, MuiViewContract view, MuiAppContract contract,
        out ValidatedMuiDocument? document, out MuiDiagnostic? diagnostic)
        => TryParse(source, view, contract, null, out document, out diagnostic);

    public static bool TryParse(string source, MuiViewContract view, MuiAppContract contract,
        IReadOnlyDictionary<string, string>? resources, out ValidatedMuiDocument? document, out MuiDiagnostic? diagnostic)
    {
        if (!TryParseRaw(source, view, contract, out document, out diagnostic)) return false;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var expandedIds = new HashSet<string>(StringComparer.Ordinal);
        var nodes = 0;
        var charts = 0;
        var root = Expand(document!.Root, "", 0);
        if (root is null || NativeControlCost(root) > MuiLimits.Controls)
        {
            document = null;
            diagnostic ??= new("MUI0016", 0, 0, "component resources, expansion or native budget");
            return false;
        }
        document = new(root, nodes);
        return true;

        MuiNode? Expand(MuiNode node, string prefix, int depth)
        {
            if (++nodes > MuiLimits.Nodes || depth >= MuiLimits.Depth || IsPlot(node.Kind) && ++charts > MuiLimits.ChartCount)
                return null;
            var id = prefix + node.Id;
            if (id.Length > 128 || !expandedIds.Add(id)) return null;
            var children = ImmutableArray.CreateBuilder<MuiNode>();
            if (node.Kind == MuiControlKind.Component)
            {
                if (node.Children.Length != 0 || !node.Properties.TryGetValue("resource", out var path) || !path.EndsWith(".mui", StringComparison.Ordinal) ||
                    resources is null || !resources.TryGetValue(path, out var component) || !paths.Add(path) ||
                    !TryParseRaw(component, view, contract, out var parsed, out _))
                    return null;
                var expanded = Expand(parsed!.Root, id + "_", depth + 1);
                paths.Remove(path);
                if (expanded is null) return null;
                children.Add(expanded);
            }
            else foreach (var child in node.Children)
            {
                var expanded = Expand(child, prefix, depth + 1);
                if (expanded is null) return null;
                children.Add(expanded);
            }
            return node with { Id = id, Kind = node.Kind == MuiControlKind.Component ? MuiControlKind.Column : node.Kind,
                Properties = node.Kind == MuiControlKind.Component ? node.Properties.SetItem("id", id).Remove("resource") : node.Properties.SetItem("id", id),
                Children = children.ToImmutable() };
        }
    }

    private static bool TryParseRaw(string source, MuiViewContract view, MuiAppContract contract,
        out ValidatedMuiDocument? document, out MuiDiagnostic? diagnostic)
    {
        document = null;
        diagnostic = null;
        if (Encoding.UTF8.GetByteCount(source) > MuiLimits.DocumentBytes)
        {
            diagnostic = new("MUI0001", 0, 0, "document size");
            return false;
        }
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MuiLimits.DocumentBytes, MaxCharactersFromEntities = 0,
            IgnoreComments = true, IgnoreWhitespace = true, ConformanceLevel = ConformanceLevel.Document,
        };
        using var input = new StringReader(source);
        using var reader = XmlReader.Create(input, settings);
        var stack = new Stack<Builder>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        MuiNode? root = null;
        var count = 0;
        var charts = 0;
        try
        {
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (++count > MuiLimits.Nodes || stack.Count >= MuiLimits.Depth || reader.NamespaceURI.Length != 0 ||
                            !Enum.TryParse<MuiControlKind>(reader.Name, false, out var kind) || !Properties.ContainsKey(kind))
                            return Fail("MUI0002", "control, count or depth", reader, out diagnostic);
                        if (stack.Count == 0 && kind is not (MuiControlKind.Window or MuiControlKind.Page))
                            return Fail("MUI0003", "root", reader, out diagnostic);
                        if (IsPlot(kind) && ++charts > MuiLimits.ChartCount)
                            return Fail("MUI0004", "chart count", reader, out diagnostic);
                        var builder = new Builder(kind);
                        if (reader.AttributeCount > 24)
                            return Fail("MUI0005", "attribute count", reader, out diagnostic);
                        while (reader.MoveToNextAttribute())
                        {
                            var name = reader.Name;
                            var value = reader.Value;
                            if (name == "resource" && kind == MuiControlKind.Image && !MuiResourceCatalog.IsImage(value))
                                return Fail("MUI0008", name, reader, out diagnostic);
                            if (reader.NamespaceURI.Length != 0 || (!Common.Contains(name) && !Properties[kind].Contains(name)) || value.Length > MuiLimits.StringLength)
                                return Fail("MUI0006", name, reader, out diagnostic);
                            if (value.StartsWith('{'))
                            {
                                if (!Bindable.Contains(name) || !MuiBinding.TryParse(value, out var binding) ||
                                    !view.Fields.TryGetValue(binding!.Path, out var field) || !AcceptsBinding(name, field.Kind, binding))
                                    return Fail("MUI0007", name, reader, out diagnostic);
                                builder.Bindings.Add(name, binding!);
                            }
                            else if (!ValidateLiteral(kind, name, value, view, contract))
                                return Fail("MUI0008", name, reader, out diagnostic);
                            builder.Properties.Add(name, value);
                        }
                        reader.MoveToElement();
                        var id = builder.Properties.GetValueOrDefault("id");
                        if (id is null || !MuiBinding.IsPath(id) || id.Contains('.') || !ids.Add(id))
                            return Fail("MUI0009", "stable id", reader, out diagnostic);
                        if (kind == MuiControlKind.Table && !ValidRowAction(builder.Properties, contract))
                            return Fail("MUI0017", "row action", reader, out diagnostic);
                        if (kind == MuiControlKind.Chart && Metric(builder.Properties, "max", 1) <= Metric(builder.Properties, "min", 0))
                            return Fail("MUI0017", "chart range", reader, out diagnostic);
                        if (stack.TryPeek(out var parent) && !CanContain(parent.Kind, kind))
                            return Fail("MUI0010", "child", reader, out diagnostic);
                        stack.Push(builder);
                        if (reader.IsEmptyElement)
                            Complete(stack, ref root);
                        break;
                    case XmlNodeType.EndElement:
                        Complete(stack, ref root);
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                        if (!stack.TryPeek(out var textNode) || textNode.Kind is not (MuiControlKind.Text or MuiControlKind.Header) || textNode.Properties.ContainsKey("text") || reader.Value.Length > MuiLimits.StringLength)
                            return Fail("MUI0011", "text", reader, out diagnostic);
                        textNode.Properties.Add("text", reader.Value);
                        break;
                    case XmlNodeType.XmlDeclaration:
                        break;
                    default:
                        return Fail("MUI0012", "XML node", reader, out diagnostic);
                }
            }
            if (root is null || stack.Count != 0)
                return Fail("MUI0013", "incomplete", reader, out diagnostic);
            // The parser permits larger documents for future templates; the active native view is explicitly bounded.
            if (NativeControlCost(root) > MuiLimits.Controls)
                return Fail("MUI0014", "active controls", reader, out diagnostic);
            document = new(root, count);
            return true;
        }
        catch (XmlException ex)
        {
            diagnostic = new("MUI0015", ex.LineNumber, ex.LinePosition, "XML");
            return false;
        }
    }

    private static int NativeControlCost(MuiNode node)
    {
        // Include native internal children and detached option popup widgets before any allocation.
        var own = node.Kind switch
        {
            MuiControlKind.Button => 2,
            MuiControlKind.Toggle => 4,
            MuiControlKind.Select => 12 + node.Children.Length * 2,
            MuiControlKind.Scroll => 3,
            MuiControlKind.List or MuiControlKind.Table => 64,
            _ => 1,
        };
        return own + (node.Kind == MuiControlKind.Select ? 0 : node.Children.Sum(NativeControlCost));
    }

    private static bool AcceptsBinding(string property, MuiValueKind kind, MuiBinding binding)
    {
        if (property is "visible" or "enabled")
            return kind == MuiValueKind.Boolean && binding.Format is null;
        if (property == "page")
            return kind == MuiValueKind.Integer && binding.Format is null && !binding.Negated;
        if (binding.Negated)
            return false;
        return property == "items" ? kind == MuiValueKind.Array && binding.Format is null : kind is not (MuiValueKind.Array or MuiValueKind.Object);
    }

    private static bool ValidateLiteral(MuiControlKind kind, string name, string value, MuiViewContract view, MuiAppContract contract)
    {
        if (name is "action" or "pageAction" or "sortAction" or "filterAction" or "rowAction")
            return contract.Actions.ContainsKey(value) && view.Actions.Contains(value);
        if (name == "resource")
            return contract.Resources.Contains(value) && !value.Contains("..", StringComparison.Ordinal) && !value.Contains(':');
        if (name is "visible" or "enabled")
            return value is "true" or "false";
        if (name is "field" or "rowField")
            return MuiBinding.IsPath(value) && view.Actions.Any(x => contract.Actions.TryGetValue(x, out var a) && a.Inputs.ContainsKey(value));
        if (name == "rowKey")
            return MuiBinding.IsPath(value) && !value.Contains('.');
        if (name == "columns" && kind == MuiControlKind.Table)
        {
            var columns = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return columns.Length is >= 1 and <= 16 && columns.Distinct(StringComparer.Ordinal).Count() == columns.Length &&
                columns.All(x => MuiBinding.IsPath(x) && !x.Contains('.'));
        }
        if (Numeric.Contains(name))
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) &&
                Math.Abs(number) <= 65536 && (name is "min" or "max" || number >= 0) &&
                (name != "pageSize" || number is >= 1 and <= MuiLimits.PageRows) && (name != "columns" || number is >= 1 and <= 16);
        return name != "class" || value.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(x => MuiBinding.IsPath(x) && !x.Contains('.'));
    }

    private static bool IsPlot(MuiControlKind kind) => kind is MuiControlKind.Chart or MuiControlKind.Map or MuiControlKind.Schematic;
    private static double Metric(IReadOnlyDictionary<string, string> properties, string key, double fallback) =>
        properties.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    private static bool ValidRowAction(IReadOnlyDictionary<string, string> properties, MuiAppContract contract)
    {
        var count = new[] { "rowAction", "rowKey", "rowField" }.Count(properties.ContainsKey);
        if (count == 0) return true;
        return count == 3 && properties.TryGetValue("columns", out var columns) &&
            !columns.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(properties["rowKey"], StringComparer.Ordinal) &&
            contract.Actions[properties["rowAction"]].Inputs.TryGetValue(properties["rowField"], out var field) && field.Kind == MuiValueKind.String;
    }

    private static bool CanContain(MuiControlKind parent, MuiControlKind child)
    {
        if (parent == MuiControlKind.Select)
            return child == MuiControlKind.Option;
        if (parent == MuiControlKind.Tabs)
            return child == MuiControlKind.Tab;
        if (child is MuiControlKind.Option or MuiControlKind.Tab) return false;
        return parent is MuiControlKind.Window or MuiControlKind.Page or MuiControlKind.Row or MuiControlKind.Column or MuiControlKind.Grid or
            MuiControlKind.Panel or MuiControlKind.Form or MuiControlKind.Tab or MuiControlKind.Scroll or MuiControlKind.Dialog;
    }

    private static void Complete(Stack<Builder> stack, ref MuiNode? root)
    {
        var node = stack.Pop().Build();
        if (stack.TryPeek(out var parent))
            parent.Children.Add(node);
        else
            root = node;
    }

    private static bool Fail(string code, string detail, XmlReader reader, out MuiDiagnostic? diagnostic)
    {
        var line = (IXmlLineInfo) reader;
        diagnostic = new(code, line.LineNumber, line.LinePosition, detail);
        return false;
    }

    private sealed class Builder(MuiControlKind kind)
    {
        public MuiControlKind Kind = kind;
        public Dictionary<string, string> Properties = new(StringComparer.Ordinal);
        public Dictionary<string, MuiBinding> Bindings = new(StringComparer.Ordinal);
        public List<MuiNode> Children = [];
        public MuiNode Build() => new(Properties["id"], Kind, Properties.ToImmutableDictionary(), Bindings.ToImmutableDictionary(), [.. Children]);
    }
}
